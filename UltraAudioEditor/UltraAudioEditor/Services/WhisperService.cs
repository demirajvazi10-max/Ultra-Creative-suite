using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace UltraAudioEditor.Services
{
    public class TranscriptSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Lokalna transkripcija preko Whisper-a — isti alati koje koristi Ultra Video Editor
    /// (whisper.exe iz Python paketa openai-whisper, ili faster-whisper-xxl.exe). Ništa ne
    /// ide na internet osim jednokratnog preuzimanja modela pri prvom korišćenju.
    /// Zvuk se šalje kao 16 kHz mono WAV, a rezultat se čita iz JSON-a (ili SRT-a).
    /// </summary>
    public static class WhisperService
    {
        public static string? FindExecutable()
        {
            string appDir = AppContext.BaseDirectory;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>
            {
                Path.Combine(appDir, "whisper.exe"),
                Path.Combine(appDir, "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Whisper", "whisper.exe"),
                Path.Combine(appDir, "Whisper", "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Tools", "whisper.exe"),
                Path.Combine(home, "miniconda3", "Scripts", "whisper.exe"),
                Path.Combine(home, "anaconda3", "Scripts", "whisper.exe"),
            };
            foreach (var v in new[] { "Python313", "Python312", "Python311", "Python310" })
                candidates.Add(Path.Combine(home, "AppData", "Local", "Programs", "Python", v, "Scripts", "whisper.exe"));
            // Ultra Video Editor instalacija (deli isti Whisper)
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf, "Ultra Video Editor", "Whisper", "faster-whisper-xxl.exe"));
            candidates.Add(Path.Combine(pf, "Ultra Video Editor", "faster-whisper-xxl.exe"));
            foreach (var c in candidates) if (File.Exists(c)) return c;
            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    foreach (var name in new[] { "whisper.exe", "faster-whisper-xxl.exe" })
                    {
                        var p = Path.Combine(dir.Trim(), name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool IsAvailable() => FindExecutable() != null;

        public static async Task<List<TranscriptSegment>> TranscribeAsync(float[] stereo, int sr, string language, string model, IProgress<string>? progress)
        {
            string exe = FindExecutable() ?? throw new InvalidOperationException("Whisper not found.");
            string dir = Path.Combine(Path.GetTempPath(), "UltraAudioEditor_whisper_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string wav = Path.Combine(dir, "audio.wav");
            await Task.Run(() => WriteMono16k(stereo, sr, wav));

            bool faster = Path.GetFileName(exe).Contains("faster", StringComparison.OrdinalIgnoreCase);
            string lang = string.IsNullOrEmpty(language) ? "" : $"--language {language} ";
            string args = faster
                ? $"\"{wav}\" --model {model} {lang}--output_format json --output_dir \"{dir}\" --compute_type auto --vad_filter True --condition_on_previous_text False --beam_size 5"
                : $"\"{wav}\" --model {model} {lang}--output_format json --output_dir \"{dir}\" --condition_on_previous_text False --temperature 0 --verbose False";

            var err = new StringBuilder();
            async Task<int> Run(string a)
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, a)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardError = true, RedirectStandardOutput = true,
                        StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
                    }
                };
                p.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { lock (err) err.AppendLine(e.Data); progress?.Report("Whisper: " + e.Data.Trim()); } };
                p.OutputDataReceived += (_, __) => { };
                p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();
                await p.WaitForExitAsync();
                return p.ExitCode;
            }

            progress?.Report(string.Format("Whisper ({0})...", model));
            int code = await Run(args);
            string? json = Directory.GetFiles(dir, "*.json").FirstOrDefault();
            if (json == null)
            {
                // neke verzije ne prihvataju json — pokušaj srt
                code = await Run(args.Replace("--output_format json", "--output_format srt"));
                string? srt = Directory.GetFiles(dir, "*.srt").FirstOrDefault();
                if (srt == null) throw new InvalidOperationException($"Whisper exit {code}: " + Tail(err.ToString()));
                var segs = ParseSrt(File.ReadAllText(srt, Encoding.UTF8));
                Cleanup(dir);
                return segs;
            }
            var result = ParseJson(File.ReadAllText(json, Encoding.UTF8));
            Cleanup(dir);
            return result;
        }

        private static void WriteMono16k(float[] st, int sr, string path)
        {
            int frames = st.Length / 2;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++) mono[f] = (st[2 * f] + st[2 * f + 1]) * 0.5f;
            var src = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(new MonoBuffer(mono, sr), 16000);
            WaveFileWriter.CreateWaveFile16(path, src);
        }

        private sealed class MonoBuffer : ISampleProvider
        {
            private readonly float[] _d; private int _p;
            public MonoBuffer(float[] d, int sr) { _d = d; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sr, 1); }
            public WaveFormat WaveFormat { get; }
            public int Read(float[] b, int o, int c) { int n = Math.Min(c, _d.Length - _p); if (n <= 0) return 0; Array.Copy(_d, _p, b, o, n); _p += n; return n; }
        }

        private static List<TranscriptSegment> ParseJson(string json)
        {
            var list = new List<TranscriptSegment>();
            var root = Newtonsoft.Json.Linq.JToken.Parse(json);
            var segs = root.Type == Newtonsoft.Json.Linq.JTokenType.Array ? root : root["segments"];
            if (segs == null) return list;
            foreach (var s in segs)
            {
                string text = (string?)s["text"] ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                list.Add(new TranscriptSegment { Start = (double?)s["start"] ?? 0, End = (double?)s["end"] ?? 0, Text = text.Trim() });
            }
            return list;
        }

        private static List<TranscriptSegment> ParseSrt(string srt)
        {
            var list = new List<TranscriptSegment>();
            foreach (var block in srt.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                int ti = lines.FindIndex(l => l.Contains("-->"));
                if (ti < 0) continue;
                var parts = lines[ti].Split("-->");
                list.Add(new TranscriptSegment
                {
                    Start = ParseSrtTime(parts[0]), End = ParseSrtTime(parts[1]),
                    Text = string.Join(" ", lines.Skip(ti + 1)).Trim()
                });
            }
            return list;
        }

        private static double ParseSrtTime(string s)
        {
            s = s.Trim().Replace(',', '.');
            return TimeSpan.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var ts) ? ts.TotalSeconds : 0;
        }

        public static string ToSrt(IEnumerable<TranscriptSegment> segs)
        {
            var sb = new StringBuilder(); int i = 1;
            static string T(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}"; }
            foreach (var s in segs) sb.AppendLine((i++).ToString()).AppendLine($"{T(s.Start)} --> {T(s.End)}").AppendLine(s.Text).AppendLine();
            return sb.ToString();
        }

        private static string Tail(string s) { var l = s.Split('\n').Where(x => x.Trim().Length > 0).ToList(); return string.Join(" ", l.Skip(Math.Max(0, l.Count - 3))); }
        private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
    }
}
