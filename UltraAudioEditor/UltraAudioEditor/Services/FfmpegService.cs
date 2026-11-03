using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// FFmpeg za formate koje NAudio ne zna (FLAC, OGG, Opus izvoz; OGG/Opus/WMA/... uvoz)
    /// i za oznake pesme (izvođač, naslov, omot). Traži se: u folderu programa, u
    /// %LOCALAPPDATA%\UltraAudioEditor\ffmpeg, u PATH-u. Ako ga nema, program nudi da ga
    /// sam preuzme (jednom, ~100 MB) — isti pristup kao Audio Description Maker.
    /// </summary>
    public static class FfmpegService
    {
        private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        public static string LocalFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraAudioEditor", "ffmpeg");

        public static string? Find()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(LocalFolder, "ffmpeg.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        public static bool IsAvailable => Find() != null;

        /// <summary>Preuzima FFmpeg (zip sa GitHub-a) i raspakuje samo ffmpeg.exe.</summary>
        public static async Task<string> DownloadAsync(IProgress<int>? progress, CancellationToken ct = default)
        {
            Directory.CreateDirectory(LocalFolder);
            string zip = Path.Combine(LocalFolder, "ffmpeg.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
            using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((int)(done * 100 / total));
                }
            }
            string exe = Path.Combine(LocalFolder, "ffmpeg.exe");
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("ffmpeg.exe not found in the downloaded archive.");
                entry.ExtractToFile(exe, overwrite: true);
            }
            try { File.Delete(zip); } catch { }
            DebugLog.Write("FFmpeg downloaded to " + exe);
            return exe;
        }

        public static async Task<(int code, string err)> RunAsync(string args, CancellationToken ct = default)
        {
            var exe = Find() ?? throw new InvalidOperationException("FFmpeg not found.");
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                }
            };
            var err = new System.Text.StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.OutputDataReceived += (_, __) => { };
            p.Start();
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            DebugLog.Write($"ffmpeg {args} -> {p.ExitCode}");
            return (p.ExitCode, err.ToString());
        }

        /// <summary>Pretvara bilo koji audio fajl u WAV (za uvoz formata koje NAudio ne čita).</summary>
        public static async Task<string> ConvertToWavAsync(string input)
        {
            string outDir = Path.Combine(AudioBuffers.RendersFolder(), "imported");
            Directory.CreateDirectory(outDir);
            string output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_" + Guid.NewGuid().ToString("N")[..6] + ".wav");
            var (code, err) = await RunAsync($"-y -hide_banner -i \"{input}\" -vn -acodec pcm_s16le \"{output}\"");
            if (code != 0 || !File.Exists(output)) throw new InvalidOperationException(LastLines(err));
            return output;
        }

        public static string ExtensionFor(string format) => format switch
        {
            "FLAC" => ".flac", "OGG" => ".ogg", "OPUS" => ".opus", "MP3" => ".mp3", "M4A" => ".m4a", "AIFF" => ".aiff", _ => ".wav"
        };

        /// <summary>
        /// Kodira WAV u zadati format i upisuje oznake (i omot, gde format to podržava).
        /// format: FLAC, OGG, OPUS, MP3, M4A, WAV. bitrateKbps se koristi za lossy formate.
        /// </summary>
        public static async Task EncodeAsync(string wavInput, string output, string format, int bitrateKbps, AudioTags? tags)
        {
            var codec = format switch
            {
                "FLAC" => "-c:a flac",
                "OGG"  => $"-c:a libvorbis -b:a {bitrateKbps}k",
                "OPUS" => $"-c:a libopus -b:a {Math.Min(bitrateKbps, 256)}k",
                "MP3"  => $"-c:a libmp3lame -b:a {bitrateKbps}k -id3v2_version 3",
                "M4A"  => $"-c:a aac -b:a {bitrateKbps}k",
                "AIFF" => "-c:a pcm_s16be",
                _      => "-c:a pcm_s16le"
            };
            bool cover = tags != null && File.Exists(tags.CoverPath) && format is "MP3" or "M4A" or "FLAC";
            string inputs = $"-i \"{wavInput}\"" + (cover ? $" -i \"{tags!.CoverPath}\"" : "");
            string maps = cover ? "-map 0:a -map 1:v -c:v copy -disposition:v attached_pic" : "-map 0:a";
            string meta = tags == null ? "" : MetaArgs(tags);
            var (code, err) = await RunAsync($"-y -hide_banner {inputs} {maps} {codec} {meta} \"{output}\"");
            if (code != 0 || !File.Exists(output)) throw new InvalidOperationException(LastLines(err));
        }

        private static string MetaArgs(AudioTags t)
        {
            static string Esc(string s) => s.Replace("\"", "'");
            var parts = new List<string>();
            void Add(string key, string v) { if (!string.IsNullOrWhiteSpace(v)) parts.Add($"-metadata {key}=\"{Esc(v.Trim())}\""); }
            Add("title", t.Title); Add("artist", t.Artist); Add("album", t.Album);
            Add("date", t.Year); Add("genre", t.Genre); Add("comment", t.Comment);
            return string.Join(" ", parts);
        }

        private static string LastLines(string err)
        {
            var lines = err.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return string.Join(" ", lines.Skip(Math.Max(0, lines.Count - 3)));
        }
    }
}
