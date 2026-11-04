using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UltraStudio.Localization;
using UltraStudio.Models;
using UltraStudio.Services;

namespace UltraStudio.Views
{
    /// <summary>
    /// Svojstva sloja — jedan dijalog za sve tipove. Polja se grade u kodu,
    /// svako ima ime za čitač ekrana; Tab ide redom kroz sve. Stilovi sloja
    /// (senka, kontura, sjaj), režim stapanja i rotacija važe za sve tipove.
    /// </summary>
    public partial class LayerPropertiesDialog : Window
    {
        private readonly Layer _layer;
        private readonly StackPanel _stack = new() { Margin = new Thickness(18) };
        private readonly List<Action> _apply = new();
        private readonly TextBlock _error;

        public LayerPropertiesDialog(Layer layer)
        {
            _layer = layer;
            Ui.Prepare(this, string.Format(Lang.T("layer_props_title"), layer.Name), 440, 720);
            ResizeMode = ResizeMode.CanResize;

            // ── Opšte ──
            Section("layer_section_general");
            Text("layer_field_name", layer.Name, v => { if (!string.IsNullOrWhiteSpace(v)) layer.Name = v.Trim(); });

            if (layer is TextLayer t)
            {
                Section("layer_section_text");
                var textBox = Text("layer_field_text", t.Text, v => t.Text = v, multiline: true);
                FontCombo(t);
                Num("layer_field_font_size", t.FontSize, v => t.FontSize = v, 1, 2000);
                Text("layer_field_color", t.ColorHex, v => t.ColorHex = v.Trim());
                Check("layer_field_bold", t.Bold, v => t.Bold = v);
                Check("layer_field_italic", t.Italic, v => t.Italic = v);
                Choice("layer_field_align", new[] { "align_left", "align_center", "align_right", "align_justify" }, (int)t.Align, i => t.Align = (TextAlign)i);
                Check("layer_field_wrap", t.Wrap, v => t.Wrap = v);
                Num("layer_field_line_spacing", t.LineSpacing, v => t.LineSpacing = v, 0.5, 4);
                Check("layer_field_autosize", t.AutoSize, v => t.AutoSize = v);

                var proof = Ui.Button(Lang.T("menu_layer_proofread"), () => { }, primary: true);
                proof.HorizontalAlignment = HorizontalAlignment.Left;
                proof.Margin = new Thickness(0, 0, 0, 12);
                proof.Click += (_, __) =>
                {
                    var pdlg = new ProofreadingDialog(textBox.Text, Lang.T("proof_title_layer")) { Owner = this };
                    if (pdlg.ShowDialog() == true) textBox.Text = pdlg.ResultText;
                };
                _stack.Children.Add(proof);
            }
            else if (layer is ShapeLayer s)
            {
                Section("layer_section_shape");
                Check("layer_field_fill_enabled", s.FillEnabled, v => s.FillEnabled = v);
                Text("layer_field_fill_color", s.FillColorHex, v => s.FillColorHex = v.Trim());
                Check("layer_field_stroke_enabled", s.StrokeEnabled, v => s.StrokeEnabled = v);
                Text("layer_field_stroke_color", s.StrokeColorHex, v => s.StrokeColorHex = v.Trim());
                Num("layer_field_stroke_width", s.StrokeWidth, v => s.StrokeWidth = v, 0, 500);
                if (s.ShapeKind == ShapeKind.Rectangle)
                    Num("layer_field_corner_radius", s.CornerRadius, v => s.CornerRadius = v, 0, 5000);
            }
            else if (layer is GradientLayer g)
            {
                Section("layer_section_gradient");
                Choice("gradient_kind", new[] { "gradient_linear", "gradient_radial" }, (int)g.Kind, i => g.Kind = (GradientKind)i);
                Text("gradient_start", g.StartColorHex, v => g.StartColorHex = v.Trim());
                Num("gradient_start_opacity", g.StartOpacity, v => g.StartOpacity = v, 0, 100);
                Text("gradient_end", g.EndColorHex, v => g.EndColorHex = v.Trim());
                Num("gradient_end_opacity", g.EndOpacity, v => g.EndOpacity = v, 0, 100);
                Num("gradient_angle", g.Angle, v => g.Angle = v, -360, 360);
            }
            else if (layer is ImageLayer il)
            {
                Section("layer_section_image");
                var path = Ui.TextBox(Lang.T("layer_section_image"), il.SourcePath);
                path.IsReadOnly = true;
                _stack.Children.Add(path);
                var replace = Ui.Button(Lang.T("layer_replace_image"), () =>
                {
                    var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|All files|*.*" };
                    if (d.ShowDialog(this) == true) path.Text = d.FileName;
                });
                replace.HorizontalAlignment = HorizontalAlignment.Left;
                replace.Margin = new Thickness(0, 0, 0, 12);
                _stack.Children.Add(replace);
                _apply.Add(() => il.SourcePath = path.Text);
            }

            // ── Položaj ──
            Section("layer_section_position");
            Num("layer_field_x", layer.X, v => layer.X = v);
            Num("layer_field_y", layer.Y, v => layer.Y = v);
            Num("layer_field_width", layer.Width, v => layer.Width = v, 1, 100000);
            Num("layer_field_height", layer.Height, v => layer.Height = v, 1, 100000);
            Num("layer_field_rotation", layer.Rotation, v => layer.Rotation = ((v % 360) + 360) % 360, -3600, 3600);

            // ── Izgled ──
            Section("layer_section_look");
            Num("layer_field_opacity", layer.Opacity, v => layer.Opacity = v, 0, 100);
            Check("layer_field_visible", layer.Visible, v => layer.Visible = v);
            Choice("layer_field_blend", Enum.GetNames(typeof(BlendMode)).Select(n => "blend_" + n).ToArray(), (int)layer.Blend, i => layer.Blend = (BlendMode)i);

            // ── Stilovi ──
            Section("layer_section_styles");
            Check("style_shadow", layer.ShadowEnabled, v => layer.ShadowEnabled = v);
            Text("style_shadow_color", layer.ShadowColorHex, v => layer.ShadowColorHex = v.Trim());
            Num("style_shadow_x", layer.ShadowOffsetX, v => layer.ShadowOffsetX = v, -2000, 2000);
            Num("style_shadow_y", layer.ShadowOffsetY, v => layer.ShadowOffsetY = v, -2000, 2000);
            Num("style_shadow_blur", layer.ShadowBlur, v => layer.ShadowBlur = v, 0, 500);
            Num("style_shadow_opacity", layer.ShadowOpacity, v => layer.ShadowOpacity = v, 0, 100);
            Check("style_outline", layer.OutlineEnabled, v => layer.OutlineEnabled = v);
            Text("style_outline_color", layer.OutlineColorHex, v => layer.OutlineColorHex = v.Trim());
            Num("style_outline_width", layer.OutlineWidth, v => layer.OutlineWidth = v, 0, 500);
            Check("style_glow", layer.GlowEnabled, v => layer.GlowEnabled = v);
            Text("style_glow_color", layer.GlowColorHex, v => layer.GlowColorHex = v.Trim());
            Num("style_glow_size", layer.GlowSize, v => layer.GlowSize = v, 0, 500);

            _error = new TextBlock { Foreground = Ui.Res("BrDanger"), TextWrapping = TextWrapping.Wrap };
            _error.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Assertive);
            _stack.Children.Add(_error);
            _stack.Children.Add(Ui.Row(
                Ui.Button(Lang.T("btn_apply"), Ok, primary: true, isDefault: true),
                Ui.Button(Lang.T("btn_cancel"), () => DialogResult = false, isCancel: true)));

