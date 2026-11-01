using System;
using System.IO;
using System.Linq;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — "Layer and colour" (Ctrl+Alt+L) for the selected clip:
    ///   • where a clip on the layer track (Video 2) sits over the picture below:
    ///     position, size, opacity, green/blue screen, its own sound;
    ///   • a colour look-up table (.cube LUT) for any picture clip.
    /// Built in code, every control labelled; the work order is the keyboard order.
    /// </summary>
    public class LayerSettingsWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly TimelineItem _item;
        private readonly WControls.ComboBox _position, _key, _blend, _mask;
        private readonly WControls.Slider _feather;
        // v2.0: masks of any shape — box, inside out, own points, moving, following an object
        private readonly WControls.Slider _mx, _my, _mw, _mh, _ex, _ey, _ew, _eh;
        private readonly WControls.CheckBox _invert, _moves, _follows;
        private readonly WControls.TextBox _points;
        private readonly WControls.Slider _x, _y, _size, _opacity, _similarity;
        private readonly WControls.CheckBox _audio, _moveToLayer;
        private readonly WControls.TextBox _lut;
        private readonly WControls.TextBlock _status;

        /// <summary>True when the user asked to put the clip on the layer track.</summary>
        public bool MoveToLayerTrack => _moveToLayer?.IsChecked == true;

        public LayerSettingsWindow(Window owner, TimelineItem item)
        {
            Owner = owner;
            _item = item;
            Title = LF("lay_title", item.Name ?? "");
            Width = 600;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var ls = item.Layer ?? new LayerSettings();
            bool onLayer = TrackSet.IsLayer(item.TrackIndex);

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(onLayer ? L("lay_intro_layer") : L("lay_intro_main")));

            if (!onLayer)
            {
                _moveToLayer = UiKit.Check(L("lay_chk_move"), false);
                panel.Children.Add(_moveToLayer);
            }

            _position = UiKit.Combo(
                (L("lay_pos_full"), "Full"), (L("lay_pos_center"), "Center"),
                (L("lay_pos_tl"), "TopLeft"), (L("lay_pos_tr"), "TopRight"),
                (L("lay_pos_bl"), "BottomLeft"), (L("lay_pos_br"), "BottomRight"),
                (L("lay_pos_custom"), "Custom"));
            UiKit.Select(_position, ls.Position);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_position"), _position));

            _size = UiKit.Slider(5, 100, Math.Clamp(ls.Size, 5, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_size"), _size));

            _x = UiKit.Slider(0, 100, Math.Clamp(ls.X, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_x"), _x));
            _y = UiKit.Slider(0, 100, Math.Clamp(ls.Y, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_y"), _y));

            _opacity = UiKit.Slider(0, 100, Math.Round(Math.Clamp(ls.Opacity, 0, 1) * 100), 5);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_opacity"), _opacity));

            // v2.0: blend mode and mask
            _blend = UiKit.Combo(
                (L("lay_blend_normal"), "Normal"), (L("lay_blend_screen"), "Screen"), (L("lay_blend_multiply"), "Multiply"),
                (L("lay_blend_overlay"), "Overlay"), (L("lay_blend_softlight"), "SoftLight"), (L("lay_blend_lighten"), "Lighten"),
                (L("lay_blend_darken"), "Darken"), (L("lay_blend_add"), "Add"), (L("lay_blend_difference"), "Difference"));
            UiKit.Select(_blend, ls.BlendMode ?? "Normal");
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_blend"), _blend));

            _mask = UiKit.Combo(MaskShapes.Shapes.Select(s => (L("lay_mask_" + s.ToLowerInvariant()), (object)s)).ToArray());
            UiKit.Select(_mask, ls.Mask ?? "None");
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_mask"), _mask));
            _points = new WControls.TextBox { Text = ls.MaskPoints ?? "", MinWidth = 420, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_points"), _points));
            _feather = UiKit.Slider(0, 50, Math.Clamp(ls.MaskFeather, 0, 50), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_feather"), _feather));
            _mx = UiKit.Slider(0, 100, Math.Clamp(ls.MaskX, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_mx"), _mx));
            _my = UiKit.Slider(0, 100, Math.Clamp(ls.MaskY, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_my"), _my));
            _mw = UiKit.Slider(2, 200, Math.Clamp(ls.MaskW, 2, 200), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_mw"), _mw));
            _mh = UiKit.Slider(2, 200, Math.Clamp(ls.MaskH, 2, 200), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_mh"), _mh));
            _invert = UiKit.Check(L("lay_chk_invert"), ls.MaskInvert);
            panel.Children.Add(_invert);
            _moves = UiKit.Check(L("lay_chk_moves"), ls.MaskMoves);
            panel.Children.Add(_moves);
            _ex = UiKit.Slider(0, 100, Math.Clamp(ls.MaskEndX, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_ex"), _ex));
            _ey = UiKit.Slider(0, 100, Math.Clamp(ls.MaskEndY, 0, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_ey"), _ey));
            _ew = UiKit.Slider(2, 200, Math.Clamp(ls.MaskEndW, 2, 200), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_ew"), _ew));
            _eh = UiKit.Slider(2, 200, Math.Clamp(ls.MaskEndH, 2, 200), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_eh"), _eh));
            bool hasPath = item.Tracking?.Path != null && item.Tracking.Path.Count > 0;
            _follows = UiKit.Check(hasPath ? LF("lay_chk_follows", item.Tracking.Target ?? "") : L("lay_chk_follows_none"), ls.MaskFollows && hasPath);
            _follows.IsEnabled = hasPath;
            panel.Children.Add(_follows);

            _key = UiKit.Combo((L("lay_key_off"), ""), (L("lay_key_green"), "green"), (L("lay_key_blue"), "blue"));
            UiKit.Select(_key, ls.ChromaKey ?? "");
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_key"), _key));

            _similarity = UiKit.Slider(5, 60, Math.Round(Math.Clamp(ls.KeySimilarity, 0.05, 0.6) * 100), 1);
            panel.Children.Add(UiKit.Labelled(L("lay_lbl_similarity"), _similarity));

            _audio = UiKit.Check(L("lay_chk_audio"), ls.KeepAudio);
            panel.Children.Add(_audio);

            // colour LUT (any picture clip)
            _lut = new WControls.TextBox { Text = item.LutPath ?? "", MinWidth = 420, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            var lutBox = UiKit.Labelled(L("lay_lbl_lut"), _lut);
            var lutButtons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal };
            var browse = UiKit.Btn(L("lay_btn_lut_browse"));
            browse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "LUT (*.cube)|*.cube|All files|*.*", Title = L("lay_lbl_lut") };
                bool chosen = dlg.ShowDialog(this) == true;
                Activate();
                if (chosen) { _lut.Text = dlg.FileName; Say(LF("lay_lut_chosen", Path.GetFileName(dlg.FileName))); }
                _lut.Focus();
            };
            var clear = UiKit.Btn(L("lay_btn_lut_clear"));
            clear.Click += (s, e) => { _lut.Text = ""; Say(L("lay_lut_cleared")); _lut.Focus(); };
            lutButtons.Children.Add(browse);
            lutButtons.Children.Add(clear);
            lutBox.Children.Add(lutButtons);
            panel.Children.Add(lutBox);

            _status = UiKit.Status();
            panel.Children.Add(_status);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var ok = UiKit.Btn(L("lay_btn_ok"));
            ok.IsDefault = true;
            ok.Click += (s, e) => { if (Apply()) { DialogResult = true; Close(); } };
            var reset = UiKit.Btn(L("lay_btn_reset"));
            reset.Click += (s, e) =>
            {
                UiKit.Select(_position, "Full"); _size.Value = 100; _x.Value = 50; _y.Value = 50;
                _opacity.Value = 100; UiKit.Select(_key, ""); _similarity.Value = 30; _audio.IsChecked = true;
                UiKit.Select(_blend, "Normal"); UiKit.Select(_mask, "None"); _feather.Value = 10;
                _mx.Value = 50; _my.Value = 50; _mw.Value = 100; _mh.Value = 100; _invert.IsChecked = false; _points.Text = "";
                _moves.IsChecked = false; _ex.Value = 50; _ey.Value = 50; _ew.Value = 100; _eh.Value = 100; _follows.IsChecked = false;
                Say(L("lay_reset_done"));
            };
            var cancel = UiKit.Btn(L("lay_btn_cancel"));
            cancel.IsCancel = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(reset);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            _position.SelectionChanged += (s, e) => UpdateEnabled();
            _key.SelectionChanged += (s, e) => UpdateEnabled();
            _mask.SelectionChanged += (s, e) => UpdateEnabled();
            _moves.Checked += (s, e) => UpdateEnabled(); _moves.Unchecked += (s, e) => UpdateEnabled();
            _follows.Checked += (s, e) => UpdateEnabled(); _follows.Unchecked += (s, e) => UpdateEnabled();
            UpdateEnabled();

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 820 };
            Loaded += (s, e) => (_moveToLayer as UIElement ?? _position).Focus();
        }

        /// <summary>Controls that make no sense for the current choice are disabled (and skipped by Tab).</summary>
        private void UpdateEnabled()
        {
            string pos = UiKit.Tag(_position) as string ?? "Full";
            _size.IsEnabled = pos != "Full";
            _x.IsEnabled = _y.IsEnabled = pos == "Custom";
            _similarity.IsEnabled = !string.IsNullOrEmpty(UiKit.Tag(_key) as string);
            _audio.IsEnabled = _item.IsVideo;
            string shape = UiKit.Tag(_mask) as string ?? "None";
            bool m = shape != "None";
            bool follows = _follows.IsChecked == true && _follows.IsEnabled;
            _feather.IsEnabled = _invert.IsEnabled = _moves.IsEnabled = m;
            _points.IsEnabled = shape == "Custom";
            _mx.IsEnabled = _my.IsEnabled = _mw.IsEnabled = _mh.IsEnabled = m && !follows;
            bool moves = m && !follows && _moves.IsChecked == true;
            _ex.IsEnabled = _ey.IsEnabled = _ew.IsEnabled = _eh.IsEnabled = moves;
            _follows.IsEnabled = m && _item.Tracking?.Path != null && _item.Tracking.Path.Count > 0;
        }

        private bool Apply()
        {
            string lut = _lut.Text.Trim().Trim('"');
            if (lut.Length > 0 && !File.Exists(lut))
            {
                Say(L("lay_lut_missing"));
                _lut.Focus();
                return false;
            }
            string shape = UiKit.Tag(_mask) as string ?? "None";
            if (shape == "Custom" && !MaskShapes.TryParsePoints(_points.Text, out _, out string bad))
            {
                Say(bad == "few" || bad == "empty" ? L("lay_points_few") : LF("lay_points_bad", bad));
                _points.Focus();
                return false;
            }
            var ls = _item.Layer ?? new LayerSettings();
            string pos = UiKit.Tag(_position) as string ?? "Full";
            ls.Position = pos;
            ls.Size = pos == "Full" ? 100 : _size.Value;
            ls.X = _x.Value;
            ls.Y = _y.Value;
            ls.Opacity = _opacity.Value / 100.0;
            ls.ChromaKey = UiKit.Tag(_key) as string ?? "";
            ls.KeySimilarity = _similarity.Value / 100.0;
            ls.KeepAudio = _audio.IsChecked == true;
            ls.BlendMode = UiKit.Tag(_blend) as string ?? "Normal";
            ls.Mask = UiKit.Tag(_mask) as string ?? "None";
            ls.MaskFeather = _feather.Value;
            ls.MaskPoints = _points.Text.Trim();
            ls.MaskX = _mx.Value; ls.MaskY = _my.Value; ls.MaskW = _mw.Value; ls.MaskH = _mh.Value;
            ls.MaskInvert = _invert.IsChecked == true;
            ls.MaskMoves = _moves.IsChecked == true;
            ls.MaskEndX = _ex.Value; ls.MaskEndY = _ey.Value; ls.MaskEndW = _ew.Value; ls.MaskEndH = _eh.Value;
            ls.MaskFollows = _follows.IsChecked == true && _follows.IsEnabled;
            _item.Layer = ls;
            _item.LutPath = lut.Length > 0 ? lut : null;
            return true;
        }

        /// <summary>A short spoken summary of a clip's layer settings (for the main window's log).</summary>
        public static string Describe(TimelineItem item)
        {
            var ls = item.Layer ?? new LayerSettings();
            string pos = ls.Position switch
            {
                "Center" => L("lay_pos_center"), "TopLeft" => L("lay_pos_tl"), "TopRight" => L("lay_pos_tr"),
                "BottomLeft" => L("lay_pos_bl"), "BottomRight" => L("lay_pos_br"),
                "Custom" => LF("lay_desc_custom", Math.Round(ls.X), Math.Round(ls.Y)), _ => L("lay_pos_full")
            };
            string s = LF("lay_desc", pos, Math.Round(ls.Position == "Full" ? 100 : ls.Size), Math.Round(ls.Opacity * 100));
            if (!string.IsNullOrEmpty(ls.ChromaKey)) s += ", " + (ls.ChromaKey == "blue" ? L("lay_key_blue") : L("lay_key_green"));
            if (!string.IsNullOrEmpty(ls.BlendMode) && ls.BlendMode != "Normal") s += ", " + LF("lay_desc_blend", L("lay_blend_" + ls.BlendMode.ToLowerInvariant()));
            if (MaskShapes.IsShape(ls.Mask))
            {
                s += ", " + LF("lay_desc_mask", L("lay_mask_" + ls.Mask.ToLowerInvariant()));
                if (ls.MaskInvert) s += ", " + L("lay_desc_inverted");
                if (ls.MaskFollows) s += ", " + LF("lay_desc_follows", item.Tracking?.Target ?? "");
                else if (ls.MaskMoves) s += ", " + L("lay_desc_moves");
            }
            if (!string.IsNullOrEmpty(item.LutPath)) s += ", LUT " + Path.GetFileName(item.LutPath);
            return s;
        }

        private void Say(string text) => UiKit.Announce(_status, text);
    }
}
