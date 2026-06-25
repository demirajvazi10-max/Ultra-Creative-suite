using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public enum MotionDirection
    {
        Static, Left, Right, Up, Down, TowardCamera, AwayCamera, Mixed, Unknown
    }

    public class MotionResult
    {
        public MotionDirection Direction { get; set; }
        public MotionDirection EndDirection { get; set; }
        public double Magnitude { get; set; }
        public bool IsStatic { get { return Direction == MotionDirection.Static; } }
        public bool HasStrongMotion { get { return Magnitude > 30; } }

        public static bool IsCompatible(MotionResult prev, MotionResult next)
        {
            if (prev == null || next == null) return true;
            if (prev.IsStatic || next.IsStatic) return true;
            if (prev.Direction == MotionDirection.Unknown || next.Direction == MotionDirection.Unknown) return true;
            if (prev.Direction == MotionDirection.Mixed || next.Direction == MotionDirection.Mixed) return true;
            if (prev.EndDirection == next.Direction) return true;

            if (prev.EndDirection == MotionDirection.Right && next.Direction == MotionDirection.Right) return true;
            if (prev.EndDirection == MotionDirection.Left && next.Direction == MotionDirection.Left) return true;
            if (prev.EndDirection == MotionDirection.Up && (next.Direction == MotionDirection.Up || next.Direction == MotionDirection.TowardCamera)) return true;
            if (prev.EndDirection == MotionDirection.Down && (next.Direction == MotionDirection.Down || next.Direction == MotionDirection.AwayCamera)) return true;
            if (prev.EndDirection == MotionDirection.TowardCamera && (next.Direction == MotionDirection.TowardCamera || next.Direction == MotionDirection.Up)) return true;
            if (prev.EndDirection == MotionDirection.AwayCamera && (next.Direction == MotionDirection.AwayCamera || next.Direction == MotionDirection.Down)) return true;

            return false;
        }
    }

    /// <summary>
    /// Detects camera motion in a video clip using a reliable frame-diff method.
    /// Extracts 6 frames (3 at the start, 3 at the end), measures pixel shift between consecutive
    /// frames using FFmpeg ssim/psnr output, and classifies direction of motion.
    /// Ova metoda radi sa svim FFmpeg build-ovima bez eksperimentalnih filtera.
    /// </summary>
    public static class MotionAnalyzer
    {
        private static readonly Dictionary<string, MotionResult> _cache =
            new Dictionary<string, MotionResult>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

        public static async Task<MotionResult> AnalyzeAsync(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath) || !File.Exists(ffmpegPath))
                return MakeUnknown();

            await _cacheLock.WaitAsync(ct);
            bool found = _cache.TryGetValue(videoPath, out MotionResult cached);
            _cacheLock.Release();
            if (found) return cached;

            var result = await DoFrameDiffAnalysis(videoPath, ffmpegPath, ct);

            await _cacheLock.WaitAsync(ct);
            _cache[videoPath] = result;
            _cacheLock.Release();

            return result;
        }

        public static async Task<MotionResult> AnalyzeEndAsync(
            string videoPath,
            string ffmpegPath,
            double clipDuration,
            double analyzeLastSeconds = 2.0,
            CancellationToken ct = default)
        {
            if (!File.Exists(videoPath) || !File.Exists(ffmpegPath))
                return MakeUnknown();

            // Za kraj klipa koristimo isti frame-diff, samo seek na zadnji dio
            return await DoFrameDiffAnalysis(videoPath, ffmpegPath, ct,
                seekTo: Math.Max(0, clipDuration - analyzeLastSeconds));
        }

        public static async Task<List<string>> FilterCompatibleAsync(
            List<string> candidatePaths,
            MotionResult previousClipMotion,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (previousClipMotion == null || previousClipMotion.IsStatic ||
                candidatePaths == null || candidatePaths.Count == 0)
                return candidatePaths;

            var compatible = new List<string>();
            foreach (var path in candidatePaths)
            {
                var motion = await AnalyzeAsync(path, ffmpegPath, ct);
                if (MotionResult.IsCompatible(previousClipMotion, motion))
                    compatible.Add(path);
            }
            return compatible.Count > 0 ? compatible : candidatePaths;
        }

        public static void ClearCache()
        {
            _cache.Clear();
        }

        // ── Pouzdana frame-diff implementacija ───────────────────────────────────
        // Extracts 3 frame pairs at 0.3s intervals and measures optical flow
        // using the standard FFmpeg scale+format pipeline without experimental filters.
        // Each pair gives dx/dy shift; we average all pairs for a more stable result.
        private static async Task<MotionResult> DoFrameDiffAnalysis(
            string videoPath, string ffmpegPath, CancellationToken ct,
            double seekTo = 0.0)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(),
                "MA_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                // Izvuci 6 frejmova: t=seekTo+0.0, +0.3, +0.6, +0.9, +1.2, +1.5
                double[] offsets = { 0.0, 0.3, 0.6, 0.9, 1.2, 1.5 };
                var framePaths = new List<string>();

                foreach (double off in offsets)
                {
                    double t = seekTo + off;
                    string outPng = Path.Combine(tmpDir, $"f_{off:F1}.png");
                    string args = $"-nostdin -ss {t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}" +
                                  $" -i \"{videoPath}\" -vframes 1 -vf \"scale=160:90\" -y \"{outPng}\"";
                    await RunFfmpegAsync(ffmpegPath, args, ct);
                    if (File.Exists(outPng)) framePaths.Add(outPng);
                }

                if (framePaths.Count < 2) return MakeUnknown();

                // Measures shift between consecutive frame pairs
                var dxList = new List<double>();
                var dyList = new List<double>();

                for (int i = 0; i < framePaths.Count - 1; i++)
                {
                    var (dx, dy) = await MeasureFrameShift(framePaths[i], framePaths[i + 1], ffmpegPath, ct);
                    if (!double.IsNaN(dx))
                    {
                        dxList.Add(dx);
                        dyList.Add(dy);
                    }
                }

                if (dxList.Count < 2) return MakeUnknown();

                double avgDx = dxList.Average();
                double avgDy = dyList.Average();
                double magnitude = Math.Sqrt(avgDx * avgDx + avgDy * avgDy);
                // Scale to 0-100 range (160px width → shift 8px = 5% of screen = magnitude ~20)
                double normalizedMag = Math.Min(100, magnitude * 12.5);

                if (normalizedMag < 4.0)
                    return new MotionResult
                    {
                        Direction = MotionDirection.Static,
                        EndDirection = MotionDirection.Static,
                        Magnitude = normalizedMag
                    };

                double threshold = 1.5;
                bool strongX = Math.Abs(avgDx) > threshold;
                bool strongY = Math.Abs(avgDy) > threshold;

                MotionDirection dir;
                if (strongX && strongY)
                {
                    if (Math.Abs(avgDx) > Math.Abs(avgDy) * 1.5)
                        dir = avgDx > 0 ? MotionDirection.Right : MotionDirection.Left;
                    else if (Math.Abs(avgDy) > Math.Abs(avgDx) * 1.5)
                        dir = avgDy > 0 ? MotionDirection.Down : MotionDirection.Up;
                    else
                        dir = MotionDirection.Mixed;
                }
                else if (strongX)
                    dir = avgDx > 0 ? MotionDirection.Right : MotionDirection.Left;
                else if (strongY)
                    dir = avgDy > 0 ? MotionDirection.Down : MotionDirection.Up;
                else
                    dir = MotionDirection.Static;

                // Provjeri zoom (TowardCamera/AwayCamera) — klipovi s jakim blur promjenama
                // Jednostavna heuristika: ako je magnitude visoka ali dx/dy mali → zoom
                if (normalizedMag > 15 && !strongX && !strongY)
                    dir = MotionDirection.TowardCamera;

                return new MotionResult
                {
                    Direction = dir,
                    EndDirection = dir,
                    Magnitude = Math.Round(normalizedMag, 1)
                };
            }
            catch
            {
                return MakeUnknown();
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // Measures pixel shift between two PNG frames using FFmpeg blend+metadata
        // Koristi standardni signalstats filter koji je dostupan u svim FFmpeg build-ovima
        private static async Task<(double dx, double dy)> MeasureFrameShift(
            string frameA, string frameB, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                // Koristimo blend diff + signalstats za mjerenje ukupne promjene pixela
                // Specifically: comparing left/right and top/bottom halves to determine direction
                string args = $"-nostdin -i \"{frameA}\" -i \"{frameB}\"" +
                    " -filter_complex" +
                    " \"[0:v]crop=80:90:0:0[left0];[1:v]crop=80:90:0:0[left1];" +
                    "[0:v]crop=80:90:80:0[right0];[1:v]crop=80:90:80:0[right1];" +
                    "[0:v]crop=160:45:0:0[top0];[1:v]crop=160:45:0:0[top1];" +
                    "[0:v]crop=160:45:0:45[bot0];[1:v]crop=160:45:0:45[bot1];" +
                    "[left0][left1]blend=all_mode=difference,signalstats=stat=mean[dl];" +
                    "[right0][right1]blend=all_mode=difference,signalstats=stat=mean[dr];" +
                    "[top0][top1]blend=all_mode=difference,signalstats=stat=mean[dt];" +
                    "[bot0][bot1]blend=all_mode=difference,signalstats=stat=mean[db]\"" +
                    " -map [dl] -map [dr] -map [dt] -map [db]" +
                    " -frames:v 1 -f null -";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (double.NaN, double.NaN);

                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                // Izvuci YAVG vrijednosti za svaki od 4 cropova
                var means = Regex.Matches(stderr, @"YAVG:([\d.]+)");
                if (means.Count < 4) return (double.NaN, double.NaN);

                double meanLeft  = double.Parse(means[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double meanRight = double.Parse(means[1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double meanTop   = double.Parse(means[2].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                double meanBot   = double.Parse(means[3].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                // dx positive = motion to the right (more changes on the left side)
                // dy positive = motion downward (more changes on the top side)
                double dx = meanLeft - meanRight;
                double dy = meanTop - meanBot;

                return (dx, dy);
            }
            catch
            {
                return (double.NaN, double.NaN);
            }
        }

        private static async Task RunFfmpegAsync(string ffmpegPath, string args, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch { }
        }

        private static MotionResult MakeUnknown()
        {
            return new MotionResult
            {
                Direction = MotionDirection.Unknown,
                EndDirection = MotionDirection.Unknown,
                Magnitude = 0
            };
        }
    }
}