            Content = new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Loaded += (_, __) =>
            {
                var first = _stack.Children.OfType<TextBox>().FirstOrDefault();
                first?.Focus(); first?.SelectAll();
            };
        }

        // ── graditelji polja ──
        private void Section(string key) => _stack.Children.Add(new TextBlock
        {
            Text = Lang.T(key), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 8),
            Foreground = Ui.Res("BrAccentAI")
        });

        private TextBox Text(string key, string value, Action<string> set, bool multiline = false)
        {
            _stack.Children.Add(Ui.Label(Lang.T(key)));
            var box = Ui.TextBox(Lang.T(key), value, multiline);
            _stack.Children.Add(box);
            _apply.Add(() => set(box.Text));
            return box;
        }

        private readonly List<(TextBox box, string label, double min, double max)> _numbers = new();

        private void Num(string key, double value, Action<double> set, double min = -100000, double max = 100000)
        {
            _stack.Children.Add(Ui.Label(Lang.T(key)));
            var box = Ui.TextBox(Lang.T(key), value.ToString("0.##", CultureInfo.InvariantCulture));
            _stack.Children.Add(box);
            _numbers.Add((box, Lang.T(key), min, max));
            _apply.Add(() => { if (Ui.TryNum(box.Text, out double v)) set(Math.Clamp(v, min, max)); });
        }

        private void Check(string key, bool value, Action<bool> set)
        {
            var c = Ui.Check(Lang.T(key), value);
            _stack.Children.Add(c);
            _apply.Add(() => set(c.IsChecked == true));
        }

        private void Choice(string key, string[] optionKeys, int selected, Action<int> set)
        {
            _stack.Children.Add(Ui.Label(Lang.T(key)));
            var c = Ui.Combo(Lang.T(key), optionKeys.Select(Lang.T), selected);
            _stack.Children.Add(c);
            _apply.Add(() => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); });
        }

        private void FontCombo(TextLayer t)
        {
            _stack.Children.Add(Ui.Label(Lang.T("layer_field_font")));
            var c = new ComboBox { IsEditable = true, FontSize = 13, Height = 30, Margin = new Thickness(0, 0, 0, 10), IsTextSearchEnabled = true };
            c.SetValue(AutomationProperties.NameProperty, Lang.T("layer_field_font"));
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n))
                    c.Items.Add(f);
            }
            catch { }
            c.Text = t.FontFamily;
            _stack.Children.Add(c);
            _apply.Add(() => { if (!string.IsNullOrWhiteSpace(c.Text)) t.FontFamily = c.Text.Trim(); });
        }

        private void Ok()
        {
            foreach (var (box, label, min, max) in _numbers)
            {
                if (!Ui.TryNum(box.Text, out double v) || v < min || v > max)
                {
                    _error.Text = string.Format(Lang.T("form_bad_number"), label) +
                                  string.Format(Lang.T("form_range"), min.ToString("0.##", CultureInfo.InvariantCulture), max.ToString("0.##", CultureInfo.InvariantCulture));
                    Ui.Announce(_error, _error.Text);
                    box.Focus(); box.SelectAll();
                    return;
                }
            }
            foreach (var a in _apply) a();
            if (_layer is TextLayer t && t.AutoSize && !t.Wrap)
            {
                var (w, h) = CanvasEngine.MeasureText(t);
                double oldCx = t.CenterX;
                t.Width = w; t.Height = h;
                // Centrirani/desno poravnati tekst zadržava svoje sidro pri promeni dužine
                if (t.Align == TextAlign.Center) t.X = oldCx - w / 2;
            }
            DialogResult = true;
        }
    }
}
