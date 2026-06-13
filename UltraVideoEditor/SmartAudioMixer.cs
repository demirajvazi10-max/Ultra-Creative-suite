using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // SMART AUDIO MIXER  —  Faza 3 / A
    //
    // Miksuje originalni zvuk klipova sa muzikom:
    //   - Ducking: muzika se stišava kad ima dijalog/govor
    //   - Fade in/out na granicama segmenata
    //   - Normalizacija glasnoće
    //   - Finalni stereo mix
    //
    // Koristi ffmpeg filtere: sidechaincompress, afade, loudnorm, amix
    // ═══════════════════════════════════════════════════════════════

    public class AudioMixSettings
    {
        /// <summary>Glasnoća muzike kad nema dijaloga (0–100).</summary>
        public double MusicVolume        { get; set; } = 85.0;

        /// <summary>Glasnoća muzike tokom duckinga (0–100).</summary>
        public double MusicDuckedVolume  { get; set; } = 25.0;

        /// <summary>Glasnoća originalnog zvuka klipova (0–100).</summary>
        public double ClipVolume         { get; set; } = 70.0;

        /// <summary>Trajanje fade-in/out na prelazima (sekunde).</summary>
        public double FadeDuration       { get; set; } = 0.4;

        /// <summary>Da li normalizovati finalni mix na -14 LUFS (YouTube standard).</summary>
        public bool   NormalizeLoudness  { get; set; } = true;

        /// <summary>Da li koristiti ducking (sidechain kompresija).</summary>
        public bool   EnableDucking      { get; set; } = true;

        /// <summary>Da li mute-ovati originalni zvuk (samo muzika).</summary>
        public bool   MuteOriginalAudio  { get; set; } = false;
    }

    public class AudioMixResult
    {
        public string OutputPath  { get; set; }
        public bool   Success     { get; set; }
        public string Error       { get; set; }
        public double LUFSLevel   { get; set; }
        public string FfmpegLog   { get; set; }
    }

    public static class SmartAudioMixer
    {
        // ── Javni API ────────────────────────────────────────────────

        /// <summary>
        /// Miksuje zvuk za finalni highlight video.
        /// </summary>
        /// <param name="videoPath">Putanja do video fajla (sa originalnim zvukom).</param>
        /// <param name="musicPath">Putanja do muzičkog fajla.</param>
        /// <param name="outputPath">Gde da sačuva rezultat (audio fajl .aac ili .m4a).</param>
        /// <param name="totalDuration">Ukupno trajanje videa (sekunde).</param>
        /// <param name="settings">Podešavanja miksa.</param>
        /// <param name="progress">Progress callback.</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task<AudioMixResult> MixAsync(
            string           videoPath,
            string           musicPath,
            string           outputPath,
            double           totalDuration,
            AudioMixSettings settings  = null,
            IProgress<(int Percent, string Message)> progress = null,
            CancellationToken ct = default)
        {
            settings ??= new AudioMixSettings();

            string ffmpegPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
                return Fail("FFmpeg nije pronađen.");
            if (!File.Exists(videoPath))
                return Fail($"Video nije pronađen: {videoPath}");
            if (!File.Exists(musicPath))
                return Fail($"Muzika nije pronađena: {musicPath}");

            progress?.Report((10, "Gradim audio filter graph…"));

            string filterComplex = BuildFilterComplex(totalDuration, settings);
            string args          = BuildFfmpegArgs(videoPath, musicPath, outputPath,
                                                    filterComplex, settings, totalDuration);

            progress?.Report((25, "Pokrećem ffmpeg audio miks…"));

            var (exitCode, log) = await RunFfmpegAsync(ffmpegPath, args, ct);

            if (exitCode != 0)
                return Fail($"FFmpeg greška (exit {exitCode}): {ExtractError(log)}", log);

            progress?.Report((90, "Merjem glasnoću (LUFS)…"));
            double lufs = await MeasureLoudnessAsync(outputPath, ffmpegPath, ct);

            progress?.Report((100, "Audio miks završen."));

            return new AudioMixResult
            {
                OutputPath = outputPath,
                Success    = true,
                LUFSLevel  = lufs,
                FfmpegLog  = log,
            };
        }

        /// <summary>
        /// Primenjuje AudioMixSettings na listu TimelineItem-a — setuje Volume
        /// i upisuje fade tagove u AudioDescription.
        /// </summary>
        public static void ApplyToTimeline(
            List<TimelineItem> items,
            AudioMixSettings   settings)
        {
            if (items == null || settings == null) return;

            foreach (var item in items)
            {
                if (item.IsAudioTrack)
                {
                    // Muzička traka
                    item.Volume = settings.MuteOriginalAudio ? 0 : settings.MusicVolume;
                }
                else if (item.IsVideoTrack)
                {
                    // Video traka — originalni zvuk
                    item.Volume = settings.MuteOriginalAudio ? 0 : settings.ClipVolume;

                    // Dodaj fade tagove
                    string fadeTag = $"afade_in={settings.FadeDuration:F2};" +
                                     $"afade_out={settings.FadeDuration:F2}";
                    item.AudioDescription = string.IsNullOrEmpty(item.AudioDescription)
                        ? fadeTag
                        : item.AudioDescription + ";" + fadeTag;
                }
            }
        }

        // ── Filter Graph Builder ─────────────────────────────────────

        private static string BuildFilterComplex(
            double totalDuration, AudioMixSettings s)
        {
            var sb = new StringBuilder();

            // Input 0: video (original audio) → [orig]
            // Input 1: muzika                 → [music]

            double musicVol  = s.MusicVolume        / 100.0;
            double duckVol   = s.MusicDuckedVolume  / 100.0;
            double clipVol   = s.ClipVolume         / 100.0;
            double fadeDur   = s.FadeDuration;

            // Trim muzike na duzinu videa
            sb.Append($"[1:a]atrim=0:{totalDuration:F3},asetpts=PTS-STARTPTS[music_trim];");

            if (s.MuteOriginalAudio)
            {
                // Samo muzika, normalizovana
                sb.Append($"[music_trim]volume={musicVol:F2}[music_vol];");
                if (s.NormalizeLoudness)
                    sb.Append("[music_vol]loudnorm=I=-14:TP=-1:LRA=11[aout]");
                else
                    sb.Append("[music_vol]acopy[aout]");
            }
            else if (s.EnableDucking)
            {
                // Ducking: sidechain kompresija
                // Original audio → detektor → kompresuje muziku kad ima zvuka
                sb.Append($"[0:a]volume={clipVol:F2}," +
                           $"afade=t=in:ss=0:d={fadeDur:F2}[clip_vol];");

                sb.Append($"[music_trim]volume={musicVol:F2}[music_full];");

                // Sidechain: clip_vol kompresuje music_full
                sb.Append("[music_full][clip_vol]sidechaincompress=" +
                           "threshold=0.02:ratio=4:attack=200:release=1000:" +
                           "makeup=1[music_ducked];");

                // Finalni miks
                sb.Append("[music_ducked][clip_vol]amix=inputs=2:duration=first:normalize=0[mixed];");

                if (s.NormalizeLoudness)
                    sb.Append("[mixed]loudnorm=I=-14:TP=-1:LRA=11[aout]");
                else
                    sb.Append("[mixed]acopy[aout]");
            }
            else
            {
                // Jednostavan miks bez duckinga
                sb.Append($"[0:a]volume={clipVol:F2}," +
                           $"afade=t=in:ss=0:d={fadeDur:F2}," +
                           $"afade=t=out:st={Math.Max(0, totalDuration - fadeDur):F2}:d={fadeDur:F2}[clip_vol];");

                sb.Append($"[music_trim]volume={musicVol:F2}," +
                           $"afade=t=in:ss=0:d={fadeDur:F2}," +
                           $"afade=t=out:st={Math.Max(0, totalDuration - fadeDur):F2}:d={fadeDur:F2}[music_vol];");

                sb.Append("[clip_vol][music_vol]amix=inputs=2:duration=first:normalize=0[mixed];");

                if (s.NormalizeLoudness)
                    sb.Append("[mixed]loudnorm=I=-14:TP=-1:LRA=11[aout]");
                else
                    sb.Append("[mixed]acopy[aout]");
            }

            return sb.ToString();
        }

        private static string BuildFfmpegArgs(
            string videoPath, string musicPath, string outputPath,
            string filterComplex, AudioMixSettings s, double totalDuration)
        {
            return $"-nostdin -y " +
                   $"-i \"{videoPath}\" " +
                   $"-i \"{musicPath}\" " +
                   $"-filter_complex \"{filterComplex}\" " +
                   $"-map \"[aout]\" " +
                   $"-t {totalDuration:F3} " +
                   $"-c:a aac -b:a 192k " +
                   $"\"{outputPath}\"";
        }

        // ── LUFS Merenje ─────────────────────────────────────────────

        private static async Task<double> MeasureLoudnessAsync(
            string audioPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                string args = $"-nostdin -i \"{audioPath}\" " +
                              $"-filter:a loudnorm=print_format=json -f null -";

                var (_, log) = await RunFfmpegAsync(ffmpegPath, args, ct);

                // Parsiramo input_i iz JSON outputa
                var match = System.Text.RegularExpressions.Regex.Match(
                    log, @"""input_i""\s*:\s*""([-\d.]+)""");

                if (match.Success &&
                    double.TryParse(match.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double lufs))
                    return lufs;
            }
            catch { }
            return -99.0;
        }

        // ── FFmpeg runner ─────────────────────────────────────────────

        private static async Task<(int exitCode, string log)> RunFfmpegAsync(
            string ffmpegPath, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ffmpegPath,
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "Process start failed");

            var logTask = proc.StandardError.ReadToEndAsync();
            var outTask = proc.StandardOutput.ReadToEndAsync();

            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
            }

            string log = await logTask + await outTask;
            return (proc.ExitCode, log);
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static AudioMixResult Fail(string error, string log = null)
            => new AudioMixResult { Success = false, Error = error, FfmpegLog = log ?? "" };

        private static string ExtractError(string log)
        {
            if (string.IsNullOrEmpty(log)) return "nepoznata greška";
            var lines = log.Split('\n');
            return lines.LastOrDefault(l => l.Contains("Error") || l.Contains("Invalid") ||
                                            l.Contains("error") || l.Contains("failed"))
                   ?? lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
                   ?? "nepoznata greška";
        }
    }
}
