using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using UltraStudio.Localization;
using UltraStudio.Models;

namespace UltraStudio.Views
{
    /// <summary>
    /// Jedan dijalog za SVE tipove slojeva — polja zajednička za sve (ime,
    /// pozicija, veličina, providnost, vidljivost) plus polja specifična za
    /// tip (tekst/oblik) grade se dinamički u konstruktoru, isti "grade se u
    /// kodu, ne u XAML-u" obrazac kao SetValueDialog, radi konzistentnosti.
    /// </summary>
    public partial class LayerPropertiesDialog : Window
    {
        private readonly Layer _layer;
        private readonly TextBox _name, _x, _y, _w, _h, _opacity;
        private readonly CheckBox _visible;

        // Tekst
        private TextBox? _text, _fontFamily, _fontSize, _colorHex;
        private CheckBox? _bold, _italic;

        // Oblik
        private TextBox? _fillColorHex, _strokeColorHex, _strokeWidth;
        private CheckBox? _fillEnabled, _strokeEnabled;

        public LayerPropertiesDialog(Layer layer)
        {
            _layer = layer;
            Title = string.Format(Lang.T("layer_props_title"), layer.Name);
            Width = 380; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgDark"];

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(18) };
            scroll.Content = stack;

            _name = AddField(stack, Lang.T("layer_field_name"), layer.Name);
            _x = AddField(stack, Lang.T("layer_field_x"), layer.X.ToString("0.#"));
            _y = AddField(stack, Lang.T("layer_field_y"), layer.Y.ToString("0.#"));
            _w = AddField(stack, Lang.T("layer_field_width"), layer.Width.ToString("0.#"));
            _h = AddField(stack, Lang.T("layer_field_height"), layer.Height.ToString("0.#"));
            _opacity = AddField(stack, Lang.T("layer_field_opacity"), layer.Opacity.ToString("0.#"));

            _visible = new CheckBox
            {
                Content = Lang.T("layer_field_visible"),
                IsChecked = layer.Visible,
                Margin = new Thickness(0, 4, 0, 14),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"]
            };
            stack.Children.Add(_visible);

            if (layer is TextLayer t)
            {
                AddSectionLabel(stack, Lang.T("layer_section_text"));
                _text = AddField(stack, Lang.T("layer_field_text"), t.Text);
                _fontFamily = AddField(stack, Lang.T("layer_field_font"), t.FontFamily);
                _fontSize = AddField(stack, Lang.T("layer_field_font_size"), t.FontSize.ToString("0.#"));
                _colorHex = AddField(stack, Lang.T("layer_field_color"), t.ColorHex);
                _bold = new CheckBox { Content = Lang.T("layer_field_bold"), IsChecked = t.Bold, Margin = new Thickness(0, 0, 0, 6),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
                _italic = new CheckBox { Content = Lang.T("layer_field_italic"), IsChecked = t.Italic, Margin = new Thickness(0, 0, 0, 14),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
                stack.Children.Add(_bold);
                stack.Children.Add(_italic);
            }
            else if (layer is ShapeLayer s)
            {
                AddSectionLabel(stack, Lang.T("layer_section_shape"));
                _fillEnabled = new CheckBox { Content = Lang.T("layer_field_fill_enabled"), IsChecked = s.FillEnabled, Margin = new Thickness(0, 0, 0, 6),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
                stack.Children.Add(_fillEnabled);
                _fillColorHex = AddField(stack, Lang.T("layer_field_fill_color"), s.FillColorHex);
                _strokeEnabled = new CheckBox { Content = Lang.T("layer_field_stroke_enabled"), IsChecked = s.StrokeEnabled, Margin = new Thickness(0, 0, 0, 6),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
                stack.Children.Add(_strokeEnabled);
                _strokeColorHex = AddField(stack, Lang.T("layer_field_stroke_color"), s.StrokeColorHex);
                _strokeWidth = AddField(stack, Lang.T("layer_field_stroke_width"), s.StrokeWidth.ToString("0.#"));
            }
            else if (layer is ImageLayer il)
            {
                AddSectionLabel(stack, Lang.T("layer_section_image"));
                var pathLbl = new TextBlock
                {
                    Text = il.SourcePath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrTextMuted"], FontSize = 11
                };
                stack.Children.Add(pathLbl);
            }

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = Lang.T("btn_apply"), Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["AIButton"], IsDefault = true };
            var cancel = new Button { Content = Lang.T("btn_cancel"), Width = 90, Height = 30,
                Style = (Style)Application.Current.Resources["StdButton"], IsCancel = true };
            ok.Click += Ok_Click;
            btnPanel.Children.Add(ok); btnPanel.Children.Add(cancel);
            stack.Children.Add(btnPanel);

            Content = scroll;
        }

        private void AddSectionLabel(StackPanel stack, string text)
        {
            stack.Children.Add(new TextBlock
            {
                Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 8),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrAccentAI"]
            });
        }

        private TextBox AddField(StackPanel stack, string label, string value)
        {
            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
            var box = new TextBox { Text = value, Height = 28, Margin = new Thickness(0, 0, 0, 12) };
            box.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
            stack.Children.Add(lbl);
            stack.Children.Add(box);
            return box;
        }

        private static double ParseD(string s, double fallback)
        {
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _layer.Name = string.IsNullOrWhiteSpace(_name.Text) ? _layer.Name : _name.Text.Trim();
            _layer.X = ParseD(_x.Text, _layer.X);
            _layer.Y = ParseD(_y.Text, _layer.Y);
            _layer.Width = ParseD(_w.Text, _layer.Width);
            _layer.Height = ParseD(_h.Text, _layer.Height);
            _layer.Opacity = Math.Clamp(ParseD(_opacity.Text, _layer.Opacity), 0, 100);
            _layer.Visible = _visible.IsChecked == true;

            if (_layer is TextLayer t)
            {
                t.Text = _text!.Text;
                t.FontFamily = string.IsNullOrWhiteSpace(_fontFamily!.Text) ? t.FontFamily : _fontFamily.Text.Trim();
                t.FontSize = Math.Max(1, ParseD(_fontSize!.Text, t.FontSize));
                t.ColorHex = _colorHex!.Text.Trim();
                t.Bold = _bold!.IsChecked == true;
                t.Italic = _italic!.IsChecked == true;
            }
            else if (_layer is ShapeLayer s)
            {
                s.FillEnabled = _fillEnabled!.IsChecked == true;
                s.FillColorHex = _fillColorHex!.Text.Trim();
                s.StrokeEnabled = _strokeEnabled!.IsChecked == true;
                s.StrokeColorHex = _strokeColorHex!.Text.Trim();
                s.StrokeWidth = Math.Max(0, ParseD(_strokeWidth!.Text, s.StrokeWidth));
            }

            DialogResult = true;
        }
    }
}
