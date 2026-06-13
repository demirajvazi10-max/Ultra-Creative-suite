using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using MessageBox     = System.Windows.MessageBox;
using Button         = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace UltraVideoEditor
{
    public partial class BatchExportDialog : Window
    {
        private readonly ObservableCollection<BatchExportJob> _jobs = new();
        private CancellationTokenSource _cts;
        private bool _running = false;

        public BatchExportDialog()
        {
            InitializeComponent();
            JobsList.ItemsSource = _jobs;
        }

        // ── Dodaj projekte ────────────────────────────────────────────

        private void BtnAddProjects_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title      = "Odaberite Iskra projekte",
                Filter     = "Iskra projekat|*.iskra|Svi fajlovi|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;

            string formatId = GetSelectedFormatId();
            string outFolder = TxtOutputFolder.Text.Trim();

            foreach (string path in dlg.FileNames)
            {
                if (_jobs.Any(j => j.ProjectPath == path)) continue;
                _jobs.Add(new BatchExportJob
                {
                    ProjectPath  = path,
                    OutputFolder = string.IsNullOrEmpty(outFolder) || outFolder == "Nije odabran…"
                                   ? Path.GetDirectoryName(path) ?? ""
                                   : outFolder,
                    FormatId     = formatId,
                });
            }
            UpdateJobCount();
        }

        private void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Odaberite output folder",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TxtOutputFolder.Text = dlg.SelectedPath;
                // Ažuriraj output folder za sve pending jobove
                foreach (var job in _jobs.Where(j => j.Status == BatchJobStatus.Pending))
                    job.OutputFolder = dlg.SelectedPath;
                JobsList.Items.Refresh();
            }
        }

        // ── Uklanjanje ────────────────────────────────────────────────

        private void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (JobsList.SelectedItem is BatchExportJob job && job.Status != BatchJobStatus.Running)
            {
                _jobs.Remove(job);
                UpdateJobCount();
            }
        }

        private void BtnClearDone_Click(object sender, RoutedEventArgs e)
        {
            var done = _jobs.Where(j =>
                j.Status == BatchJobStatus.Done ||
                j.Status == BatchJobStatus.Error ||
                j.Status == BatchJobStatus.Skipped).ToList();
            foreach (var j in done) _jobs.Remove(j);
            UpdateJobCount();
        }

        // ── Batch pokretanje ──────────────────────────────────────────

        private async void BtnStartBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_running) { _cts?.Cancel(); return; }
            if (_jobs.Count == 0) return;

            var pending = _jobs.Where(j => j.Status == BatchJobStatus.Pending).ToList();
            if (pending.Count == 0)
            {
                MessageBox.Show("Nema projekata u čekanju. Dodajte nove ili uklonite završene.",
                    "Batch Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Provjeri output foldere
            foreach (var job in pending)
            {
                if (string.IsNullOrEmpty(job.OutputFolder))
                    job.OutputFolder = Path.GetDirectoryName(job.ProjectPath) ?? "";
            }

            _running             = true;
            _cts                 = new CancellationTokenSource();
            BtnStartBatch.Content = "⏹ Otkaži";
            ProgressPanel.Visibility = Visibility.Visible;
            TotalProgressBar.Value   = 0;
            BtnOpenFolder.IsEnabled  = false;

            int total = pending.Count;
            int done  = 0;

            var progress = new Progress<(int JobIndex, BatchExportJob Job, string Message)>(p =>
            {
                if (!Dispatcher.CheckAccess())
                { Dispatcher.Invoke(() => HandleProgress(p, total, ref done)); return; }
                HandleProgress(p, total, ref done);
            });

            try
            {
                await BatchExportEngine.RunAsync(pending, progress, _cts.Token);
            }
            catch (Exception ex)
            {
                Log($"Greška: {ex.Message}");
            }
            finally
            {
                _running = false;
                _cts?.Dispose(); _cts = null;
                BtnStartBatch.Content = "▶ Pokreni Batch Export";
                ProgressPanel.Visibility = Visibility.Collapsed;
                JobsList.Items.Refresh();
                UpdateJobCount();
                BtnOpenFolder.IsEnabled = !string.IsNullOrEmpty(TxtOutputFolder.Text) &&
                                          TxtOutputFolder.Text != "Nije odabran…";

                int okCount = _jobs.Count(j => j.Status == BatchJobStatus.Done);
                int errCount = _jobs.Count(j => j.Status == BatchJobStatus.Error);
                Log($"Batch završen — ✅ {okCount} gotovo, ❌ {errCount} grešaka.");
                TxtProgressMsg.Text = $"Gotovo: {okCount}/{total}";
                TotalProgressBar.Value = 100;
            }
        }

        private void HandleProgress(
            (int JobIndex, BatchExportJob Job, string Message) p,
            int total, ref int done)
        {
            JobsList.Items.Refresh();
            Log(p.Message);

            if (p.Job.Status == BatchJobStatus.Done ||
                p.Job.Status == BatchJobStatus.Error ||
                p.Job.Status == BatchJobStatus.Skipped)
                done++;

            double overall = total > 0
                ? (done * 100.0 + p.Job.ProgressPct) / total
                : 0;
            TotalProgressBar.Value = Math.Min(overall, 100);
            TxtTotalPct.Text       = $"{(int)overall}%";
            TxtProgressMsg.Text    = p.Message;
        }

        // ── Otvori folder ─────────────────────────────────────────────

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = TxtOutputFolder.Text.Trim();
            if (Directory.Exists(folder))
                Process.Start("explorer.exe", folder);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private string GetSelectedFormatId()
        {
            if (CmbFormat.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "youtube";
            return "youtube";
        }

        private void UpdateJobCount()
        {
            int total   = _jobs.Count;
            int pending = _jobs.Count(j => j.Status == BatchJobStatus.Pending);
            TxtJobCount.Text         = total == 0
                ? "Nema projekata"
                : $"{total} projekata  ·  {pending} u čekanju";
            BtnStartBatch.IsEnabled  = total > 0 && !_running;
        }

        private void Log(string msg)
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}]  {msg}\n");
            TxtLog.ScrollToEnd();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                var r = MessageBox.Show("Batch export je u toku. Otkazati?",
                    "Otkaži", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                _cts?.Cancel();
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnClosed(e);
        }
    }
}
