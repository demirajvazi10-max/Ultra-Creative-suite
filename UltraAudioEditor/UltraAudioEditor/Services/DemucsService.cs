using System.Diagnostics;
using System.IO;

using UltraAudioEditor.Localization;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// Vokal/instrumental razdvajanje pomoću Demucs (Meta AI).
    /// Demucs se poziva kao eksterni subprocess — identično kako Audacity i drugi editore rade.
    ///
    /// INSTALACIJA (jednom):
    ///   pip install demucs
    ///   ili: winget install Python.Python.3  pa  pip install demucs
    ///
    /// Modeli koje podržavamo:
    ///   htdemucs       — najbrži, 2 stema (vocals + no_vocals)
    ///   htdemucs_ft    — finiji, 4 stema (drums, bass, other, vocals)  ← default
    ///   mdx_extra      — visok kvalitet, sporiji
    /// </summary>
    public class DemucsService
    {
        public enum StemMode
        {
            TwoStems,   // vocals + no_vocals (instrumental)
            FourStems   // drums + bass + other + vocals
        }

        public bool IsAvailable => FindPython() != null;
        public string StatusMessage { get; private set; } = "";

        // ── Provjera da li je Demucs instaliran ───────────────────────────
        public async Task<bool> CheckAvailableAsync()
        {
            string? python = FindPython();
            if (python == null)
            {
                StatusMessage = Lang.T("demucs_no_python") + "\n\n" + LastDiagnostics;
                return false;
            }
            try
            {
                // Prvi put kad se Demucs proveri, Python učitava PyTorch što može
                // potrajati i po pola minuta na sporijem hardveru — zato 90s, ne
                // par sekundi. Ako ni to ne prođe, nešto je stvarno zaglavljeno.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var result = await RunCommandAsync(python, "-m demucs --help", "", null, cts.Token);
                if (result.ExitCode != 0)
                {
                    StatusMessage = Lang.T("demucs_not_found_msg") +
                        $"\n\n[koristi: {python}]\nexit={result.ExitCode}\n{result.StdErr}";
                    return false;
                }
                StatusMessage = Lang.T("demucs_available");
                return true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = Lang.T("demucs_timeout");
                return false;
            }
            catch
            {
                StatusMessage = Lang.T("demucs_not_found_msg");
                return false;
            }
        }

        // ── Glavna metoda za razdvajanje ──────────────────────────────────
        public async Task<DemucsResult> SeparateAsync(
            string inputFilePath,
            string outputDirectory,
            StemMode mode = StemMode.TwoStems,
            string model = "htdemucs",
            IProgress<(int Percent, string Status)>? progress = null,
            CancellationToken ct = default)
        {
            string? python = FindPython();
            if (python == null)
                throw new Exception(Lang.T("demucs_no_python"));

            if (!File.Exists(inputFilePath))
                throw new FileNotFoundException(Lang.T("demucs_no_audio"), inputFilePath);

            Directory.CreateDirectory(outputDirectory);

            // Demucs argumenti
            string stems = mode == StemMode.TwoStems ? "--two-stems vocals" : "";
            string args = $"-m demucs {stems} --name {model} --out \"{outputDirectory}\" \"{inputFilePath}\"";

            progress?.Report((5, "Pokretanje Demucs..."));

            var result = await RunCommandAsync(python, args, outputDirectory, progress, ct);

            if (result.ExitCode != 0)
                throw new Exception(string.Format(Lang.T("demucs_error"), result.StdErr));

            // Pronađi outpute — Demucs kreira: outputDir/model/track_name/*.wav
            string trackName = Path.GetFileNameWithoutExtension(inputFilePath);
            string stemDir   = Path.Combine(outputDirectory, model, trackName);

            progress?.Report((90, Lang.T("demucs_searching")));

            if (!Directory.Exists(stemDir))
            {
                // Neki Demucs build-ovi koriste drugačiji path
                var found = Directory.GetDirectories(outputDirectory, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(d => Directory.GetFiles(d, "*.wav").Length > 0);
                stemDir = found ?? outputDirectory;
            }

            var wavFiles = Directory.GetFiles(stemDir, "*.wav");
            progress?.Report((100, "Gotovo!"));

            return new DemucsResult
            {
                StemDirectory = stemDir,
                VocalsPath    = wavFiles.FirstOrDefault(f => f.Contains("vocals")),
                NoVocalsPath  = wavFiles.FirstOrDefault(f => f.Contains("no_vocals") || f.Contains("instrumental")),
                DrumsPath     = wavFiles.FirstOrDefault(f => f.Contains("drums")),
                BassPath      = wavFiles.FirstOrDefault(f => f.Contains("bass")),
                OtherPath     = wavFiles.FirstOrDefault(f => f.Contains("other")),
                AllStems      = wavFiles.ToList()
            };
        }

        // ── Async subprocess runner ───────────────────────────────────────
        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCommandAsync(
            string executable, string arguments, string workingDir,
            IProgress<(int, string)>? progress, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = executable,
                Arguments              = arguments,
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = new Process { StartInfo = psi };
            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdout.AppendLine(e.Data);
                // Demucs ispisuje progress kao: "Separating track ..."  ili  "100%"
                if (e.Data.Contains('%') && int.TryParse(
                    new string(e.Data.TakeWhile(c => char.IsDigit(c)).ToArray()), out int pct))
                    progress?.Report((Math.Clamp(5 + pct * 80 / 100, 5, 85), $"Demucs: {pct}%"));
                else if (e.Data.Length > 0)
                    progress?.Report((-1, e.Data.Trim()));
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Vreme isteklo (ili je pozivalac otkazao) — ubij proces da ne ostane
                // da visi u pozadini, i prosledi grešku dalje umesto da zaglavimo zauvek.
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            return (proc.ExitCode, stdout.ToString(), stderr.ToString());
        }

        public static string LastDiagnostics { get; private set; } = "";

        // ── Pronalaženje Pythona ───────────────────────────────────────────
        private static string? FindPython()
        {
            string[] candidates = {
                "py",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Python\Python312\python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Python\Python311\python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Python\Python310\python.exe"),
                @"C:\Python312\python.exe", @"C:\Python311\python.exe",
                @"C:\Python310\python.exe", @"C:\Python39\python.exe",
                "python", "python3",
            };

            var log = new System.Text.StringBuilder();
            log.AppendLine($"LocalAppData resolved to: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");

            foreach (var candidate in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate, Arguments = "--version",
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    string outp = p?.StandardOutput.ReadToEnd() ?? "";
                    string errp = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(2000);
                    log.AppendLine($"  [{candidate}] exit={p?.ExitCode} out=\"{outp.Trim()}\" err=\"{errp.Trim()}\"");
                    if (p?.ExitCode == 0)
                    {
                        LastDiagnostics = log.ToString();
                        return candidate;
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine($"  [{candidate}] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
            }
            LastDiagnostics = log.ToString();
            return null;
        }
    }

    public class DemucsResult
    {
        public string  StemDirectory { get; init; } = "";
        public string? VocalsPath    { get; init; }
        public string? NoVocalsPath  { get; init; }
        public string? DrumsPath     { get; init; }
        public string? BassPath      { get; init; }
        public string? OtherPath     { get; init; }
        public List<string> AllStems { get; init; } = new();
    }
}
