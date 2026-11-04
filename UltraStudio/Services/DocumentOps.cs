using System;
using System.Collections.Generic;
using System.Linq;
using UltraStudio.Localization;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Strane dokumenta. Trenutna strana je uvek "raspakovana" u glavnim poljima
    /// projekta; ostale su JSON snimci u Pages. Svaka operacija prvo upiše trenutnu
    /// stranu nazad (Sync), pa tek onda menja listu.
    /// </summary>
    public static class PageOps
    {
        public static string PageJson(ImageProject p) => ProjectJson.Serialize(ProjectJson.Clone(p));

        public static void Sync(ImageProject p)
        {
            string j = PageJson(p);
            if (p.Pages.Count == 0) { p.Pages.Add(j); p.CurrentPage = 0; }
            else p.Pages[Math.Clamp(p.CurrentPage, 0, p.Pages.Count - 1)] = j;
        }

        private static void Load(ImageProject p, int i)
        {
            var pages = p.Pages;
            var path = p.ProjectPath;
            ProjectJson.Restore(p, pages[i]);
            p.Pages = pages;
            p.CurrentPage = i;
            p.ProjectPath = path;
        }

        public static void GoTo(ImageProject p, int index)
        {
            if (p.Pages.Count <= 1) return;
            index = Math.Clamp(index, 0, p.Pages.Count - 1);
            if (index == p.CurrentPage) return;
            Sync(p);
            Load(p, index);
        }

        /// <summary>Prazna strana iste veličine, pozadine i rezolucije kao trenutna.</summary>
        public static string BlankLike(ImageProject p, int? w = null, int? h = null)
        {
            var b = new ImageProject
            {
                CanvasWidth = w ?? p.CanvasWidth, CanvasHeight = h ?? p.CanvasHeight,
                CanvasBackgroundHex = p.HasImage ? "#FFFFFF" : p.CanvasBackgroundHex,
                BlankCanvas = true, Dpi = p.Dpi, NextLayerNumber = p.NextLayerNumber
            };
            return ProjectJson.Serialize(b);
        }

        public static void Insert(ImageProject p, string pageJson, int index)
        {
            Sync(p);
            index = Math.Clamp(index, 0, p.Pages.Count);
            p.Pages.Insert(index, pageJson);
            Load(p, index);
        }

        public static void Duplicate(ImageProject p)
        {
            Sync(p);
            var copy = new ImageProject();
            ProjectJson.Restore(copy, p.Pages[p.CurrentPage]);
            foreach (var l in copy.Layers) l.Id = Guid.NewGuid();
            p.Pages.Insert(p.CurrentPage + 1, PageJson(copy));
            Load(p, p.CurrentPage + 1);
        }

        public static void Delete(ImageProject p)
        {
            if (p.Pages.Count <= 1) throw new InvalidOperationException(Lang.T("page_last"));
            int i = p.CurrentPage;
            p.Pages.RemoveAt(i);
            Load(p, Math.Min(i, p.Pages.Count - 1));
            if (p.Pages.Count == 1) p.Pages.Clear(); // opet obična slika od jedne strane
        }

        public static bool Move(ImageProject p, int dir)
        {
            if (p.Pages.Count <= 1) return false;
            int i = p.CurrentPage, t = i + dir;
            if (t < 0 || t >= p.Pages.Count) return false;
            Sync(p);
            (p.Pages[i], p.Pages[t]) = (p.Pages[t], p.Pages[i]);
            p.CurrentPage = t;
            return true;
        }

        /// <summary>Sve strane kao zasebni projekti (za PDF, izvoz i proveru).</summary>
        public static List<ImageProject> All(ImageProject p)
        {
            if (p.Pages.Count == 0) return new List<ImageProject> { ProjectJson.Clone(p) };
            Sync(p);
            return p.Pages.Select(j => { var c = new ImageProject(); ProjectJson.Restore(c, j); c.Pages.Clear(); return c; }).ToList();
        }

        /// <summary>Menja sve putanje fajlova u snimku strane (za pakovanje .ustudio fajla).</summary>
        public static string MapPaths(string pageJson, Func<string?, string?> map)
        {
            var pg = new ImageProject();
            ProjectJson.Restore(pg, pageJson);
            pg.OriginalPath = map(pg.OriginalPath);
            pg.BasePath = map(pg.BasePath);
            pg.Selection.MaskPath = map(pg.Selection.MaskPath);
            foreach (var l in pg.Layers.OfType<ImageLayer>()) l.SourcePath = map(l.SourcePath) ?? "";
            pg.Pages.Clear();
            return ProjectJson.Serialize(pg);
        }

        /// <summary>Kratak opis strane za listu strana.</summary>
        public static string Summary(ImageProject page, int index, int total)
        {
            var firstText = page.Layers.OfType<TextLayer>().OrderBy(t => t.Y).FirstOrDefault(t => t.Role != "page_number" && !string.IsNullOrWhiteSpace(t.Text));
            string what = firstText != null
                ? "„" + Shorten(firstText.Text, 40) + "“"
                : page.HasImage ? Lang.T("page_has_photo") : Lang.T("page_empty");
            return string.Format(Lang.T("page_summary"), index + 1, total, page.Layers.Count, what);
        }

        private static string Shorten(string s, int n)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > n ? s[..(n - 1)] + "…" : s;
        }
    }

    /// <summary>Tekst kroz više strana i brojevi strana.</summary>
    public static class DocumentOps
    {
        public sealed class FlowOptions
        {
            public string FontFamily = "Georgia";
            public double FontSizePx = 46;       // 11 pt na 300 dpi
            public double LineSpacing = 1.35;
            public TextAlign Align = TextAlign.Justify;
            public string ColorHex = "#1A1A1A";
            public double MarginPx = 236;        // 2 cm na 300 dpi
            public bool PageNumbers = true;
        }

        public static double PtToPx(double pt, double dpi) => pt * dpi / 72.0;
        public static double PxToPt(double px, double dpi) => px * 72.0 / dpi;
        public static double MmToPx(double mm, double dpi) => mm / 25.4 * dpi;

        /// <summary>
        /// Raspoređuje dugačak tekst u tekstualne okvire na onoliko strana koliko treba.
        /// Ako je trenutna strana prazna, tekst počinje na njoj; inače se strane dodaju posle nje.
        /// Vraća broj strana sa tekstom.
        /// </summary>
        public static int FlowText(ImageProject p, string text, FlowOptions o)
        {
            int W = p.CanvasWidth, H = p.CanvasHeight;
            double footer = o.PageNumbers ? o.FontSizePx * 2.2 : 0;
            var frame = new TextLayer
            {
                FontFamily = o.FontFamily, FontSize = o.FontSizePx, LineSpacing = o.LineSpacing, Align = o.Align,
                ColorHex = o.ColorHex, Wrap = true, AutoSize = false,
                X = o.MarginPx, Y = o.MarginPx, Width = Math.Max(10, W - 2 * o.MarginPx), Height = Math.Max(o.FontSizePx, H - 2 * o.MarginPx - footer)
            };
            var lines = TextLayout.Lines(frame, text.Replace("\r\n", "\n").Trim('\n'), frame.Width);
            int perPage = Math.Max(1, TextLayout.VisibleLineCount(frame));

            // Delovi teksta po stranama — svaka strana dobija tačno one redove koji u nju staju
            var chunks = new List<string>();
            for (int start = 0; start < lines.Count; start += perPage)
            {
                var sb = new System.Text.StringBuilder();
                var group = lines.Skip(start).Take(perPage).ToList();
                // prazan red na vrhu strane nema smisla
                while (group.Count > 1 && group[0].Text.Length == 0 && group[0].EndsParagraph) group.RemoveAt(0);
                for (int i = 0; i < group.Count; i++)
                {
                    sb.Append(group[i].Text);
                    if (i < group.Count - 1) sb.Append(group[i].EndsParagraph ? "\n" : " ");
                }
                chunks.Add(sb.ToString());
            }
            if (chunks.Count == 0) return 0;

            bool currentEmpty = !p.HasImage && p.Layers.Count == 0;
            int insertAt = p.Pages.Count == 0 ? 1 : p.CurrentPage + 1;
            for (int i = 0; i < chunks.Count; i++)
            {
                var layer = new TextLayer
                {
                    Name = Lang.T("frame_name") + " " + (i + 1), Text = chunks[i], FontFamily = frame.FontFamily, FontSize = frame.FontSize,
                    LineSpacing = frame.LineSpacing, Align = frame.Align, ColorHex = frame.ColorHex, Wrap = true, AutoSize = false,
                    X = frame.X, Y = frame.Y, Width = frame.Width, Height = frame.Height, Role = "flow"
                };
                if (i == 0 && currentEmpty)
                {
                    p.Layers.Add(layer);
                    p.BlankCanvas = true;
                    continue;
                }
                var page = new ImageProject();
                ProjectJson.Restore(page, PageOps.BlankLike(p));
                page.Layers.Add(layer);
                PageOps.Insert(p, PageOps.PageJson(page), insertAt++);
            }
            if (o.PageNumbers) AddPageNumbers(p, 0, 0, o.FontSizePx * 0.85, o.ColorHex, o.FontFamily);
            return chunks.Count;
        }

        /// <summary>
        /// Brojevi na svim stranama (stari se zamenjuju). format: 0 = "1", 1 = "Strana 1", 2 = "1 / 10", 3 = "— 1 —".
        /// position: 0 = dole u sredini, 1 = dole desno, 2 = dole spolja (desno na neparnim, levo na parnim), 3 = gore desno.
        /// </summary>
        public static void AddPageNumbers(ImageProject p, int format, int position, double fontSizePx, string colorHex, string fontFamily = "Segoe UI")
        {
            int current = p.CurrentPage;
            PageOps.Sync(p);
            int total = p.Pages.Count;
            for (int i = 0; i < total; i++)
            {
                var pg = new ImageProject();
                ProjectJson.Restore(pg, p.Pages[i]);
                pg.Pages.Clear();
                foreach (var old in pg.Layers.Where(l => l.Role == "page_number").ToList()) pg.Layers.Remove(old);
                string text = format switch
                {
                    1 => string.Format(Lang.T("page_number_word"), i + 1),
                    2 => $"{i + 1} / {total}",
                    3 => $"— {i + 1} —",
                    _ => (i + 1).ToString()
                };
                var t = new TextLayer
                {
                    Name = Lang.T("page_number_layer"), Role = "page_number", Text = text, FontSize = fontSizePx,
                    FontFamily = fontFamily, ColorHex = colorHex, AutoSize = true
                };
                var (w, h) = CanvasEngine.MeasureText(t);
                t.Width = w; t.Height = h;
                double m = Math.Min(pg.CanvasWidth, pg.CanvasHeight) * 0.05;
                bool right = position == 1 || position == 3 || (position == 2 && i % 2 == 0);
                t.Y = position == 3 ? m * 0.6 : pg.CanvasHeight - m * 0.6 - h;
                t.X = position == 0 ? (pg.CanvasWidth - w) / 2 : right ? pg.CanvasWidth - m - w : m;
                t.Align = position == 0 ? TextAlign.Center : right ? TextAlign.Right : TextAlign.Left;
                t.X = Math.Round(t.X); t.Y = Math.Round(t.Y);
                pg.Layers.Add(t);
                p.Pages[i] = PageOps.PageJson(pg);
            }
            // vrati trenutnu stranu iz osveženog snimka
            var pages = p.Pages; var path = p.ProjectPath;
            ProjectJson.Restore(p, pages[current]);
            p.Pages = pages; p.CurrentPage = current; p.ProjectPath = path;
        }

        /// <summary>Izmena svake strane dokumenta (trenutna se posle ponovo raspakuje).</summary>
        public static void ForEachPage(ImageProject p, Action<ImageProject> change)
        {
            if (p.Pages.Count == 0) { change(p); return; }
            int current = p.CurrentPage;
            PageOps.Sync(p);
            for (int i = 0; i < p.Pages.Count; i++)
            {
                var pg = new ImageProject();
                ProjectJson.Restore(pg, p.Pages[i]);
                pg.Pages.Clear();
                change(pg);
                p.Pages[i] = PageOps.PageJson(pg);
            }
            var pages = p.Pages; var path = p.ProjectPath;
            ProjectJson.Restore(p, pages[current]);
            p.Pages = pages; p.CurrentPage = current; p.ProjectPath = path;
        }

        /// <summary>Svi tekst-slojevi celog dokumenta (sve strane), za tipografiju, pismo i lekturu.</summary>
        public static int ForEachTextLayer(ImageProject p, bool allPages, Func<TextLayer, bool> change)
        {
            int changed = 0;
            if (!allPages || p.Pages.Count == 0)
            {
                foreach (var t in p.Layers.OfType<TextLayer>()) if (change(t)) changed++;
                return changed;
            }
            int current = p.CurrentPage;
            PageOps.Sync(p);
            for (int i = 0; i < p.Pages.Count; i++)
            {
                var pg = new ImageProject();
                ProjectJson.Restore(pg, p.Pages[i]);
                pg.Pages.Clear();
                bool any = false;
                foreach (var t in pg.Layers.OfType<TextLayer>()) if (change(t)) { changed++; any = true; }
                if (any) p.Pages[i] = PageOps.PageJson(pg);
            }
            var pages = p.Pages; var path = p.ProjectPath;
            ProjectJson.Restore(p, pages[current]);
            p.Pages = pages; p.CurrentPage = current; p.ProjectPath = path;
            return changed;
        }
    }
}
