using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using WpfMessageBox = System.Windows.MessageBox;

namespace UltraVideoEditor
{
    /// <summary>
    /// Dijalog za preuzimanje videa ili audio sa YouTube-a putem yt-dlp.
    /// Integrates directly into the MainWindow timeline.
    /// Accessible for JAWS / NVDA screen readers.
    /// </summary>
    public partial class YouTubeDownloadDialog : Window
    {
        // ─── Javni rezultat ───────────────────────────────────────────────────
        /// <summary>Lista putanja preuzetih fajlova, gotova za dodavanje na timeline.</summary>
        public List<string> DownloadedFiles { get; private set; } = new();

        // ─── Interne promenljive ──────────────────────────────────────────────
        private CancellationTokenSource _cts;
        private bool _downloadInProgress = false;

        // yt-dlp location — searched in PATH, next to .exe, and in AppData
        private static readonly string[] YtdlpSearchPaths = {
            "yt-dlp.exe",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UltraVideoEditor", "yt-dlp.exe")
        };

        // Folder za preuzete fajlove
        private static readonly string DownloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "UltraVideoEditor_Downloads");

        // ─── Konstruktor ──────────────────────────────────────────────────────
        public YouTubeDownloadDialog()
        {
            InitializeComponent();
            Directory.CreateDirectory(DownloadFolder);
            Loaded += (_, _) => txtUrl.Focus();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI EVENT HANDLERS
        // ═════════════════════════════════════════════════════════════════════

        private void TxtUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFetchInfo_Click(sender, e);
        }

        private void FormatType_Changed(object sender, RoutedEventArgs e)
        {
            if (pnlQuality == null) return;
            pnlQuality.Visibility = rbVideo.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void BtnFetchInfo_Click(object sender, RoutedEventArgs e)
        {
            string url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                Announce("Error: URL field is empty.");
                txtUrl.Focus();
                return;
            }

            SetFetchingState(true);
            pnlInfo.Visibility = Visibility.Collapsed;
            btnDownload.IsEnabled = false;

            try
            {
                string ytdlp = FindYtdlp();
                if (ytdlp == null)
                {
                    bool installed = await OfferYtdlpInstallAsync();
                    if (!installed) return;
                    ytdlp = FindYtdlp();
                    if (ytdlp == null) { ShowError("yt-dlp not found even after installation."); return; }
                }

                var info = await FetchInfoAsync(ytdlp, url);
                if (info == null) return;

                // Show info
                txbInfoTitle.Text = info.IsPlaylist
                    ? $"🎵 Plejlista: {info.Title}  ({info.Count} videa)"
                    : $"🎬 {info.Title}";

                txbInfoMeta.Text = info.IsPlaylist
                    ? $"Kanal: {info.Channel}"
                    : $"Kanal: {info.Channel}   Trajanje: {FormatDuration(info.DurationSec)}";

                pnlInfo.Visibility = Visibility.Visible;
                btnDownload.IsEnabled = true;

                string announcement = info.IsPlaylist
                    ? $"Playlist found: {info.Title}, {info.Count} videos."
                    : $"Video found: {info.Title}, duration {FormatDuration(info.DurationSec)}.";
                Announce(announcement);
                btnDownload.Focus();
            }
            catch (Exception ex)
            {
                ShowError($"Error: {ex.Message}");
            }
            finally
            {
                SetFetchingState(false);
            }
        }

