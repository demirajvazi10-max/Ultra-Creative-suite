using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — mask shapes for layers. Every shape is a polygon in a unit box (-1 … 1 on both
    /// axes, y down); it is placed in the mask's box and drawn into a grey picture
    /// (white = the layer shows) with a soft edge. The picture is written as a PGM file,
    /// which FFmpeg reads directly — no FFmpeg run and no per-pixel formula per frame.
    /// </summary>
    public static class MaskShapes
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The shapes, in the order they are offered.</summary>
        public static readonly string[] Shapes =
            { "None", "Ellipse", "Rectangle", "RoundedRectangle", "Diamond", "Triangle", "Star", "Heart", "Custom" };

        public static bool IsShape(string s) => !string.IsNullOrEmpty(s) && s != "None" && Shapes.Contains(s);

        /// <summary>The shape as a polygon in the unit box; null when there is none (or the custom points are not usable).</summary>
        public static List<(double X, double Y)> UnitPolygon(string shape, string customPoints)
        {
            var p = new List<(double, double)>();
            switch (shape)
            {
                case "Ellipse":
                    for (int i = 0; i < 96; i++) { double a = i * 2 * Math.PI / 96; p.Add((Math.Cos(a), Math.Sin(a))); }
                    break;
                case "Rectangle":
                    p.AddRange(new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) });
                    break;
                case "RoundedRectangle":
                {
                    const double r = 0.3;
                    (double cx, double cy, double a0)[] corners = { (1 - r, -1 + r, -90), (1 - r, 1 - r, 0), (-1 + r, 1 - r, 90), (-1 + r, -1 + r, 180) };
                    foreach (var (cx, cy, a0) in corners)
                        for (int i = 0; i <= 12; i++)
                        {
                            double a = (a0 + i * 90.0 / 12) * Math.PI / 180;
                            p.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                        }
                    break;
                }
                case "Diamond":
                    p.AddRange(new[] { (0.0, -1.0), (1.0, 0.0), (0.0, 1.0), (-1.0, 0.0) });
                    break;
                case "Triangle":
                    p.AddRange(new[] { (0.0, -1.0), (1.0, 1.0), (-1.0, 1.0) });
                    break;
                case "Star":
                    for (int i = 0; i < 10; i++)
                    {
                        double a = (-90 + i * 36) * Math.PI / 180, rr = i % 2 == 0 ? 1.0 : 0.42;
                        p.Add((rr * Math.Cos(a), rr * Math.Sin(a) * 1.05 + 0.08));
                    }
                    break;
                case "Heart":
                    for (int i = 0; i < 80; i++)
                    {
                        double t = i * 2 * Math.PI / 80;
                        double x = 16 * Math.Pow(Math.Sin(t), 3);
                        double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
                        p.Add((x / 17.0, -y / 15.5 + 0.1));
                    }
                    break;
                case "Custom":
                    if (!TryParsePoints(customPoints, out var pts, out _)) return null;
                    p.AddRange(pts.Select(q => (q.X / 50.0 - 1, q.Y / 50.0 - 1)));
                    break;
                default:
                    return null;
            }
            return p;
        }

        /// <summary>
        /// Custom points: "x y; x y; …" in percent of the mask box (0 0 = top left, 100 100 =
        /// bottom right), at least three. Commas between x and y work too.
        /// </summary>
        public static bool TryParsePoints(string text, out List<(double X, double Y)> points, out string error)
        {
            points = new List<(double, double)>();
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty"; return false; }
            foreach (string part in text.Split(new[] { ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var nums = part.Replace(',', ' ').Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length == 0) continue;
                if (nums.Length != 2 || !double.TryParse(nums[0], NumberStyles.Float, Inv, out double x) ||
                    !double.TryParse(nums[1], NumberStyles.Float, Inv, out double y))
                { error = part.Trim(); return false; }
                points.Add((Math.Clamp(x, 0, 100), Math.Clamp(y, 0, 100)));
            }
            if (points.Count < 3) { error = "few"; return false; }
            return true;
        }

        /// <summary>
        /// Draws a polygon (pixel coordinates) into a w × h grey picture: 255 inside, 0 outside,
        /// a soft band <paramref name="featherPx"/> wide inside the edge. Inverted on request.
        /// </summary>
        public static byte[] Rasterize(int w, int h, List<(double X, double Y)> poly, double featherPx, bool invert)
        {
            var data = new byte[w * h];
            byte outside = (byte)(invert ? 255 : 0);
            if (poly == null || poly.Count < 3)
            {
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(invert ? 0 : 255);
                return data;
            }
            double minX = poly.Min(q => q.X), maxX = poly.Max(q => q.X), minY = poly.Min(q => q.Y), maxY = poly.Max(q => q.Y);
            int n = poly.Count;
            double f = Math.Max(0.5, featherPx);
            for (int y = 0; y < h; y++)
            {
                double py = y + 0.5;
                for (int x = 0; x < w; x++)
                {
                    double px = x + 0.5;
                    byte v = outside;
                    if (px >= minX && px <= maxX && py >= minY && py <= maxY)
                    {
                        bool inside = false;
                        double best = double.MaxValue;
                        for (int i = 0, j = n - 1; i < n; j = i++)
                        {
                            var (xi, yi) = poly[i];
                            var (xj, yj) = poly[j];
                            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
                            double d = SegDist2(px, py, xi, yi, xj, yj);
                            if (d < best) best = d;
                        }
                        if (inside)
                        {
                            double a = Math.Clamp(Math.Sqrt(best) / f, 0, 1);
                            a = a * a * (3 - 2 * a);   // smooth edge
                            int val = (int)Math.Round(255 * a);
                            v = (byte)(invert ? 255 - val : val);
                        }
                    }
                    data[y * w + x] = v;
                }
            }
            return data;
        }

        private static double SegDist2(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len = dx * dx + dy * dy;
            double t = len < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len, 0, 1);
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return qx * qx + qy * qy;
        }

        /// <summary>Writes a binary PGM (P5) picture.</summary>
        public static void WritePgm(string path, int w, int h, byte[] data)
        {
            using var fs = File.Create(path);
            var head = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
            fs.Write(head, 0, head.Length);
            fs.Write(data, 0, data.Length);
        }

        /// <summary>
        /// The whole-frame mask of a layer (the shape in its box), drawn at most 640 pixels wide
        /// (FFmpeg scales it to the layer). Returns false when the layer has no usable mask.
        /// </summary>
        public static bool WriteFrameMask(LayerSettings ls, int layerW, int layerH, string file)
        {
            var unit = UnitPolygon(ls?.Mask, ls?.MaskPoints);
            if (unit == null) return false;
            double k = Math.Min(1.0, 640.0 / Math.Max(1, layerW));
            int w = Math.Max(8, (int)Math.Round(layerW * k)), h = Math.Max(8, (int)Math.Round(layerH * k));
            double bw = w * Math.Clamp(ls.MaskW, 2, 400) / 100.0, bh = h * Math.Clamp(ls.MaskH, 2, 400) / 100.0;
            double cx = w * ls.MaskX / 100.0, cy = h * ls.MaskY / 100.0;
            var poly = unit.Select(q => (cx + q.X * bw / 2, cy + q.Y * bh / 2)).ToList();
            double feather = Math.Clamp(ls.MaskFeather, 0, 50) / 100.0 * Math.Min(bw, bh);
            WritePgm(file, w, h, Rasterize(w, h, poly, feather, ls.MaskInvert));
            return true;
        }

        /// <summary>
        /// The mask for a moving mask: only the box, with the shape filling it (at most 512
        /// pixels on its longer side). Inverted, the box is white around a black shape and is
        /// laid on a white picture.
        /// </summary>
        public static bool WriteBoxMask(LayerSettings ls, double boxAspect, string file)
        {
            var unit = UnitPolygon(ls?.Mask, ls?.MaskPoints);
            if (unit == null) return false;
            boxAspect = Math.Clamp(boxAspect, 0.05, 20);
            int w = boxAspect >= 1 ? 512 : Math.Max(8, (int)Math.Round(512 * boxAspect));
            int h = boxAspect >= 1 ? Math.Max(8, (int)Math.Round(512 / boxAspect)) : 512;
            var poly = unit.Select(q => (w / 2.0 + q.X * w / 2.0, h / 2.0 + q.Y * h / 2.0)).ToList();
            double feather = Math.Clamp(ls.MaskFeather, 0, 50) / 100.0 * Math.Min(w, h);
            WritePgm(file, w, h, Rasterize(w, h, poly, feather, ls.MaskInvert));
            return true;
        }
    }
}
