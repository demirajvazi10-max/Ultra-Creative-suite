using System;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — "Title" (Ctrl+Alt+T): text drawn over the selected picture clip in the render —
    /// a heading, an optional second line (a name's role, a place), where it sits (centre,
    /// top, bottom, lower third, corners), size, colour, a dark band behind it, how it comes
    /// in and goes out (fade, slide up, slide in from the right, zoom), when it starts and
    /// how long it stays.
    /// </summary>
    public class TitleWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly TimelineItem _item;
        private readonly WControls.TextBox _text, _sub;
        private readonly WControls.ComboBox _position, _animation, _color;
        private readonly WControls.Slider _size, _startIn, _length;
        private readonly WControls.CheckBox _box;
        private readonly WControls.TextBlock _status;

        /// <summary>True when the user removed the title.</summary>
        public bool Removed { get; private set; }

        public TitleWindow(Window owner, TimelineItem item)
        {
            Owner = owner;
            _item = item;
            Title = LF("ttl_title", item.Name ?? "");
            Width = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var t = item.TextOverlay ?? new TextOverlayData();
            bool has = t.Enabled && !string.IsNullOrWhiteSpace(t.Text);

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("ttl_intro")));

            _text = new WControls.TextBox { Text = has ? t.Text : "", MinWidth = 440, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_text"), _text));
            _sub = new WControls.TextBox { Text = has ? t.Subtitle ?? "" : "", MinWidth = 440, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_sub"), _sub));

            _position = UiKit.Combo(
                (L("ttl_pos_center"), "Center"), (L("ttl_pos_lower"), "Lower third"),
                (L("ttl_pos_top"), "Top center"), (L("ttl_pos_bottom"), "Bottom center"),
                (L("ttl_pos_tl"), "Top left"), (L("ttl_pos_tr"), "Top right"),
                (L("ttl_pos_bl"), "Bottom left"), (L("ttl_pos_br"), "Bottom right"));
            UiKit.Select(_position, NormalizePosition(t.Position));
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_position"), _position));

            _animation = UiKit.Combo(
                (L("ttl_anim_fade"), "Fade"), (L("ttl_anim_slideup"), "SlideUp"),
                (L("ttl_anim_slideleft"), "SlideLeft"), (L("ttl_anim_zoom"), "Zoom"), (L("ttl_anim_none"), "None"));
            UiKit.Select(_animation, string.IsNullOrEmpty(t.Animation) ? "Fade" : t.Animation);
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_animation"), _animation));

            _size = UiKit.Slider(16, 200, Math.Clamp(t.SelectedFontSize > 0 ? t.SelectedFontSize : 64, 16, 200), 2);
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_size"), _size));

            _color = UiKit.Combo(
                (L("ttl_col_white"), "#FFFFFF"), (L("ttl_col_yellow"), "#FFD700"), (L("ttl_col_black"), "#000000"),
                (L("ttl_col_red"), "#E53935"), (L("ttl_col_blue"), "#42A5F5"), (L("ttl_col_green"), "#66BB6A"));
            UiKit.Select(_color, (t.Color ?? "#FFFFFF").ToUpperInvariant());
            if (_color.SelectedIndex < 0) _color.SelectedIndex = 0;
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_color"), _color));

            _box = UiKit.Check(L("ttl_chk_box"), t.Box);
            panel.Children.Add(_box);

            double clipLen = Math.Max(0.5, item.Duration);
            _startIn = UiKit.Slider(0, Math.Floor(clipLen * 2) / 2, Math.Clamp(t.StartIn, 0, clipLen), 0.5);
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_start"), _startIn));
            _length = UiKit.Slider(0, Math.Ceiling(clipLen * 2) / 2, Math.Clamp(t.Length, 0, clipLen), 0.5);
            panel.Children.Add(UiKit.Labelled(L("ttl_lbl_length"), _length));

            _status = UiKit.Status();
            panel.Children.Add(_status);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("ttl_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_text.Text)) { UiKit.Announce(_status, L("ttl_need_text")); _text.Focus(); return; }
                Apply();
                DialogResult = true;
                Close();
            };
            var remove = UiKit.Btn(L("ttl_btn_remove"));
            remove.IsEnabled = has;
            remove.Click += (s, e) =>
            {
                _item.TextOverlay ??= new TextOverlayData();
                _item.TextOverlay.Enabled = false;
                Removed = true;
                DialogResult = true;
                Close();
            };
            var cancel = UiKit.Btn(L("ttl_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(remove);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Loaded += (s, e) => _text.Focus();
        }

        /// <summary>The positions written by the older text dialog ("📐 Bottom right", "Centar") mapped to ours.</summary>
        private static string NormalizePosition(string p)
        {
            string s = (p ?? "").ToLowerInvariant();
            if (s.Contains("lower")) return "Lower third";
            bool top = s.Contains("top"), bottom = s.Contains("bottom"), left = s.Contains("left"), right = s.Contains("right");
            if (top && left) return "Top left";
            if (top && right) return "Top right";
            if (bottom && left) return "Bottom left";
            if (bottom && right) return "Bottom right";
            if (top) return "Top center";
            if (bottom) return "Bottom center";
            return "Center";
        }

        private void Apply()
        {
            var t = _item.TextOverlay ??= new TextOverlayData();
            t.Text = _text.Text.Trim();
            t.Subtitle = _sub.Text.Trim();
            t.Position = UiKit.Tag(_position) as string ?? "Center";
            t.Animation = UiKit.Tag(_animation) as string ?? "Fade";
            t.SelectedFontSize = (int)Math.Round(_size.Value);
            t.Color = UiKit.Tag(_color) as string ?? "#FFFFFF";
            t.Box = _box.IsChecked == true;
            t.StartIn = _startIn.Value;
            t.Length = _length.Value;
            t.Enabled = true;
        }

        /// <summary>A spoken summary of the clip's title.</summary>
        public static string Describe(TimelineItem item)
        {
            var t = item.TextOverlay;
            if (t == null || !t.Enabled || string.IsNullOrWhiteSpace(t.Text)) return L("ttl_d_none");
            string s = LF("ttl_d_title", t.Text, NormalizePosition(t.Position) switch
            {
                "Lower third" => L("ttl_pos_lower"), "Top center" => L("ttl_pos_top"), "Bottom center" => L("ttl_pos_bottom"),
                "Top left" => L("ttl_pos_tl"), "Top right" => L("ttl_pos_tr"), "Bottom left" => L("ttl_pos_bl"),
                "Bottom right" => L("ttl_pos_br"), _ => L("ttl_pos_center")
            });
            if (!string.IsNullOrWhiteSpace(t.Subtitle)) s += ", " + t.Subtitle;
            return s;
        }
    }
}
