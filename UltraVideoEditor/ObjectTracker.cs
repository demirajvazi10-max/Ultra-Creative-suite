using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — one place of the followed object (times in seconds of the clip's FILE; positions 0 … 1 of its frame).</summary>
    public class TrackPoint
    {
        public double T { get; set; }
        /// <summary>Centre of the object.</summary>
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Size of the object.</summary>
        public double W { get; set; }
        public double H { get; set; }
        /// <summary>The object was not seen here; the last place is kept.</summary>
        public bool Lost { get; set; }
    }

    /// <summary>v2.0 — an object followed through a clip, and what is done with it. Saved with the clip.</summary>
    public class TrackingData
    {
        /// <summary>What was asked for ("the dog") or the part of the picture it started from.</summary>
        public string Target { get; set; } = "";
        public List<TrackPoint> Path { get; set; } = new List<TrackPoint>();
        /// <summary>None (only the path, for a mask or a layer), Blur, Pixelate, Black or Spotlight.</summary>
        public string Effect { get; set; } = "Blur";
        /// <summary>How strong the blur / how coarse the pixels, 1 … 100.</summary>
        public double Strength { get; set; } = 50;
        /// <summary>The covered area is this many percent bigger than the object.</summary>
        public double Padding { get; set; } = 20;

        public static readonly string[] Effects = { "None", "Blur", "Pixelate", "Black", "Spotlight" };

        public bool HasEffect => Path != null && Path.Count > 0 && !string.IsNullOrEmpty(Effect) && Effect != "None";

        public TrackingData Clone()
        {
            var c = (TrackingData)MemberwiseClone();
            c.Path = (Path ?? new List<TrackPoint>()).Select(p => new TrackPoint { T = p.T, X = p.X, Y = p.Y, W = p.W, H = p.H, Lost = p.Lost }).ToList();
            return c;
        }

        /// <summary>The place at a file time (between two points it is interpolated).</summary>
        public TrackPoint At(double t)
        {
            if (Path == null || Path.Count == 0) return null;
            if (t <= Path[0].T) return Path[0];
            if (t >= Path[^1].T) return Path[^1];
            int i = Path.FindIndex(p => p.T > t);
            var a = Path[i - 1]; var b = Path[i];
            double k = (t - a.T) / Math.Max(1e-6, b.T - a.T);
            return new TrackPoint { T = t, X = a.X + (b.X - a.X) * k, Y = a.Y + (b.Y - a.Y) * k, W = a.W + (b.W - a.W) * k, H = a.H + (b.H - a.H) * k };
        }
    }

    /// <summary>
    /// v2.0 — follows an object through a clip. The object is found on one frame — by the
    /// picture AI (Gemini or the local vision model) from a description such as "the man in
    /// the red jacket", or from a part of the picture chosen from a list — and then followed
    /// frame by frame (10 frames a second, small grey frames) by matching its look
    /// (normalised correlation), with a slowly updated template, a little change of size, and
    /// a wider search when it is lost. The result is a path saved with the clip.
    /// </summary>
    public static class ObjectTracker
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string N(double v) => v.ToString("0.###", Inv);

        public static readonly string[] Regions = { "Center", "TopLeft", "Top", "TopRight", "Left", "Right", "BottomLeft", "Bottom", "BottomRight" };

        /// <summary>A box in a part of the picture (size = width in percent of the frame, square on screen).</summary>
        public static TrackPoint RegionBox(string region, double sizePercent, double frameAspect)
        {
            double w = Math.Clamp(sizePercent, 5, 90) / 100.0;
            double h = Math.Min(0.95, w * frameAspect);
            double x = region switch { "TopLeft" or "Left" or "BottomLeft" => 1 / 6.0, "TopRight" or "Right" or "BottomRight" => 5 / 6.0, _ => 0.5 };
            double y = region switch { "TopLeft" or "Top" or "TopRight" => 1 / 6.0, "BottomLeft" or "Bottom" or "BottomRight" => 5 / 6.0, _ => 0.5 };
            x = Math.Clamp(x, w / 2, 1 - w / 2);
            y = Math.Clamp(y, h / 2, 1 - h / 2);
            return new TrackPoint { X = x, Y = y, W = w, H = h };
        }

        /// <summary>Where in the frame a point is, in words (key of the 3 × 3 part).</summary>
        public static string RegionOf(double x, double y)
        {
            int c = x < 1 / 3.0 ? 0 : x < 2 / 3.0 ? 1 : 2, r = y < 1 / 3.0 ? 0 : y < 2 / 3.0 ? 1 : 2;
            return (r, c) switch
            {
                (0, 0) => "TopLeft", (0, 1) => "Top", (0, 2) => "TopRight",
                (1, 0) => "Left", (1, 1) => "Center", (1, 2) => "Right",
                (2, 0) => "BottomLeft", (2, 1) => "Bottom", _ => "BottomRight"
            };
        }

        // ── finding the object with the picture AI ──────────────────────────────────

        /// <summary>
        /// Asks the picture AI where <paramref name="what"/> is on the frame at
        /// <paramref name="fileTime"/>. Returns the box, or null with a reason
        /// ("no_ai", "not_found", "no_frame", or the error text).
        /// </summary>
        public static async Task<(TrackPoint Box, string Problem)> LocateAsync(string path, double fileTime, string what, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(what)) return (null, "not_found");
            string model = await FrameDescriber.FindModelAsync();
            if (!GeminiClient.HasKey && model == null) return (null, "no_ai");

            string jpg = Path.Combine(Path.GetTempPath(), $"uve_track_{Guid.NewGuid():N}.jpg");
            try
            {
                string ss = fileTime > 0.01 ? $"-ss {N(fileTime)} " : "";
                await RunAsync($"-nostdin -y -hide_banner -loglevel error {ss}-i \"{path}\" -map 0:v:0 -frames:v 1 -vf \"scale=768:-2\" -q:v 3 -update 1 \"{jpg}\"", ct);
                if (!File.Exists(jpg) || new FileInfo(jpg).Length < 100) return (null, "no_frame");
                var (iw, ih) = JpegSize(jpg);

                string prompt =
                    "Find this in the picture: \"" + what.Trim().Replace("\"", "'") + "\". " +
                    "Answer ONLY with JSON, no other text: {\"found\": true or false, \"box_2d\": [ymin, xmin, ymax, xmax]} " +
                    "with the coordinates normalised to 0-1000. If there are several, choose the most prominent one. " +
                    "If it is not in the picture, answer {\"found\": false}.";
                string answer = null;
                if (GeminiClient.HasKey)
                {
                    try { answer = await GeminiClient.AskAsync(prompt, Convert.ToBase64String(await File.ReadAllBytesAsync(jpg, ct)), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { AppLog.Error("Object tracking: Gemini", ex); }
                }
                if (string.IsNullOrWhiteSpace(answer) && model != null)
                {
                    var (resp, err) = await new OllamaClient().VisionAsyncEx(jpg, prompt, model, ct);
                    answer = resp;
                    if (string.IsNullOrWhiteSpace(answer) && !string.IsNullOrEmpty(err)) return (null, err);
                }
                return ParseBox(answer, iw, ih);
            }
            finally { try { File.Delete(jpg); } catch { } }
        }

        /// <summary>Reads a box from the AI's answer: Gemini's [ymin, xmin, ymax, xmax] on 0–1000, or pixels.</summary>
        internal static (TrackPoint, string) ParseBox(string answer, int imgW, int imgH)
        {
            if (string.IsNullOrWhiteSpace(answer)) return (null, "not_found");
            if (Regex.IsMatch(answer, "\"found\"\\s*:\\s*false", RegexOptions.IgnoreCase)) return (null, "not_found");
            var m = Regex.Match(answer, @"\[\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\]");
            if (!m.Success) return (null, "not_found");
            double[] v = Enumerable.Range(1, 4).Select(i => double.Parse(m.Groups[i].Value, Inv)).ToArray();
            double ymin = v[0], xmin = v[1], ymax = v[2], xmax = v[3];
            if (v.Max() > 1000.5 && imgW > 0 && imgH > 0)
            {   // pixels of the picture that was sent
                ymin /= imgH; ymax /= imgH; xmin /= imgW; xmax /= imgW;
            }
            else if (v.Max() <= 1.0001) { /* already 0 … 1 */ }
            else { ymin /= 1000; ymax /= 1000; xmin /= 1000; xmax /= 1000; }
            if (xmax < xmin) (xmin, xmax) = (xmax, xmin);
            if (ymax < ymin) (ymin, ymax) = (ymax, ymin);
            xmin = Math.Clamp(xmin, 0, 1); xmax = Math.Clamp(xmax, 0, 1); ymin = Math.Clamp(ymin, 0, 1); ymax = Math.Clamp(ymax, 0, 1);
            if (xmax - xmin < 0.01 || ymax - ymin < 0.01) return (null, "not_found");
            return (new TrackPoint { X = (xmin + xmax) / 2, Y = (ymin + ymax) / 2, W = xmax - xmin, H = ymax - ymin }, null);
        }

        private static (int, int) JpegSize(string file)
        {
            try
            {
                var b = File.ReadAllBytes(file);
                for (int i = 2; i + 9 < b.Length;)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    int len = (b[i + 2] << 8) | b[i + 3];
                    if (marker >= 0xC0 && marker <= 0xC3) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + len;
                }
            }
            catch { }
            return (0, 0);
        }

        // ── following it ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Follows the object from <paramref name="start"/> (found at file time
        /// <paramref name="startTime"/>) forwards and backwards through the file part
        /// [<paramref name="from"/>, <paramref name="from"/> + <paramref name="length"/>].
        /// <paramref name="progress"/> gets 0 … 1. Returns the path, one point per frame looked at.
        /// </summary>
        public static async Task<List<TrackPoint>> TrackAsync(string path, double from, double length, double startTime, TrackPoint start,
                                                               Action<double> progress, CancellationToken ct)
        {
            length = Math.Max(0.2, length);
            double fps = Math.Min(10, 3000 / length);   // at most 3000 frames in memory
            const int FW = 320;
            var probe = new LayerCompositor(AppPaths.FFprobe, (a, c) => Task.FromResult(false), null);
            var (sw, sh) = await probe.ProbeSizeAsync(path, ct);
            if (sw <= 0 || sh <= 0) { sw = 1920; sh = 1080; }
            int fh = Math.Max(16, (int)Math.Round(FW * (double)sh / sw / 2) * 2);

            var frames = await ReadFramesAsync(path, from, length, fps, FW, fh, p => progress?.Invoke(p * 0.4), ct);
            if (frames.Count == 0) return new List<TrackPoint>();

            int k0 = Math.Clamp((int)Math.Round((startTime - from) * fps), 0, frames.Count - 1);
            var result = new TrackPoint[frames.Count];
            int done = 0;
            void Step() { done++; if (done % 10 == 0) progress?.Invoke(0.4 + 0.6 * done / frames.Count); }

            foreach (int dir in new[] { +1, -1 })
            {
                var tr = new Follower(frames[k0], FW, fh, start);
                if (dir > 0) { result[k0] = tr.Point(from + k0 / fps); Step(); }
                for (int k = k0 + dir; k >= 0 && k < frames.Count; k += dir)
                {
                    ct.ThrowIfCancellationRequested();
                    tr.Next(frames[k]);
                    result[k] = tr.Point(from + k / fps);
                    Step();
                    if (k % 50 == 0) await Task.Yield();
                }
            }
            progress?.Invoke(1);
            return Smooth(result.Where(p => p != null).ToList());
        }

        /// <summary>A light moving average (3 points) that leaves "lost" stretches as they are.</summary>
        private static List<TrackPoint> Smooth(List<TrackPoint> p)
        {
            if (p.Count < 3) return p;
            var o = new List<TrackPoint>(p.Count);
            for (int i = 0; i < p.Count; i++)
            {
                int a = Math.Max(0, i - 1), b = Math.Min(p.Count - 1, i + 1);
                var r = p.Skip(a).Take(b - a + 1).ToList();
                o.Add(new TrackPoint { T = p[i].T, X = r.Average(q => q.X), Y = r.Average(q => q.Y), W = r.Average(q => q.W), H = r.Average(q => q.H), Lost = p[i].Lost });
            }
            return o;
        }

        private static async Task<List<byte[]>> ReadFramesAsync(string path, double from, double length, double fps, int w, int h,
                                                                Action<double> progress, CancellationToken ct)
        {
            var list = new List<byte[]>();
            string args = $"-nostdin -hide_banner -loglevel error -ss {N(from)} -t {N(length)} -i \"{path}\" -map 0:v:0 " +
                          $"-vf \"fps={N(fps)},scale={w}:{h}:flags=area,format=gray\" -f rawvideo -pix_fmt gray -";
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.FFmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            p.Start();
            _ = p.StandardError.ReadToEndAsync();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                var s = p.StandardOutput.BaseStream;
                int size = w * h;
                double expected = Math.Max(1, length * fps);
                while (true)
                {
                    var buf = new byte[size];
                    int got = 0;
                    while (got < size)
                    {
                        int r = await s.ReadAsync(buf.AsMemory(got, size - got), ct).ConfigureAwait(false);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got < size) break;
                    list.Add(buf);
                    if (list.Count % 20 == 0) progress?.Invoke(Math.Min(1, list.Count / expected));
                }
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            return list;
        }

        /// <summary>Follows one object through grey frames by template matching.</summary>
        private sealed class Follower
        {
            private readonly int _w, _h;
            private double _cx, _cy, _scale = 1;   // centre in pixels, size relative to the template
            private readonly double _tw, _th;      // template size in pixels at scale 1
            private readonly int _gx, _gy;         // samples across / down
            private readonly double[] _tpl;        // template samples (zero mean, unit length)
            private double[] _raw;                 // template samples as grey values (for updating)
            private bool _lost;
            private int _lostFor;

            public Follower(byte[] frame, int w, int h, TrackPoint box)
            {
                _w = w; _h = h;
                _cx = box.X * w; _cy = box.Y * h;
                _tw = Math.Max(6, box.W * w); _th = Math.Max(6, box.H * h);
                _gx = Math.Clamp((int)Math.Round(_tw), 6, 24);
                _gy = Math.Clamp((int)Math.Round(_th), 6, 24);
                _raw = Sample(frame, _cx, _cy, 1);
                _tpl = Normalise(_raw);
            }

            public TrackPoint Point(double t) => new TrackPoint
            {
                T = t, X = Math.Clamp(_cx / _w, 0, 1), Y = Math.Clamp(_cy / _h, 0, 1),
                W = Math.Clamp(_tw * _scale / _w, 0.005, 1), H = Math.Clamp(_th * _scale / _h, 0.005, 1), Lost = _lost
            };

            private double[] Sample(byte[] f, double cx, double cy, double scale)
            {
                var s = new double[_gx * _gy];
                double sw = _tw * scale, sh = _th * scale;
                int i = 0;
                for (int y = 0; y < _gy; y++)
                {
                    double py = cy - sh / 2 + (y + 0.5) * sh / _gy;
                    int iy = Math.Clamp((int)py, 0, _h - 1);
                    for (int x = 0; x < _gx; x++)
                    {
                        double px = cx - sw / 2 + (x + 0.5) * sw / _gx;
                        int ix = Math.Clamp((int)px, 0, _w - 1);
                        s[i++] = f[iy * _w + ix];
                    }
                }
                return s;
            }

            private static double[] Normalise(double[] s)
            {
                double mean = s.Average();
                var o = new double[s.Length];
                double len = 0;
                for (int i = 0; i < s.Length; i++) { o[i] = s[i] - mean; len += o[i] * o[i]; }
                len = Math.Sqrt(len);
                if (len < 1e-6) return o;
                for (int i = 0; i < o.Length; i++) o[i] /= len;
                return o;
            }

            private double Score(byte[] f, double cx, double cy, double scale)
            {
                var s = Sample(f, cx, cy, scale);
                double mean = s.Average(), len = 0, dot = 0;
                for (int i = 0; i < s.Length; i++) { double v = s[i] - mean; len += v * v; dot += v * _tpl[i]; }
                return len < 1e-6 ? 0 : dot / Math.Sqrt(len);
            }

            public void Next(byte[] f)
            {
                double size = Math.Max(_tw, _th) * _scale;
                double radius = Math.Max(6, size * 0.6) * (_lost ? Math.Min(4, 1.5 + _lostFor * 0.25) : 1);
                double best = -2, bx = _cx, by = _cy;
                // coarse search, then fine
                double stepC = Math.Max(1, radius / 10);
                for (double dy = -radius; dy <= radius; dy += stepC)
                    for (double dx = -radius; dx <= radius; dx += stepC)
                    {
                        double sc = Score(f, _cx + dx, _cy + dy, _scale);
                        if (sc > best) { best = sc; bx = _cx + dx; by = _cy + dy; }
                    }
                double cbx = bx, cby = by;
                for (double dy = -stepC; dy <= stepC; dy += 1)
                    for (double dx = -stepC; dx <= stepC; dx += 1)
                    {
                        double sc = Score(f, cbx + dx, cby + dy, _scale);
                        if (sc > best) { best = sc; bx = cbx + dx; by = cby + dy; }
                    }
                // a little change of size
                double bs = _scale;
                foreach (double m in new[] { 0.94, 1.06 })
                {
                    double ns = Math.Clamp(_scale * m, 0.3, 3);
                    double sc = Score(f, bx, by, ns);
                    if (sc > best + 0.01) { best = sc; bs = ns; }
                }

                if (best < 0.45)
                {
                    _lost = true; _lostFor++;
                    return;   // keep the last place
                }
                if (_lost && best < 0.6) { _lostFor++; return; }
                _lost = false; _lostFor = 0;
                _cx = Math.Clamp(bx, 0, _w); _cy = Math.Clamp(by, 0, _h); _scale = bs;
                if (best > 0.75)
                {
                    // follow slow changes of look (turning, light)
                    var now = Sample(f, _cx, _cy, _scale);
                    for (int i = 0; i < _raw.Length; i++) _raw[i] = _raw[i] * 0.88 + now[i] * 0.12;
                    var nt = Normalise(_raw);
                    Array.Copy(nt, _tpl, nt.Length);
                }
            }
        }

        // ── describing and simplifying the path ─────────────────────────────────────

        /// <summary>Seconds followed and the stretches where the object was lost.</summary>
        public static (double Followed, List<(double From, double To)> Lost) Summary(List<TrackPoint> path)
        {
            var lost = new List<(double, double)>();
            if (path == null || path.Count == 0) return (0, lost);
            double? ls = null;
            foreach (var p in path)
            {
                if (p.Lost && ls == null) ls = p.T;
                if (!p.Lost && ls != null) { lost.Add((ls.Value, p.T)); ls = null; }
            }
            if (ls != null) lost.Add((ls.Value, path[^1].T));
            return (path[^1].T - path[0].T, lost);
        }

        /// <summary>
        /// Fewer points for drawing (Ramer–Douglas–Peucker on position and size, at most
        /// <paramref name="max"/> points), so the FFmpeg command stays short.
        /// </summary>
        public static List<TrackPoint> Simplify(List<TrackPoint> path, int max = 40)
        {
            if (path == null || path.Count <= 2) return path?.ToList() ?? new List<TrackPoint>();
            double eps = 0.004;
            List<TrackPoint> r;
            do
            {
                var keep = new bool[path.Count];
                keep[0] = keep[^1] = true;
                Rdp(path, 0, path.Count - 1, eps, keep);
                r = path.Where((p, i) => keep[i]).ToList();
                eps *= 1.6;
            } while (r.Count > max && eps < 1);
            return r;
        }

        private static void Rdp(List<TrackPoint> p, int a, int b, double eps, bool[] keep)
        {
            if (b <= a + 1) return;
            double best = 0; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double k = (p[i].T - p[a].T) / Math.Max(1e-6, p[b].T - p[a].T);
                double d = Math.Max(Math.Max(Math.Abs(p[i].X - (p[a].X + (p[b].X - p[a].X) * k)), Math.Abs(p[i].Y - (p[a].Y + (p[b].Y - p[a].Y) * k))),
                                    Math.Max(Math.Abs(p[i].W - (p[a].W + (p[b].W - p[a].W) * k)), Math.Abs(p[i].H - (p[a].H + (p[b].H - p[a].H) * k))));
                if (d > best) { best = d; idx = i; }
            }
            if (best > eps && idx > 0)
            {
                keep[idx] = true;
                Rdp(p, a, idx, eps, keep);
                Rdp(p, idx, b, eps, keep);
            }
        }

        private static async Task RunAsync(string args, CancellationToken ct)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.FFmpeg, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
                }
            };
            p.Start();
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                await p.WaitForExitAsync(ct);
        }
    }
}