        private async void BtnDownload_Click(object sender, RoutedEventArgs e)
        {
            string url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            string ytdlp = FindYtdlp();
            if (ytdlp == null) { ShowError("yt-dlp not found."); return; }

            bool isAudio = rbAudio.IsChecked == true;
            string quality = ((ComboBoxItem)cmbQuality.SelectedItem)?.Tag?.ToString() ?? "best";

            _cts = new CancellationTokenSource();
            SetDownloadState(true);

            try
            {
                var files = await RunDownloadAsync(ytdlp, url, isAudio, quality, _cts.Token);
                DownloadedFiles = files;

                if (files.Count > 0)
                {
                    Announce($"Download complete. {files.Count} file(s) added to timeline.");
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ShowError("Download finished but no files were found.");
                }
            }
            catch (OperationCanceledException)
            {
                Announce("Preuzimanje otkazano.");
                SetProgress(0, "Otkazano.");
            }
            catch (Exception ex)
            {
                ShowError($"Error during download: {ex.Message}");
            }
            finally
            {
                SetDownloadState(false);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadInProgress)
            {
                _cts?.Cancel();
                return;
            }
            DialogResult = false;
            Close();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  YT-DLP LOGIKA
        // ═════════════════════════════════════════════════════════════════════

        private string FindYtdlp()
        {
            foreach (var path in YtdlpSearchPaths)
            {
                if (File.Exists(path)) return path;
            }

            // Also try via PATH
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo("yt-dlp", "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                p.WaitForExit(3000);
                if (p.ExitCode == 0) return "yt-dlp";
            }
            catch { }

            return null;
        }

        private async Task<bool> OfferYtdlpInstallAsync()
        {
            var result = WpfMessageBox.Show(
                "yt-dlp not found on this system.\n\n" +
                "Yt-dlp je besplatan alat neophodan za preuzimanje sa YouTube-a.\n\n" +
                "Would you like to download it automatically? (~10 MB, one time)",
                "yt-dlp not found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return false;

            SetProgress(0, "Preuzimam yt-dlp...");
            pnlProgress.Visibility = Visibility.Visible;

            try
            {
                string dest = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor", "yt-dlp.exe");

                Directory.CreateDirectory(Path.GetDirectoryName(dest));

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "UltraVideoEditor/1.0");

                // Preuzimamo sa GitHub releases
                string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
                Announce("Downloading yt-dlp, please wait...");

                var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? -1;
                using var stream = await response.Content.ReadAsStreamAsync();
                using var file = File.Create(dest);

                var buffer = new byte[65536];
                long downloaded = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0)
                    {
                        double pct = downloaded * 100.0 / total;
                        SetProgress(pct, $"yt-dlp: {pct:F0}%  ({downloaded / 1024 / 1024:F1} MB)");
                    }
                }

                Announce("yt-dlp downloaded successfully.");
                SetProgress(100, "yt-dlp spreman.");
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"Unable to download yt-dlp: {ex.Message}\n\nDownload manually from: https://github.com/yt-dlp/yt-dlp/releases");
                return false;
            }
        }

        // ─── Info ─────────────────────────────────────────────────────────────

        private class VideoInfo
        {
            public string Title   { get; set; } = "";
            public string Channel { get; set; } = "";
            public double DurationSec { get; set; }
            public bool   IsPlaylist  { get; set; }
            public int    Count       { get; set; }
        }

        private async Task<VideoInfo> FetchInfoAsync(string ytdlp, string url)
        {
            // --dump-single-json daje JSON sa svim info
            string args = $"--dump-single-json --flat-playlist --no-warnings \"{url}\"";

            string output = await RunProcessAsync(ytdlp, args, null, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(output))
            {
                ShowError("Nema odgovora od yt-dlp. Proveri URL.");
                return null;
            }

            // Parse manually without a JSON library dependency
            var info = new VideoInfo();
            info.Title   = ExtractJson(output, "title");
            info.Channel = ExtractJson(output, "uploader") is { Length: > 0 } u ? u : ExtractJson(output, "channel");

            string dtype = ExtractJson(output, "_type");
            if (dtype == "playlist")
            {
                info.IsPlaylist = true;
                // Broj videa: counts entries array
                var matches = Regex.Matches(output, "\"url\"\\s*:");
                info.Count = matches.Count;
                if (info.Count == 0)
                {
                    string countStr = ExtractJson(output, "playlist_count");
                    int.TryParse(countStr, out int cnt);
                    info.Count = cnt;
                }
            }
            else
            {
                string durStr = ExtractJson(output, "duration");
                double.TryParse(durStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double dur);
                info.DurationSec = dur;
            }

            return info;
        }

        // ─── Download ─────────────────────────────────────────────────────────

        private async Task<List<string>> RunDownloadAsync(
            string ytdlp, string url, bool audio, string quality, CancellationToken ct)
        {
            // Output template
            string outTemplate = Path.Combine(DownloadFolder, "%(title)s.%(ext)s");

            string formatArg;
            string postProcess = "";

            if (audio)
            {
                formatArg   = "--format bestaudio/best";
                postProcess = "--extract-audio --audio-format mp3 --audio-quality 192K";
            }
            else
            {
                if (quality == "best")
                    formatArg = "--format \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\"";
                else
                    formatArg = $"--format \"bestvideo[height<={quality}][ext=mp4]+bestaudio[ext=m4a]/best[height<={quality}][ext=mp4]/best[height<={quality}]\"";

                postProcess = "--merge-output-format mp4";
            }

            string args = $"{formatArg} {postProcess} --no-warnings --newline " +
                          $"--output \"{outTemplate}\" \"{url}\"";

            var downloadedPaths = new List<string>();

            // Hvatamo output linije za progress
            await RunProcessStreamAsync(ytdlp, args, line =>
            {
                ParseProgressLine(line, downloadedPaths);
            }, ct);

            // Ako lista nije popunjena iz output-a, skeniraj folder
            if (downloadedPaths.Count == 0)
            {
                // Uzimamo fajlove nastale u poslednjih 5 minuta
                var cutoff = DateTime.Now.AddMinutes(-5);
                foreach (var f in Directory.GetFiles(DownloadFolder))
                {
                    if (File.GetCreationTime(f) >= cutoff)
                        downloadedPaths.Add(f);
                }
            }

            return downloadedPaths;
        }

        private void ParseProgressLine(string line, List<string> paths)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // [download] X.X% of Y.YMiB at Z.ZMiB/s ETA HH:MM
            var pctMatch = Regex.Match(line, @"\[download\]\s+([\d.]+)%");
            if (pctMatch.Success)
            {
                double pct = double.Parse(pctMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);

                // Izvuci brzinu i ETA ako postoje
                string speed = "";
                var speedMatch = Regex.Match(line, @"at\s+([\d.]+\s*\w+/s)");
                if (speedMatch.Success) speed = speedMatch.Groups[1].Value;

                string eta = "";
                var etaMatch = Regex.Match(line, @"ETA\s+([\d:]+)");
                if (etaMatch.Success) eta = etaMatch.Groups[1].Value;

                string label = $"{pct:F1}%";
                if (!string.IsNullOrEmpty(speed)) label += $"  •  {speed}";
                if (!string.IsNullOrEmpty(eta))   label += $"  •  ETA {eta}";

                Dispatcher.Invoke(() => SetProgress(pct, label));
                return;
            }

            // [download] Destination: path\to\file.mp4
            var destMatch = Regex.Match(line, @"\[download\] Destination:\s+(.+)");
            if (destMatch.Success)
            {
                string path = destMatch.Groups[1].Value.Trim();
                Dispatcher.Invoke(() =>
                {
                    txbProgress.Text = $"Skida se: {Path.GetFileName(path)}";
                    Announce($"Preuzimanje: {Path.GetFileName(path)}");
                });
                return;
            }

            // [Merger] / [ExtractAudio] — finished file
            var mergeMatch = Regex.Match(line, @"\[(?:Merger|ExtractAudio)\] Destination:\s+(.+)");
            if (mergeMatch.Success)
            {
                string path = mergeMatch.Groups[1].Value.Trim();
                if (File.Exists(path) && !paths.Contains(path))
                    paths.Add(path);
                Dispatcher.Invoke(() =>
                {
                    SetProgress(100, $"Done: {Path.GetFileName(path)}");
                    Announce($"Download complete: {Path.GetFileName(path)}");
                });
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  PROCESS HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private async Task<string> RunProcessAsync(string exe, string args,
            Action<string> lineCallback, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var p = new Process { StartInfo = psi };
            var sb = new System.Text.StringBuilder();

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                sb.AppendLine(e.Data);
                lineCallback?.Invoke(e.Data);
            };

            p.Start();
            p.BeginOutputReadLine();

            await Task.Run(() => p.WaitForExit(), ct);
            if (ct.IsCancellationRequested) { try { p.Kill(); } catch { } }

            return sb.ToString();
        }

        private async Task RunProcessStreamAsync(string exe, string args,
            Action<string> lineCallback, CancellationToken ct)
        {
            await RunProcessAsync(exe, args, lineCallback, ct);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private void SetFetchingState(bool fetching)
        {
            btnFetchInfo.IsEnabled = !fetching;
            btnFetchInfo.Content   = fetching ? "Loading..." : "Load info";
            txtUrl.IsEnabled       = !fetching;
        }

        private void SetDownloadState(bool downloading)
        {
            _downloadInProgress    = downloading;
            btnDownload.IsEnabled  = !downloading;
            btnFetchInfo.IsEnabled = !downloading;
            txtUrl.IsEnabled       = !downloading;
            btnCancel.Content      = downloading ? "Cancel download" : "Cancel";
            pnlProgress.Visibility = downloading ? Visibility.Visible : Visibility.Visible;

            if (downloading)
            {
                prgDownload.Value    = 0;
                txbProgress.Text     = "Priprema...";
            }
        }

        private void SetProgress(double percent, string label)
        {
            prgDownload.Value = percent;
            txbProgress.Text  = label;
        }

        private void Announce(string msg)
        {
            // AutomationProperties.LiveSetting="Polite" na txbProgress
            // already sends JAWS/NVDA notification when text changes.
            // This method ensures messages are read outside the progress element too.
            Dispatcher.InvokeAsync(() => { txbProgress.Text = msg; });
        }

        private void ShowError(string msg)
        {
            Announce($"Error: {msg}");
            WpfMessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // ─── JSON mini-parser (bez dependencija) ─────────────────────────────
        private static string ExtractJson(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;

            // Also try number
            m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*([\\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        // ─── Formatiranje trajanja ─────────────────────────────────────────────
        private static string FormatDuration(double secs)
        {
            if (secs <= 0) return "";
            var ts = TimeSpan.FromSeconds(secs);
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }
}
