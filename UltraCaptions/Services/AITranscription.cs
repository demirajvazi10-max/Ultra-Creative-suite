using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UltraCaptions.Models;

namespace UltraCaptions.Services
{
    /// <summary>
    /// Local Whisper transcription - no internet, no API key required.
    /// Uses the same approach as Ultra Video Editor's AI Video Creator: shells
    /// out to a local whisper.exe / faster-whisper-xxl.exe install (via the
    /// Python "openai-whisper" or "faster-whisper" packages) rather than a
    /// cloud API, so there's no per-user cost and nothing leaves the machine.
    /// </summary>
    public static class AITranscription
    {
        public static bool IsWhisperAvailable() => FindWhisperExecutable() != null;

        private static string? FindWhisperExecutable()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "whisper.exe"),
                Path.Combine(appDir, "whisper-cli.exe"),
                Path.Combine(appDir, "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Whisper", "whisper.exe"),
                Path.Combine(appDir, "Tools", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python311", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python312", "Scripts", "whisper.exe"),
            };

            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo("where", "whisper")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    string first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            }
            catch { /* "where" not finding it is a normal outcome, not an error */ }

            return null;
        }

        /// <summary>
        /// Runs Whisper against a media file and returns one CaptionEntry per
        /// detected line, with start/end timestamps already filled in. The
        /// caller drops these straight into the same editable list used for
        /// manually-typed lines.
        /// </summary>
        public static async Task<List<CaptionEntry>> TranscribeAsync(string mediaPath, string language = "auto")
        {
            string? whisperExe = FindWhisperExecutable();
            if (whisperExe == null)
                throw new InvalidOperationException(
                    "Whisper not found. Install it with: pip install openai-whisper (requires Python + ffmpeg).");

            string tempDir = Path.Combine(Path.GetTempPath(), "UltraCaptions_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string langArg = language == "auto" ? "" : $" --language {language}";
                string args = $"\"{mediaPath}\" --output_format srt --output_dir \"{tempDir}\"{langArg}";

                var psi = new ProcessStartInfo(whisperExe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start Whisper process.");
                await process.WaitForExitAsync();

                string srtPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(mediaPath) + ".srt");
                if (!File.Exists(srtPath))
                    throw new InvalidOperationException("Whisper finished but produced no .srt file.");

                return SrtService.Import(srtPath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
