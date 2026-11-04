using System;
using System.Linq;
using ImageMagick;
using UltraStudio.Localization;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Destruktivne izmene radne slike. Svaka učita PUNU rezoluciju, izmeni je,
    /// upiše novi PNG u folder sesije i postavi BasePath. Pozivalac (MainWindow)
    /// pre toga zove Undo.Checkpoint, pa je sve ovde poništivo.
    /// Selekcija se poštuje svuda gde ima smisla (filter/popuna/brisanje samo u njoj).
    /// </summary>
    public static class EditOps
    {
        public static MagickImage LoadFull(ImageProject p, bool withAdjustments = false) =>
            CanvasEngine.RenderBase(p, null, out _, withAdjustments);

        public static void Commit(ImageProject p, MagickImage img, string prefix)
        {
            string path = Session.NewFile(prefix);
            img.ResetPage();
            ImageEngine.SavePng(img, path);
            p.BasePath = path;
            p.CanvasWidth = (int)img.Width;
            p.CanvasHeight = (int)img.Height;
        }

        // ════════════════════════════════════════════════════════════════
        // SELEKCIJA
        // ════════════════════════════════════════════════════════════════
        public static byte[] FullMask(ImageProject p) => ImageEngine.BuildMask(p.Selection, p.CanvasWidth, p.CanvasHeight, 1.0);

        public static (int x, int y, int w, int h)? SelectionBounds(ImageProject p)
        {
            var s = p.Selection;
            if (!s.IsActive) return null;
            if ((s.Kind == SelectionKind.Rectangle || s.Kind == SelectionKind.Ellipse) && !s.Inverted && s.Feather <= 0)
            {
                int x0 = (int)Math.Max(0, Math.Floor(s.X)), y0 = (int)Math.Max(0, Math.Floor(s.Y));
                int x1 = (int)Math.Min(p.CanvasWidth, Math.Ceiling(s.X + s.Width)), y1 = (int)Math.Min(p.CanvasHeight, Math.Ceiling(s.Y + s.Height));
                if (x1 <= x0 || y1 <= y0) return null;
                return (x0, y0, x1 - x0, y1 - y0);
            }
            return PixelOps.MaskBounds(FullMask(p), p.CanvasWidth, p.CanvasHeight, 20);
        }

        public static void SetMask(ImageProject p, byte[] gray, int w, int h, string label)
        {
            string path = Session.NewFile("mask");
            ImageEngine.SaveGrayPng(gray, w, h, path);
            p.Selection = new Selection { Kind = SelectionKind.Mask, MaskPath = path, Label = label, X = 0, Y = 0, Width = p.CanvasWidth, Height = p.CanvasHeight };
        }

        /// <summary>Izgovorljiv opis selekcije: oblik, mesto, veličina, udeo slike.</summary>
        public static string DescribeSelection(ImageProject p)
        {
            var s = p.Selection;
            if (!s.IsActive) return Lang.T("sel_none");
            var b = SelectionBounds(p);
            double cov = PixelOps.Coverage(ImageEngine.BuildMask(s, Math.Max(1, p.CanvasWidth / 4), Math.Max(1, p.CanvasHeight / 4), 0.25)) * 100;
            string kind = s.Kind switch
            {
                SelectionKind.Rectangle => Lang.T("sel_kind_rect"),
                SelectionKind.Ellipse => Lang.T("sel_kind_ellipse"),
                _ => string.IsNullOrEmpty(s.Label) ? Lang.T("sel_kind_mask") : s.Label
            };
            string where = b == null ? "" : string.Format(Lang.T("sel_where"),
                Describe.Position(b.Value.x + b.Value.w / 2.0, b.Value.y + b.Value.h / 2.0, p.CanvasWidth, p.CanvasHeight),
                b.Value.w, b.Value.h, b.Value.x, b.Value.y);
            string extra = (s.Inverted ? " " + Lang.T("sel_inverted") : "") + (s.Feather > 0 ? " " + string.Format(Lang.T("sel_feathered"), s.Feather) : "");
            return string.Format(Lang.T("sel_summary"), kind, where, cov.ToString("0.#"), extra).Trim();
        }

        // ════════════════════════════════════════════════════════════════
        // GEOMETRIJA
        // ════════════════════════════════════════════════════════════════
        public static void Crop(ImageProject p, int x, int y, int w, int h)
        {
            x = Math.Clamp(x, 0, Math.Max(0, p.CanvasWidth - 1));
            y = Math.Clamp(y, 0, Math.Max(0, p.CanvasHeight - 1));
            w = Math.Clamp(w, 1, p.CanvasWidth - x);
            h = Math.Clamp(h, 1, p.CanvasHeight - y);
            if (p.HasImage)
            {
                using var img = LoadFull(p);
                img.Crop(new MagickGeometry(x, y, (uint)w, (uint)h));
                img.ResetPage();
                Commit(p, img, "crop");
            }
            else { p.CanvasWidth = w; p.CanvasHeight = h; }
            foreach (var l in p.Layers) { l.X -= x; l.Y -= y; }
            p.Selection = new Selection();
        }

        /// <summary>Najveći isečak zadate razmere, centriran oko tačke (podrazumevano centar slike).</summary>
        public static (int x, int y, int w, int h) RatioRect(int W, int H, double ratioW, double ratioH, double? cx = null, double? cy = null)
        {
            double target = ratioW / ratioH;
            int w, h;
            if ((double)W / H > target) { h = H; w = (int)Math.Round(H * target); }
            else { w = W; h = (int)Math.Round(W / target); }
            double centerX = cx ?? W / 2.0, centerY = cy ?? H / 2.0;
            int x = (int)Math.Round(Math.Clamp(centerX - w / 2.0, 0, W - w));
            int y = (int)Math.Round(Math.Clamp(centerY - h / 2.0, 0, H - h));
            return (x, y, w, h);
        }

        public static void Resize(ImageProject p, int nw, int nh)
        {
            double sx = (double)nw / p.CanvasWidth, sy = (double)nh / p.CanvasHeight, sa = Math.Sqrt(sx * sy);
            if (p.HasImage)
            {
                using var img = LoadFull(p);
                img.FilterType = FilterType.Lanczos;
                img.Resize(new MagickGeometry((uint)nw, (uint)nh) { IgnoreAspectRatio = true });
                Commit(p, img, "resize");
            }
            else { p.CanvasWidth = nw; p.CanvasHeight = nh; }
            foreach (var l in p.Layers)
            {
                l.X *= sx; l.Y *= sy; l.Width *= sx; l.Height *= sy;
                l.ShadowOffsetX *= sa; l.ShadowOffsetY *= sa; l.ShadowBlur *= sa; l.OutlineWidth *= sa; l.GlowSize *= sa;
                if (l is TextLayer t) t.FontSize *= sa;
                if (l is ShapeLayer sh) { sh.StrokeWidth *= sa; sh.CornerRadius *= sa; }
            }
            var s = p.Selection;
            s.X *= sx; s.Y *= sy; s.Width *= sx; s.Height *= sy; s.Feather *= sa;
        }

        /// <summary>anchor: 0..8 (gore-levo ... dole-desno, 4 = centar).</summary>
        public static void CanvasSize(ImageProject p, int nw, int nh, int anchor, string bgHex)
        {
            int ax = anchor % 3, ay = anchor / 3;
            int ox = ax == 0 ? 0 : ax == 1 ? (nw - p.CanvasWidth) / 2 : nw - p.CanvasWidth;
            int oy = ay == 0 ? 0 : ay == 1 ? (nh - p.CanvasHeight) / 2 : nh - p.CanvasHeight;
            if (p.HasImage)
            {
                using var src = LoadFull(p);
                var bg = string.Equals(bgHex, "transparent", StringComparison.OrdinalIgnoreCase) ? MagickColors.Transparent : ImageEngine.Color(bgHex, MagickColors.White);
                using var img = new MagickImage(bg, (uint)nw, (uint)nh);
                img.Composite(src, ox, oy, CompositeOperator.Over);
                Commit(p, img, "canvas");
            }
            else { p.CanvasWidth = nw; p.CanvasHeight = nh; if (!string.IsNullOrWhiteSpace(bgHex)) p.CanvasBackgroundHex = bgHex; }
            foreach (var l in p.Layers) { l.X += ox; l.Y += oy; }
            p.Selection.X += ox; p.Selection.Y += oy;
        }

        /// <summary>Okreće celu sliku (radnu sliku I slojeve) u smeru kazaljke.</summary>
        public static void Rotate(ImageProject p, double angle)
        {
            angle = ((angle % 360) + 360) % 360;
            if (angle == 0) return;
            double W0 = p.CanvasWidth, H0 = p.CanvasHeight;
            if (p.HasImage)
            {
                using var img = LoadFull(p);
                img.BackgroundColor = MagickColors.Transparent;
                img.Rotate(angle);
                img.ResetPage();
                Commit(p, img, "rotate");
            }
            else
            {
                double rad0 = angle * Math.PI / 180;
                double c = Math.Abs(Math.Cos(rad0)), s0 = Math.Abs(Math.Sin(rad0));
                p.CanvasWidth = (int)Math.Round(W0 * c + H0 * s0);
                p.CanvasHeight = (int)Math.Round(W0 * s0 + H0 * c);
            }
            double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
            foreach (var l in p.Layers)
            {
                double dx = l.CenterX - W0 / 2, dy = l.CenterY - H0 / 2;
                double nx = dx * cos - dy * sin, ny = dx * sin + dy * cos;
                l.X = p.CanvasWidth / 2.0 + nx - l.Width / 2;
                l.Y = p.CanvasHeight / 2.0 + ny - l.Height / 2;
                l.Rotation = ((l.Rotation + angle) % 360 + 360) % 360;
            }
            p.Selection = new Selection();
        }

        public static void Flip(ImageProject p, bool horizontal)
        {
            if (p.HasImage)
            {
                using var img = LoadFull(p);
                if (horizontal) img.Flop(); else img.Flip();
                Commit(p, img, "flip");
            }
            foreach (var l in p.Layers)
            {
                if (horizontal) l.X = p.CanvasWidth - l.X - l.Width; else l.Y = p.CanvasHeight - l.Y - l.Height;
                if (l.Rotation != 0) l.Rotation = (360 - l.Rotation) % 360;
            }
            var s = p.Selection;
            if (s.Kind == SelectionKind.Mask) p.Selection = new Selection();
            else if (horizontal) s.X = p.CanvasWidth - s.X - s.Width; else s.Y = p.CanvasHeight - s.Y - s.Height;
        }

        // ════════════════════════════════════════════════════════════════
        // PIKSELI (filter, popuna, brisanje, kopija u sloj, primena podešavanja)
        // ════════════════════════════════════════════════════════════════
        private static void ProcessWithinSelection(ImageProject p, string prefix, Action<MagickImage> process, bool withAdjustments = false)
        {
            using var img = LoadFull(p, withAdjustments);
            using var processed = (MagickImage)img.Clone();
            process(processed);
            if (p.Selection.IsActive) ImageEngine.BlendThroughMask(img, processed, FullMask(p));
            else ImageEngine.Assign(img, ImageEngine.ToRgba(processed), (int)processed.Width, (int)processed.Height);
            Commit(p, img, prefix);
        }

        public static void Filter(ImageProject p, FilterKind kind, double amount) =>
            ProcessWithinSelection(p, "filter", img => ImageEngine.ApplyFilter(img, kind, amount));

        public enum AutoKind { Tone, Contrast, Color }

        /// <summary>Auto ton (razvlačenje nivoa po kanalu), auto kontrast (zajedno, bez promene boja), auto boja (balans belog).</summary>
        public static void Auto(ImageProject p, AutoKind kind) =>
            ProcessWithinSelection(p, "auto", img =>
            {
                switch (kind)
                {
                    case AutoKind.Tone: img.AutoLevel(Channels.RGB); break;
                    case AutoKind.Contrast: img.ContrastStretch(new Percentage(0.5), new Percentage(0.5)); break;
                    case AutoKind.Color: img.WhiteBalance(); break;
                }
            });

        /// <summary>"Upeče" trenutna podešavanja u sliku (samo u selekciji, ako postoji) i vrati klizače na nulu.</summary>
        public static void BakeAdjustments(ImageProject p)
        {
            var snapshot = ProjectJson.Clone(p);
            ProcessWithinSelection(p, "adjust", img => ImageEngine.ApplyAdjustments(img, snapshot, 1.0));
            p.ResetAdjustments();
        }

        public static void FillSelection(ImageProject p, string hex) =>
            ProcessWithinSelection(p, "fill", img =>
            {
                using var solid = new MagickImage(ImageEngine.Color(hex, MagickColors.White), img.Width, img.Height);
                img.Composite(solid, 0, 0, CompositeOperator.Src);
            });

        public static void DeleteSelection(ImageProject p) =>
            ProcessWithinSelection(p, "delete", img =>
            {
                img.Alpha(AlphaOption.Set);
                img.Evaluate(Channels.Alpha, EvaluateOperator.Set, 0);
            });

        /// <summary>Kopira izabrani deo (sa podešavanjima, kako se vidi) u novi sloj na istom mestu. cut = i obriši iz slike.</summary>
        public static ImageLayer? CopySelectionToLayer(ImageProject p, string name, bool cut)
        {
            var b = SelectionBounds(p);
            if (b == null) return null;
            var mask = FullMask(p);
            using var img = LoadFull(p, withAdjustments: true);
            int w = (int)img.Width, h = (int)img.Height;
            var px = ImageEngine.ToRgba(img);
            for (int i = 0; i < mask.Length && i * 4 + 3 < px.Length; i++) px[i * 4 + 3] = (byte)(px[i * 4 + 3] * mask[i] / 255);
            using var cutImg = ImageEngine.FromRgba(px, w, h);
            cutImg.Crop(new MagickGeometry(b.Value.x, b.Value.y, (uint)b.Value.w, (uint)b.Value.h));
            cutImg.ResetPage();
            string path = Session.NewFile("piece");
            ImageEngine.SavePng(cutImg, path);
            if (cut) DeleteSelection(p);
            return new ImageLayer { Name = name, SourcePath = path, X = b.Value.x, Y = b.Value.y, Width = b.Value.w, Height = b.Value.h };
        }

        /// <summary>Spaja sve vidljive slojeve i podešavanja u jednu sliku.</summary>
        public static void Flatten(ImageProject p)
        {
            using var img = CanvasEngine.RenderComposite(p, null, out _);
            Commit(p, img, "flatten");
            p.Layers.Clear();
            p.ResetAdjustments();
        }

        // ════════════════════════════════════════════════════════════════
        // RETUŠ
        // ════════════════════════════════════════════════════════════════
        /// <summary>Uklanja (popunjava okolinom) sve što pokriva maska.</summary>
        public static void Inpaint(ImageProject p, byte[] mask)
        {
            using var img = LoadFull(p);
            int w = (int)img.Width, h = (int)img.Height;
            int grow = Math.Max(2, Math.Max(w, h) / 400);
            var m = PixelOps.Dilate(mask, w, h, grow);
            var px = ImageEngine.ToRgba(img);
            PixelOps.Inpaint(px, w, h, m);
            ImageEngine.Assign(img, px, w, h);
            Commit(p, img, "retouch");
        }

        public static int RedEye(ImageProject p, byte[] mask)
        {
            using var img = LoadFull(p);
            int w = (int)img.Width, h = (int)img.Height;
            var px = ImageEngine.ToRgba(img);
            int n = PixelOps.RemoveRedEye(px, w, h, mask);
            ImageEngine.Assign(img, px, w, h);
            Commit(p, img, "redeye");
            return n;
        }

        // ════════════════════════════════════════════════════════════════
        // POZADINA (mask = objekat/osoba, 255 = zadržati)
        // ════════════════════════════════════════════════════════════════
        public static void RemoveBackground(ImageProject p, byte[] subject)
        {
            using var img = LoadFull(p);
            int w = (int)img.Width, h = (int)img.Height;
            var px = ImageEngine.ToRgba(img);
            for (int i = 0; i < subject.Length; i++) px[i * 4 + 3] = (byte)(px[i * 4 + 3] * subject[i] / 255);
            ImageEngine.Assign(img, px, w, h);
            Commit(p, img, "nobg");
        }

        /// <summary>Nova pozadina: boja (#RRGGBB) ili slika (popunjava platno, višak se seče).</summary>
        public static void ReplaceBackground(ImageProject p, byte[] subject, string? colorHex, string? imagePath)
        {
            using var img = LoadFull(p);
            int w = (int)img.Width, h = (int)img.Height;
            var px = ImageEngine.ToRgba(img);
            for (int i = 0; i < subject.Length; i++) px[i * 4 + 3] = (byte)(px[i * 4 + 3] * subject[i] / 255);
            using var fg = ImageEngine.FromRgba(px, w, h);

            MagickImage bg;
            if (!string.IsNullOrEmpty(imagePath))
            {
                bg = ImageEngine.Load(imagePath);
                bg.Resize(new MagickGeometry((uint)w, (uint)h) { FillArea = true });
                bg.Crop(new MagickGeometry((uint)w, (uint)h), Gravity.Center);
                bg.ResetPage();
            }
            else bg = new MagickImage(ImageEngine.Color(colorHex, MagickColors.White), (uint)w, (uint)h);

            using (bg)
            {
                bg.Composite(fg, 0, 0, CompositeOperator.Over);
                Commit(p, bg, "newbg");
            }
        }

        public static void BlurBackground(ImageProject p, byte[] subject, double amount)
        {
            using var img = LoadFull(p);
            using var blurred = (MagickImage)img.Clone();
            double size = Math.Max(0.5, Math.Max(img.Width, img.Height) / 1000.0);
            blurred.Blur(0, 1 + Math.Clamp(amount, 1, 100) / 100.0 * 14 * size);
            var bgMask = subject.Select(v => (byte)(255 - v)).ToArray();
            ImageEngine.BlendThroughMask(img, blurred, bgMask);
            Commit(p, img, "bgblur");
        }

        /// <summary>Isečen objekat (providna pozadina) kao zaseban PNG, isečen na svoj okvir.</summary>
        public static (string path, int x, int y, int w, int h)? Cutout(ImageProject p, byte[] subject)
        {
            int W = p.CanvasWidth, H = p.CanvasHeight;
            var b = PixelOps.MaskBounds(subject, W, H, 20);
            if (b == null) return null;
            using var img = LoadFull(p, withAdjustments: true);
            var px = ImageEngine.ToRgba(img);
            for (int i = 0; i < subject.Length; i++) px[i * 4 + 3] = (byte)(px[i * 4 + 3] * subject[i] / 255);
            using var c = ImageEngine.FromRgba(px, (int)img.Width, (int)img.Height);
            c.Crop(new MagickGeometry(b.Value.x, b.Value.y, (uint)b.Value.w, (uint)b.Value.h));
            c.ResetPage();
            string path = Session.NewFile("cutout");
            ImageEngine.SavePng(c, path);
            return (path, b.Value.x, b.Value.y, b.Value.w, b.Value.h);
        }
    }

    /// <summary>Opisi položaja rečima (gore levo, centar...), za izgovor.</summary>
    public static class Describe
    {
        public static string Position(double cx, double cy, double W, double H)
        {
            int col = cx < W / 3 ? 0 : cx > W * 2 / 3 ? 2 : 1;
            int row = cy < H / 3 ? 0 : cy > H * 2 / 3 ? 2 : 1;
            return Lang.T($"pos_{row}{col}");
        }

        public static string Layer(Layer l, ImageProject p)
        {
            string pos = Position(l.CenterX, l.CenterY, p.CanvasWidth, p.CanvasHeight);
            return string.Format(Lang.T("layer_where"), l.Name, pos, Math.Round(l.X), Math.Round(l.Y), Math.Round(l.Width), Math.Round(l.Height));
        }
    }
}
