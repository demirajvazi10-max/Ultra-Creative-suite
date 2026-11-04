using System;
using System.Collections.Generic;
using System.Linq;
using ImageMagick;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Raspored teksta u redove — ISTI za pregled, izvoz slike i PDF, pa se prelom
    /// reči nigde ne razlikuje. Širine reči se mere stvarnim fontom (Magick) i keširaju.
    /// Sve mere su u pikselima platna (pune rezolucije).
    /// </summary>
    public static class TextLayout
    {
        public sealed class Line
        {
            public string Text = "";
            public bool EndsParagraph;
            public List<(string word, double width)> Words = new();
            public double Width;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, double> _widths = new();

        private static string FontKey(TextLayer t) => $"{t.FontFamily}|{t.FontSize:0.##}|{(t.Bold ? 1 : 0)}{(t.Italic ? 1 : 0)}|";

        public static double Measure(TextLayer t, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string key = FontKey(t) + text;
            lock (_lock) { if (_widths.TryGetValue(key, out var w)) return w; }
            double width;
            try
            {
                using var m = new MagickImage(MagickColors.Transparent, 1, 1);
                m.Settings.FontFamily = t.FontFamily;
                m.Settings.FontPointsize = t.FontSize;
                m.Settings.FontWeight = t.Bold ? FontWeight.Bold : FontWeight.Normal;
                m.Settings.FontStyle = t.Italic ? FontStyleType.Italic : FontStyleType.Normal;
                var metrics = m.FontTypeMetrics(text);
                width = metrics?.TextWidth ?? 0;
                if (width <= 0) width = text.Length * t.FontSize * 0.5;
            }
            catch { width = text.Length * t.FontSize * 0.5; }
            lock (_lock)
            {
                if (_widths.Count > 20000) _widths.Clear();
                _widths[key] = width;
            }
            return width;
        }

        public static double SpaceWidth(TextLayer t) => Math.Max(t.FontSize * 0.2, Measure(t, "x x") - Measure(t, "xx"));

        public static double LineHeight(TextLayer t) => t.FontSize * Math.Max(0.5, t.LineSpacing);

        /// <summary>Redovi teksta. Bez prelamanja: jedan red po pasusu. Sa prelamanjem: reči se slažu do širine okvira.</summary>
        public static List<Line> Lines(TextLayer t) => Lines(t, t.Text ?? "", t.Wrap ? t.Width : double.MaxValue);

        public static List<Line> Lines(TextLayer t, string text, double maxWidth)
        {
            var result = new List<Line>();
            var paragraphs = text.Replace("\r\n", "\n").Split('\n');
            bool wrap = maxWidth < double.MaxValue / 2;
            double space = wrap ? SpaceWidth(t) : 0;
            foreach (var para in paragraphs)
            {
                if (!wrap)
                {
                    result.Add(new Line { Text = para, EndsParagraph = true });
                    continue;
                }
                // Nevidljivi razmak (NBSP) lepi reči — ne lomi se na njemu
                var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) { result.Add(new Line { Text = "", EndsParagraph = true }); continue; }
                var cur = new Line();
                foreach (var w in words)
                {
                    double ww = Measure(t, w);
                    double needed = cur.Words.Count == 0 ? ww : cur.Width + space + ww;
                    if (cur.Words.Count > 0 && needed > maxWidth)
                    {
                        cur.Text = string.Join(" ", cur.Words.Select(x => x.word));
                        result.Add(cur);
                        cur = new Line();
                        needed = ww;
                    }
                    cur.Words.Add((w, ww));
                    cur.Width = needed;
                }
                cur.Text = string.Join(" ", cur.Words.Select(x => x.word));
                cur.EndsParagraph = true;
                result.Add(cur);
            }
            return result;
        }

        /// <summary>Koliko redova tekstualnog okvira staje u njegovu visinu.</summary>
        public static int VisibleLineCount(TextLayer t)
        {
            if (!t.Wrap) return int.MaxValue;
            double lh = LineHeight(t);
            // prvi red zauzima visinu fonta, svaki sledeći još po jedan razmak redova
            if (t.Height < t.FontSize) return 0;
            return 1 + (int)Math.Floor((t.Height - t.FontSize) / lh + 0.001);
        }

        /// <summary>Broj redova koji ne staju u okvir (0 = sve staje).</summary>
        public static int OverflowLines(TextLayer t)
        {
            if (!t.Wrap) return 0;
            return Math.Max(0, Lines(t).Count - VisibleLineCount(t));
        }
    }
}
