using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32;

// Aliasi
using MessageBox       = System.Windows.MessageBox;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using Clipboard        = System.Windows.Clipboard;
using CheckBox         = System.Windows.Controls.CheckBox;
using Orientation      = System.Windows.Controls.Orientation;

namespace UltraVideoEditor
{
    /// <summary>
    /// Code-behind za Phase3Dialog.
    /// Orkestrira: TransitionEngine, SmartAudioMixer,
    ///             AccessibilityReportGenerator, ExportPipeline.
    /// </summary>
    public partial class Phase3Dialog : Window
    {
        // ── State ─────────────────────────────────────────────────────
        private HighlightResult           _result;
        private string                    _sourceVideoPath;
        private string                    _musicPath;
        private CancellationTokenSource   _cts;
        private bool                      _running = false;

        // ── Konstruktor ───────────────────────────────────────────────

        public Phase3Dialog(
            HighlightResult result,
            string          sourceVideoPath,
            string          musicPath)
        {
            InitializeComponent();
            UiScaling.Register(this);

            _result          = result;
            _sourceVideoPath = sourceVideoPath;
            _musicPath       = musicPath;

            // Default output folder = Videos\IskraExports\datum
            TxtOutputFolder.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "IskraExports",
                DateTime.Now.ToString("yyyyMMdd_HHmm"));

            // Slider event handlers
            SliderMusicVol.ValueChanged += (_, e)
                => TxtMusicVol.Text = $"{(int)e.NewValue}%";
            SliderClipVol.ValueChanged  += (_, e)
                => TxtClipVol.Text  = $"{(int)e.NewValue}%";
        }

        // ── Folder picker ─────────────────────────────────────────────

        private void BtnPickFolder_Click(object sender, RoutedEventArgs e)
        {
            // WPF nema FolderBrowserDialog — koristimo SaveFileDialog trik
            var dlg = new SaveFileDialog
            {
                Title            = "Select output folder (enter any file name)",
                FileName         = "output_folder",
                Filter           = "Folder|*.none",
                CheckPathExists  = false,
                CheckFileExists  = false,
            };
            if (dlg.ShowDialog() == true)
                TxtOutputFolder.Text = Path.GetDirectoryName(dlg.FileName) ?? TxtOutputFolder.Text;
        }

