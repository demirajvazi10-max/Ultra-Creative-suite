using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — "Follow an object" (Ctrl+Alt+O). An object is found on one frame — described in
    /// words for the picture AI ("the man in the red jacket", "the licence plate") or picked
    /// from a part of the picture — and followed through the clip. Then it can be blurred,
    /// pixelated, covered with black or lit (the rest darker); a layer's mask can follow it;
    /// or a layer clip can be moved along with an object in the picture below it (a label,
    /// an arrow or a logo that stays on the object).
    /// Everything is spoken: what was found and where, how far the following got, where the
    /// object went and where it was lost.
    /// </summary>
    public class TrackingWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly TimelineItem _item, _below;
        private readonly double _playhead;
        private readonly double _outAspect;   // output height / width
        private readonly bool _isLayer;

        private readonly WControls.ComboBox _mode, _region, _startAt, _effect;
        private readonly WControls.TextBox _what;
        private readonly WControls.Slider _size, _strength, _padding;
        private readonly WControls.CheckBox _maskFollows;
        private readonly WControls.Button _find, _ok, _remove, _cancel;
        private readonly WControls.TextBlock _status;

        private CancellationTokenSource _cts;
        private List<TrackPoint> _path;          // the result of the last "Find and follow"
        private TimelineItem _pathClip;          // the clip the path belongs to
        private string _target = "";
        private double _srcAspect = 16.0 / 9;   // width / height of the clip whose path was made

        /// <summary>The clip whose path was made (the layer itself, or the clip below it).</summary>
        public bool Changed { get; private set; }
        public string Summary { get; private set; } = "";

        public TrackingWindow(Window owner, TimelineItem item, TimelineItem below, double playheadTimeline, string resolution)
        {
            Owner = owner;
            _item = item;
            _below = below;
            _playhead = playheadTimeline;
            _isLayer = TrackSet.IsLayer(item.TrackIndex);
            _outAspect = 9.0 / 16;
            try { var p = (resolution ?? "1920x1080").Split('x'); _outAspect = double.Parse(p[1]) / double.Parse(p[0]); } catch { }

            Title = LF("obj_title", item.Name ?? "");
            Width = 640;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var tr = item.Tracking;
            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("obj_intro")));
            if (tr?.Path != null && tr.Path.Count > 0)
                panel.Children.Add(UiKit.Text(LF("obj_has_path", tr.Target ?? "", FormatS(tr.Path[^1].T - tr.Path[0].T)), 6));

            var modes = new List<(string, object)> { (L("obj_mode_inside"), "Inside") };
            if (_isLayer && below != null) modes.Add((LF("obj_mode_attach", below.Name ?? ""), "Attach"));
            _mode = UiKit.Combo(modes.ToArray());
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_mode"), _mode));

            _what = new WControls.TextBox { Text = tr?.Target ?? "", MinWidth = 420, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            _what.SetResourceReference(WControls.Control.BackgroundProperty, "ControlBg");
            _what.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_what"), _what));

            var regions = new List<(string, object)> { (L("obj_region_ai"), "AI") };
            regions.AddRange(ObjectTracker.Regions.Select(r => (L("obj_reg_" + r), (object)r)));
            _region = UiKit.Combo(regions.ToArray());
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_region"), _region));

            _size = UiKit.Slider(5, 80, 20, 1);
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_size"), _size));

            _startAt = UiKit.Combo((L("obj_start_playhead"), "Playhead"), (L("obj_start_clip"), "Start"));
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_start"), _startAt));

            _find = UiKit.Btn(L("obj_btn_find"));
            _find.Click += async (s, e) => await FindAsync();
            panel.Children.Add(_find);

            _effect = UiKit.Combo((L("obj_fx_None"), "None"), (L("obj_fx_Blur"), "Blur"), (L("obj_fx_Pixelate"), "Pixelate"),
                                  (L("obj_fx_Black"), "Black"), (L("obj_fx_Spotlight"), "Spotlight"));
            UiKit.Select(_effect, tr?.Effect ?? "Blur");
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_effect"), _effect));

            _strength = UiKit.Slider(1, 100, Math.Clamp(tr?.Strength ?? 50, 1, 100), 1);
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_strength"), _strength));
            _padding = UiKit.Slider(0, 150, Math.Clamp(tr?.Padding ?? 20, 0, 150), 5);
            panel.Children.Add(UiKit.Labelled(L("obj_lbl_padding"), _padding));

            if (_isLayer)
            {
                _maskFollows = UiKit.Check(L("obj_chk_mask"), item.Layer?.MaskFollows == true);
                panel.Children.Add(_maskFollows);
            }

            _status = UiKit.Status();
            panel.Children.Add(_status);

            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _ok = UiKit.Btn(L("obj_btn_ok"));
            _ok.IsDefault = false;
            _ok.Click += (s, e) => { if (Apply()) { DialogResult = true; Close(); } };
            _remove = UiKit.Btn(L("obj_btn_remove"));
            _remove.IsEnabled = item.Tracking != null;
            _remove.Click += (s, e) =>
            {
                _item.Tracking = null;
                if (_item.Layer != null) _item.Layer.MaskFollows = false;
                Changed = true;
                Summary = L("obj_removed");
                DialogResult = true;
                Close();
            };
            _cancel = UiKit.Btn(L("obj_btn_cancel"));
            _cancel.IsCancel = true;
            _cancel.Click += (s, e) =>
            {
                if (_cts != null) { _cts.Cancel(); return; }   // stop the following, keep the window
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(_ok);
            buttons.Children.Add(_remove);
            buttons.Children.Add(_cancel);
            panel.Children.Add(buttons);

            _mode.SelectionChanged += (s, e) => UpdateEnabled();
            _region.SelectionChanged += (s, e) => UpdateEnabled();
            _effect.SelectionChanged += (s, e) => UpdateEnabled();
            UpdateEnabled();

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Loaded += (s, e) => _what.Focus();
            Closing += (s, e) => _cts?.Cancel();
        }

        private bool Attach => (UiKit.Tag(_mode) as string) == "Attach";

        private void UpdateEnabled()
        {
            bool ai = (UiKit.Tag(_region) as string) == "AI";
            _what.IsEnabled = ai;
            _size.IsEnabled = !ai;
            bool inside = !Attach;
            _effect.IsEnabled = inside;
            string fx = UiKit.Tag(_effect) as string ?? "None";
            _strength.IsEnabled = inside && fx != "None" && fx != "Black";
            _padding.IsEnabled = inside;
            if (_maskFollows != null) _maskFollows.IsEnabled = inside;
        }

        private void Say(string text) => UiKit.Announce(_status, text);

        private static string FormatS(double s) => s.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

        /// <summary>The clip that is searched, and its part (file time from, length) and start time.</summary>
        private (TimelineItem Clip, double From, double Length, double StartFile) Range()
        {
            var clip = Attach ? _below : _item;
            double speed = clip.IsImage ? 1 : Math.Clamp(clip.VideoEffect?.Speed is double sp && sp > 0.01 ? sp : 1, 0.1, 10);
            if (TrackSet.IsLayer(clip.TrackIndex)) speed = 1;   // layers play at normal speed
            double t0 = clip.Start, t1 = clip.Start + clip.Duration;
            if (Attach) { t0 = Math.Max(t0, _item.Start); t1 = Math.Min(t1, _item.Start + _item.Duration); }
            if (t1 - t0 < 0.1) return (clip, 0, 0, 0);
            double from = clip.SourceOffset + (t0 - clip.Start) * speed;
            double length = (t1 - t0) * speed;
            double startT = (UiKit.Tag(_startAt) as string) == "Playhead" && _playhead >= t0 && _playhead < t1 ? _playhead : t0;
            double startFile = clip.SourceOffset + (startT - clip.Start) * speed;
            return (clip, from, length, startFile);
        }

        private async Task FindAsync()
        {
            if (_cts != null) return;
            var (clip, from, length, startFile) = Range();
            if (clip == null || clip.IsImage || length < 0.1 || string.IsNullOrEmpty(clip.Path) || !System.IO.File.Exists(clip.Path))
            {
                Say(L(Attach ? "obj_no_overlap" : "obj_video_only"));
                return;
            }
            _cts = new CancellationTokenSource();
            _find.IsEnabled = _ok.IsEnabled = _remove.IsEnabled = false;
            try
            {
                var probe = new LayerCompositor(AppPaths.FFprobe, (a, c) => Task.FromResult(false), null);
                var (sw, sh) = await probe.ProbeSizeAsync(clip.Path, _cts.Token);
                double frameAspect = sw > 0 && sh > 0 ? (double)sw / sh : 16.0 / 9;
                _srcAspect = frameAspect;

                TrackPoint box;
                string region = UiKit.Tag(_region) as string ?? "AI";
                if (region == "AI")
                {
                    string what = _what.Text.Trim();
                    if (what.Length == 0) { Say(L("obj_need_what")); _what.Focus(); return; }
                    Say(LF("obj_looking", what));
                    var (found, problem) = await ObjectTracker.LocateAsync(clip.Path, startFile, what, _cts.Token);
                    if (found == null)
                    {
                        Say(problem switch
                        {
                            "no_ai" => L("obj_no_ai"),
                            "not_found" => LF("obj_not_found", what),
                            "no_frame" => L("obj_no_frame"),
                            _ => LF("obj_ai_error", problem)
                        });
                        _region.Focus();
                        return;
                    }
                    box = found;
                    _target = what;
                    Say(LF("obj_found", what, L("obj_reg_" + ObjectTracker.RegionOf(box.X, box.Y)), Math.Round(box.W * 100)));
                }
                else
                {
                    box = ObjectTracker.RegionBox(region, _size.Value, frameAspect);
                    _target = L("obj_reg_" + region);
                }

                double lastSpoken = -1;
                var path = await ObjectTracker.TrackAsync(clip.Path, from, length, startFile, box, p =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (p - lastSpoken >= 0.25 || p >= 1) { lastSpoken = p; Say(LF("obj_progress", Math.Round(p * 100))); }
                    }));
                }, _cts.Token);
                if (path == null || path.Count == 0) { Say(L("obj_no_frame")); return; }
                _path = path;
                _pathClip = clip;
                Summary = Describe(path, _target);
                Say(Summary + " " + L(Attach ? "obj_then_ok_attach" : "obj_then_ok"));
                _ok.Focus();
            }
            catch (OperationCanceledException) { Say(L("obj_stopped")); }
            catch (Exception ex) { AppLog.Error("Following an object", ex); Say(LF("obj_ai_error", ex.Message)); }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _find.IsEnabled = _ok.IsEnabled = true;
                _remove.IsEnabled = _item.Tracking != null;
            }
        }

        /// <summary>"Followed the dog for 7.9 s. It went from the left to the top right. Not seen 3.2 – 3.6 s."</summary>
        public static string Describe(List<TrackPoint> path, string target)
        {
            var (followed, lost) = ObjectTracker.Summary(path);
            string a = ObjectTracker.RegionOf(path[0].X, path[0].Y), b = ObjectTracker.RegionOf(path[^1].X, path[^1].Y);
            string s = LF("obj_followed", target, FormatS(followed));
            s += " " + (a == b ? LF("obj_stayed", L("obj_reg_" + a)) : LF("obj_moved", L("obj_reg_" + a), L("obj_reg_" + b)));
            if (lost.Count > 0)
                s += " " + LF("obj_lost", string.Join(", ", lost.Take(4).Select(l => $"{FormatS(l.From - path[0].T)}–{FormatS(l.To - path[0].T)}")));
            return s;
        }

        private bool Apply()
        {
            if (_cts != null) return false;
            if (Attach)
            {
                if (_path == null || _pathClip == null) { Say(L("obj_find_first")); _find.Focus(); return false; }
                AttachLayer();
                Changed = true;
                return true;
            }
            var tr = _item.Tracking?.Clone() ?? new TrackingData();
            if (_path != null && ReferenceEquals(_pathClip, _item)) { tr.Path = _path; tr.Target = _target; }
            if (tr.Path == null || tr.Path.Count == 0) { Say(L("obj_find_first")); _find.Focus(); return false; }
            tr.Effect = UiKit.Tag(_effect) as string ?? "None";
            tr.Strength = _strength.Value;
            tr.Padding = _padding.Value;
            _item.Tracking = tr;
            if (_maskFollows != null)
            {
                bool follow = _maskFollows.IsChecked == true;
                if (follow)
                {
                    _item.Layer ??= new LayerSettings();
                    if (!MaskShapes.IsShape(_item.Layer.Mask)) _item.Layer.Mask = "Ellipse";
                    _item.Layer.MaskFollows = true;
                }
                else if (_item.Layer != null) _item.Layer.MaskFollows = false;
            }
            Changed = true;
            if (string.IsNullOrEmpty(Summary)) Summary = Describe(tr.Path, tr.Target);
            Summary += " " + LF("obj_saved", L("obj_fx_" + tr.Effect)) + (_maskFollows?.IsChecked == true ? " " + L("obj_mask_saved") : "");
            return true;
        }

        /// <summary>
        /// The layer gets position keyframes that put its centre on the object, wherever the
        /// object is in the picture below (the clip below is fitted into the frame).
        /// </summary>
        private void AttachLayer()
        {
            var clip = _pathClip;
            double speed = clip.IsImage ? 1 : Math.Clamp(clip.VideoEffect?.Speed is double sp && sp > 0.01 ? sp : 1, 0.1, 10);
            if (TrackSet.IsLayer(clip.TrackIndex)) speed = 1;
            // where the clip's picture sits in a 1 × aspect frame
            double srcAspect = _srcAspect;
            double W = 1, H = _outAspect;
            double k = Math.Min(W / 1.0, H / (1.0 / srcAspect));   // the picture is 1 × 1/srcAspect before fitting
            double pw = 1.0 * k, ph = pw / srcAspect, ox = (W - pw) / 2, oy = (H - ph) / 2;

            var old = _item.Keyframes?.OrderBy(x => x.Time).FirstOrDefault();
            var keys = new List<AnimationKeyframe>();
            foreach (var p in ObjectTracker.Simplify(_path, 40))
            {
                double tl = clip.Start + (p.T - clip.SourceOffset) / speed - _item.Start;
                if (tl < -0.01 || tl > _item.Duration + 0.01) continue;
                double nx = (ox + p.X * pw) / W, ny = (oy + p.Y * ph) / H;
                keys.Add(new AnimationKeyframe
                {
                    Time = Math.Clamp(tl, 0, _item.Duration),
                    X = (nx - 0.5) * 1920,
                    Y = (ny - 0.5) * 1920 * _outAspect,
                    Zoom = old?.Zoom ?? 1, Rotation = old?.Rotation ?? 0, Opacity = old?.Opacity ?? 1
                });
            }
            if (keys.Count == 0) return;
            _item.Keyframes = keys;
            _item.Layer ??= new LayerSettings();
            if (_item.Layer.Position == "Full") _item.Layer.Size = 30;
            _item.Layer.Position = "Custom";
            _item.Layer.X = 50;
            _item.Layer.Y = 50;
            Summary = (string.IsNullOrEmpty(Summary) ? Describe(_path, _target) : Summary) + " " + LF("obj_attached", clip.Name ?? "", keys.Count);
        }
    }
}
