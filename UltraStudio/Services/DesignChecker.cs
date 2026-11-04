using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ImageMagick;
using UltraStudio.Localization;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Provera dizajna (F7): izgovorljiv izveštaj o rasporedu — šta je gde,
    /// koliko je veliko, da li je tekst čitljiv (WCAG kontrast prema stvarnoj
    /// pozadini ispod njega), šta izlazi van platna, šta se preklapa.
    /// </summary>
    public static class DesignChecker
    {
        public static string Run(ImageProject p)
        {
            var sb = new StringBuilder();
            int W = p.CanvasWidth, H = p.CanvasHeight;
            var warnings = new List<string>();

            string? preset = CanvasPresets.Match(W, H);
            sb.AppendLine(string.Format(Lang.T("dc_canvas"), W, H, preset != null ? $" ({preset})" : "", Orientation(W, H)));
            sb.AppendLine(p.HasImage ? Lang.T("dc_has_photo") : string.Format(Lang.T("dc_blank_bg"), p.CanvasBackgroundHex));
            if (!string.IsNullOrEmpty(p.LookName)) sb.AppendLine(string.Format(Lang.T("dc_look"), p.LookName));

            var visible = p.Layers.Where(l => l.Visible).ToList();
            int hidden = p.Layers.Count - visible.Count;
            sb.AppendLine(string.Format(Lang.T("dc_layers"), p.Layers.Count, hidden));
            sb.AppendLine();

            // Od najvišeg ka najnižem — onako kako oko "čita" kompoziciju
            for (int i = p.Layers.Count - 1; i >= 0; i--)
            {
                var l = p.Layers[i];
                if (!l.Visible) continue;
                sb.AppendLine(DescribeLayer(l, p, warnings));
            }

            // Hijerarhija teksta
            var texts = visible.OfType<TextLayer>().OrderByDescending(t => t.FontSize).ToList();
            if (texts.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine(Lang.T("dc_hierarchy"));
                foreach (var t in texts) sb.AppendLine($"  {Short(t.Text)} — {t.FontSize:0} px{(t.Bold ? ", " + Lang.T("layer_field_bold").ToLowerInvariant() : "")}");
                if (texts.Count >= 2 && texts[0].FontSize < texts[1].FontSize * 1.25)
                    warnings.Add(Lang.T("dc_warn_hierarchy"));
            }

            // Kontrast teksta prema onome što je stvarno ispod
            if (texts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Lang.T("dc_contrast_header"));
                foreach (var t in texts) sb.AppendLine("  " + ContrastLine(p, t, warnings));
            }

            // Preklapanje tekstova
            for (int a = 0; a < texts.Count; a++)
                for (int b = a + 1; b < texts.Count; b++)
                    if (Intersects(texts[a], texts[b]))
                        warnings.Add(string.Format(Lang.T("dc_warn_overlap"), texts[a].Name, texts[b].Name));

            sb.AppendLine();
            if (warnings.Count == 0) sb.AppendLine(Lang.T("dc_all_good"));
            else
            {
                sb.AppendLine(string.Format(Lang.T("dc_warnings"), warnings.Count));
                foreach (var w in warnings) sb.AppendLine("  • " + w);
            }
            return sb.ToString().TrimEnd();
        }

        private static string Orientation(int w, int h) =>
            w == h ? Lang.T("dc_square") : w > h ? Lang.T("dc_landscape") : Lang.T("dc_portrait");

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " / ").Trim();
            return s.Length > 50 ? "\"" + s[..47] + "...\"" : "\"" + s + "\"";
        }

        private static string DescribeLayer(Layer l, ImageProject p, List<string> warnings)
        {
            int W = p.CanvasWidth, H = p.CanvasHeight;
            string kind = Lang.T(l.TypeLabelKey);
            string pos = Describe.Position(l.CenterX, l.CenterY, W, H);
            double areaPct = Math.Min(100, l.Width * l.Height / (double)(W * H) * 100);

            var parts = new List<string>
            {
                $"{l.Name} ({kind}): {pos}",
                string.Format(Lang.T("dc_size"), Math.Round(l.Width), Math.Round(l.Height), areaPct.ToString("0.#"))
            };
            if (Math.Abs(l.CenterX - W / 2.0) <= W * 0.01) parts.Add(Lang.T("dc_centered_h"));
            if (Math.Abs(l.CenterY - H / 2.0) <= H * 0.01) parts.Add(Lang.T("dc_centered_v"));
            if (l.Rotation != 0) parts.Add(string.Format(Lang.T("dc_rotated"), l.Rotation.ToString("0.#")));
            if (l.Opacity < 100) parts.Add(string.Format(Lang.T("dc_opacity"), l.Opacity.ToString("0")));
            if (l.Blend != BlendMode.Normal) parts.Add(string.Format(Lang.T("dc_blend"), Lang.T("blend_" + l.Blend)));
            var styles = new List<string>();
            if (l.ShadowEnabled) styles.Add(Lang.T("style_shadow").ToLowerInvariant());
            if (l.OutlineEnabled) styles.Add(Lang.T("style_outline").ToLowerInvariant());
            if (l.GlowEnabled) styles.Add(Lang.T("style_glow").ToLowerInvariant());
            if (styles.Count > 0) parts.Add(Lang.T("dc_styles") + " " + string.Join(", ", styles));

            switch (l)
            {
                case TextLayer t:
                    parts.Add(string.Format(Lang.T("dc_text"), Short(t.Text), t.FontFamily, t.FontSize.ToString("0"), t.ColorHex));
                    if (string.IsNullOrWhiteSpace(t.Text)) warnings.Add(string.Format(Lang.T("dc_warn_empty"), t.Name));
                    if (t.Wrap)
                    {
                        parts.Add(Lang.T("dc_frame"));
                        int over = TextLayout.OverflowLines(t);
                        if (over > 0) warnings.Add(string.Format(Lang.T("dc_warn_overflow"), t.Name, over));
                    }
                    if (t.FontSize < H / 60.0 && !t.Wrap) warnings.Add(string.Format(Lang.T("dc_warn_small"), t.Name, t.FontSize.ToString("0")));
                    break;
                case ShapeLayer s:
                    parts.Add(s.FillEnabled ? string.Format(Lang.T("dc_fill"), s.FillColorHex) : Lang.T("dc_no_fill"));
                    break;
                case ImageLayer im:
                    if (!System.IO.File.Exists(im.SourcePath)) warnings.Add(string.Format(Lang.T("dc_warn_missing"), im.Name));
                    break;
                case GradientLayer g:
                    parts.Add($"{g.StartColorHex} → {g.EndColorHex}");
                    break;
            }

            // Van platna / preblizu ivice
            var outside = new List<string>();
            if (l.X < -0.5) outside.Add(string.Format(Lang.T("dc_edge_left"), Math.Round(-l.X)));
            if (l.Y < -0.5) outside.Add(string.Format(Lang.T("dc_edge_top"), Math.Round(-l.Y)));
            if (l.X + l.Width > W + 0.5) outside.Add(string.Format(Lang.T("dc_edge_right"), Math.Round(l.X + l.Width - W)));
            if (l.Y + l.Height > H + 0.5) outside.Add(string.Format(Lang.T("dc_edge_bottom"), Math.Round(l.Y + l.Height - H)));
            bool fullBleed = l.X <= 0.5 && l.Y <= 0.5 && l.X + l.Width >= W - 0.5 && l.Y + l.Height >= H - 0.5;
            if (outside.Count > 0 && !(fullBleed && l is not TextLayer))
                warnings.Add(string.Format(Lang.T("dc_warn_outside"), l.Name, string.Join(", ", outside)));
            else if (l is TextLayer && outside.Count == 0)
            {
                double margin = Math.Min(W, H) * 0.03;
                if (l.X < margin || l.Y < margin || W - (l.X + l.Width) < margin || H - (l.Y + l.Height) < margin)
                    warnings.Add(string.Format(Lang.T("dc_warn_margin"), l.Name));
            }

            return string.Join("; ", parts) + ".";
        }

        private static bool Intersects(Layer a, Layer b) =>
            a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

        private static string ContrastLine(ImageProject p, TextLayer t, List<string> warnings)
        {
            try
            {
                using var bg = CanvasEngine.RenderComposite(p, 900, out double s, skipLayer: t.Id);
                int w = (int)bg.Width, h = (int)bg.Height;
                var px = ImageEngine.ToRgba(bg);
                var (dark, light) = PixelOps.ExtremeColors(px, w, h,
                    (int)(t.X * s), (int)(t.Y * s), Math.Max(1, (int)(t.Width * s)), Math.Max(1, (int)(t.Height * s)));
                var tc = ImageEngine.ColorBytes(t.ColorHex);
                var text = (tc.r, tc.g, tc.b);
                double worst = Math.Min(PixelOps.ContrastRatio(text, dark), PixelOps.ContrastRatio(text, light));
                bool large = t.FontSize >= 24 || (t.Bold && t.FontSize >= 18.66);
                double need = large ? 3.0 : 4.5;
                string verdict = worst >= (large ? 4.5 : 7.0) ? Lang.T("dc_aaa")
                               : worst >= need ? Lang.T("dc_aa")
                               : Lang.T("dc_fail");
                if (worst < need)
                    warnings.Add(string.Format(Lang.T("dc_warn_contrast"), t.Name, worst.ToString("0.0"), need.ToString("0.0")));
                return string.Format(Lang.T("dc_contrast_line"), t.Name, worst.ToString("0.0"), verdict);
            }
            catch (Exception ex)
            {
                return $"{t.Name}: {ex.Message}";
            }
        }
    }
}