        // ── Glavna akcija ─────────────────────────────────────────────

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            if (_result == null || !_result.Success)
            {
                MessageBox.Show("No valid HighlightResult.\n" +
                                "Run Phase 1 and 2 before Phase 3.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputFolder = TxtOutputFolder.Text.Trim();
            if (string.IsNullOrEmpty(outputFolder))
            {
                MessageBox.Show("Please select an output folder.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _running          = true;
            _cts              = new CancellationTokenSource();
            BtnRun.IsEnabled  = false;
            BtnClose.Content  = "⏹ Cancel";
            BtnClose.Click   -= BtnClose_Click;
            BtnClose.Click   += BtnCancelRun_Click;
            ProgressPanel.Visibility = Visibility.Visible;

            var progress = new Progress<(int Percent, string Message)>(p =>
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => SetProgress(p.Percent, p.Message));
                    return;
                }
                SetProgress(p.Percent, p.Message);
            });

            try
            {
                await RunPhase3Async(outputFolder, progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetProgress(0, "Cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Phase 3 — Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running         = false;
                _cts?.Dispose();
                _cts             = null;
                BtnRun.IsEnabled = true;
                BtnClose.Content = "Close";
                BtnClose.Click  -= BtnCancelRun_Click;
                BtnClose.Click  += BtnClose_Click;
            }
        }

        private async Task RunPhase3Async(
            string outputFolder,
            IProgress<(int, string)> progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(outputFolder);
            string baseName = $"highlight_{DateTime.Now:yyyyMMdd_HHmm}";

            // ── B: Tranzicije ─────────────────────────────────────────
            progress.Report((5, "B — Analyzing transitions…"));
            List<TransitionDecision> transitions = null;
            if (ChkEnableTransitions.IsChecked == true)
            {
                transitions = TransitionEngine.Decide(_result.Segments, _result.Beats);
                progress.Report((12, $"B — {transitions.Count} prelaza odabrano."));
            }

            // ── A: Audio miks ─────────────────────────────────────────
            AudioMixSettings audioSettings = BuildAudioSettings();
            string mixedAudioPath = null;

            if (!string.IsNullOrEmpty(_musicPath) && File.Exists(_musicPath) &&
                !string.IsNullOrEmpty(_sourceVideoPath) && File.Exists(_sourceVideoPath))
            {
                progress.Report((15, "A — Smart audio miks…"));
                mixedAudioPath = Path.Combine(outputFolder, $"{baseName}_mix.aac");

                var audioProgress = new Progress<(int, string)>(p =>
                {
                    int mapped = 15 + p.Item1 * 25 / 100;
                    progress.Report((mapped, $"A — {p.Item2}"));
                });

                var mixResult = await SmartAudioMixer.MixAsync(
                    videoPath      : _sourceVideoPath,
                    musicPath      : _musicPath,
                    outputPath     : mixedAudioPath,
                    totalDuration  : _result.TotalDuration,
                    settings       : audioSettings,
                    progress       : audioProgress,
                    ct             : ct);

                if (!mixResult.Success)
                {
                    progress.Report((40, $"A — Audio mix failed: {mixResult.Error}. Continuing without the mix."));
                    mixedAudioPath = null;
                }
                else
                {
                    progress.Report((40, $"A — Audio miks OK. LUFS: {mixResult.LUFSLevel:F1}"));
                }
            }

            // ── C: Accessibility Report ───────────────────────────────
            progress.Report((42, "C — Generating accessibility report…"));
            string reportPath = Path.Combine(outputFolder, $"{baseName}_accessibility.txt");

            if (ChkGenReport.IsChecked == true)
            {
                var reportOpts = new AccessibilityReportOptions
                {
                    ProjectName        = baseName,
                    Language           = "sr",
                    IncludeTtsSummary  = ChkTtsSummary.IsChecked == true,
                    IncludeNavMarkers  = ChkNavMarkers.IsChecked == true,
                    IncludeTransitions = transitions?.Count > 0,
                };

                await AccessibilityReportGenerator.GenerateAsync(
                    result        : _result,
                    transitions   : transitions,
                    audioSettings : audioSettings,
                    options       : reportOpts,
                    outputPath    : reportPath,
                    ct            : ct);

                progress.Report((50, "C — Accessibility report generisan."));
            }

            // ── D: Export Pipeline ────────────────────────────────────
            progress.Report((52, "D — Pripremam export pipeline…"));

            var formats = new List<ExportFormat>();
            if (ChkExportYoutube.IsChecked == true)
                formats.Add(new ExportFormat { Id = "youtube", Label = "YouTube FHD",  Enabled = true });
            if (ChkExportReels.IsChecked   == true)
                formats.Add(new ExportFormat { Id = "reels",   Label = "Reels/TikTok", Enabled = true });
            if (ChkExportMp3.IsChecked     == true)
                formats.Add(new ExportFormat { Id = "mp3",     Label = "MP3 Audio",    Enabled = true });
            if (ChkExportReport.IsChecked  == true && ChkGenReport.IsChecked == true)
                formats.Add(new ExportFormat { Id = "report",  Label = "Accessibility",Enabled = true });

            if (formats.Count == 0)
            {
                progress.Report((100, "No export formats selected."));
            }
            else
            {
                var job = new ExportJob
                {
                    SourceVideoPath  = _sourceVideoPath ?? "",
                    MixedAudioPath   = mixedAudioPath,
                    OutputFolder     = outputFolder,
                    BaseName         = baseName,
                    Formats          = formats,
                    HighlightResult  = _result,
                    Transitions      = transitions,
                    AudioSettings    = audioSettings,
                    UseGPU           = ChkUseGPU.IsChecked == true,
                };

                var exportProgress = new Progress<(int, string)>(p =>
                {
                    int mapped = 52 + p.Item1 * 46 / 100;
                    progress.Report((mapped, $"D — {p.Item2}"));
                });

                var exportResult = await ExportPipeline.ExportAllAsync(
                    job, exportProgress, ct);

                progress.Report((100, $"Export complete: {exportResult.SuccessCount}/{formats.Count} formats."));

                // Show result
                ShowExportSummary(exportResult, outputFolder);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private AudioMixSettings BuildAudioSettings() => new AudioMixSettings
        {
            MusicVolume       = SliderMusicVol.Value,
            ClipVolume        = SliderClipVol.Value,
            MusicDuckedVolume = SliderMusicVol.Value * 0.30,
            EnableDucking     = ChkDucking.IsChecked      == true,
            NormalizeLoudness = ChkNormalize.IsChecked    == true,
            MuteOriginalAudio = ChkMuteOriginal.IsChecked == true,
            FadeDuration      = 0.4,
        };

        private void ShowExportSummary(ExportPipelineResult result, string folder)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowExportSummary(result, folder));
                return;
            }

            string icon = result.AllSucceeded ? "✅" : "⚠️";
            string msg  = $"{icon} Export complete!\n\n" +
                          $"Successful: {result.SuccessCount}/{result.Results.Count} formats\n" +
                          $"Folder: {folder}\n\n";

            foreach (var r in result.Results)
            {
                string sz = r.Success && r.FileSizeBytes > 0
                    ? $" ({r.FileSizeBytes / (1024.0 * 1024):F1} MB)"
                    : "";
                msg += $"{(r.Success ? "✅" : "❌")} {r.FormatId}{sz}\n";
            }

            MessageBox.Show(msg, "Phase 3 — Export complete",
                MessageBoxButton.OK,
                result.AllSucceeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void SetProgress(int pct, string msg)
        {
            ProgressBar.Value   = pct;
            TxtProgressPct.Text = $"{pct}%";
            TxtProgressMsg.Text = msg;
        }

        private void BtnCancelRun_Click(object sender, RoutedEventArgs e)
            => _cts?.Cancel();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                var c = MessageBox.Show(
                    "Operation in progress. Cancel and close?",
                    "Potvrda", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (c != MessageBoxResult.Yes) return;
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
