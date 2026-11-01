using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — one camera of a multicam clip.</summary>
    public class MulticamAngle
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        /// <summary>How much later this camera started than the master (seconds): camera time = master time − Delay.</summary>
        public double Delay { get; set; }
        /// <summary>Length of the camera's file, seconds.</summary>
        public double Length { get; set; }
    }

    /// <summary>v2.0 — a multicam clip: several cameras of one scene, in sync, cut between with the number keys.</summary>
    public class MulticamData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        /// <summary>Where it starts on the timeline and how long it is (the master camera's part).</summary>
        public double Start { get; set; }
        public double Length { get; set; }
        /// <summary>The master camera's file time at <see cref="Start"/>.</summary>
        public double MasterOffset { get; set; }
        public int Track { get; set; }
        public List<MulticamAngle> Angles { get; set; } = new List<MulticamAngle>();

        /// <summary>The file time of camera <paramref name="k"/> (1-based) at a timeline time; NaN when that camera has no picture there.</summary>
        public double SourceAt(int k, double t)
        {
            if (k < 1 || k > Angles.Count) return double.NaN;
            var a = Angles[k - 1];
            double s = t - Start + MasterOffset - a.Delay;
            return s < -0.01 || (a.Length > 0 && s > a.Length - 0.05) ? double.NaN : Math.Max(0, s);
        }
    }

    /// <summary>
    /// v2.0 — puts cameras in sync by their sound: the loudness envelope of each file
    /// (100 values a second, rises only, so claps and words stand out) is compared with the
    /// master's at every shift up to two minutes; the best match is the delay.
    /// </summary>
    public static class AudioSync
    {
        private const int Rate = 4000, Hop = 40;   // 100 envelope values a second
        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static async Task<float[]> EnvelopeAsync(string path, double from, double seconds, CancellationToken ct)
        {
            string args = $"-nostdin -hide_banner -loglevel error -ss {N(Math.Max(0, from))} -t {N(seconds)} -i \"{path}\" -vn -ac 1 -ar {Rate} -f s16le -";
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.FFmpeg, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
            };
            p.Start();
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
            {
                await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
                await p.WaitForExitAsync(ct);
            }
            var b = ms.ToArray();
            int n = b.Length / 2 / Hop;
            var env = new float[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < Hop; j++)
                {
                    short s = BitConverter.ToInt16(b, (i * Hop + j) * 2);
                    sum += s * (double)s;
                }
                env[i] = (float)Math.Log(1e-3 + Math.Sqrt(sum / Hop) / 32768.0);
            }
            // rises only (onsets), then zero mean / unit length
            var on = new float[n];
            for (int i = 1; i < n; i++) on[i] = Math.Max(0, env[i] - env[i - 1]);
            double mean = on.Length == 0 ? 0 : on.Average(x => (double)x);
            double len = 0;
            for (int i = 0; i < n; i++) { on[i] = (float)(on[i] - mean); len += on[i] * on[i]; }
            len = Math.Sqrt(len);
            if (len > 1e-9) for (int i = 0; i < n; i++) on[i] = (float)(on[i] / len);
            return on;
        }

        /// <summary>
        /// The delay of <paramref name="angle"/> against <paramref name="master"/> in seconds
        /// (camera time = master time − delay) and how sure the match is (0 … 1).
        /// </summary>
        public static (double Lag, double Confidence) Find(float[] master, float[] angle, double centerSeconds, double rangeSeconds = 120)
        {
            if (master.Length < 50 || angle.Length < 50) return (0, 0);
            int maxLag = (int)(rangeSeconds * 100), c0 = (int)Math.Round(centerSeconds * 100);
            int best = c0; double bestV = double.MinValue, second = double.MinValue;
            var scores = new double[2 * maxLag + 1];
            for (int lag = c0 - maxLag; lag <= c0 + maxLag; lag++)
            {
                // angle[j] matches master[j + lag]
                int j0 = Math.Max(0, -lag), j1 = Math.Min(angle.Length, master.Length - lag);
                if (j1 - j0 < 200) { scores[lag - c0 + maxLag] = double.MinValue; continue; }
                double s = 0;
                for (int j = j0; j < j1; j++) s += angle[j] * master[j + lag];
                scores[lag - c0 + maxLag] = s;
                if (s > bestV) { bestV = s; best = lag; }
            }
            for (int i = 0; i < scores.Length; i++)
                if (Math.Abs(i - maxLag + c0 - best) > 50 && scores[i] > second) second = scores[i];
            double conf = bestV <= 0 ? 0 : Math.Clamp(1 - Math.Max(0, second) / bestV, 0, 1);
            return (best / 100.0, conf);
        }
    }

    /// <summary>
    /// v2.0 — "Make a multicam clip": choose the cameras (video clips of the same scene), the
    /// one whose sound is used, and how to put them in sync (by their sound, or as they are
    /// placed). Everything is a labelled check box or list; the result is spoken.
    /// </summary>
    public class MulticamWindow : Window
    {
        private static string L(string k) => UiKit.L(k);
        private static string LF(string k, params object[] a) => UiKit.LF(k, a);

        private readonly List<(TimelineItem Item, WControls.CheckBox Box)> _clips = new List<(TimelineItem, WControls.CheckBox)>();
        private readonly WControls.ComboBox _master, _sync;
        private readonly WControls.TextBlock _status;
        private readonly WControls.Button _ok;
        private CancellationTokenSource _cts;

        public List<TimelineItem> Cameras { get; private set; }
        public TimelineItem Master { get; private set; }
        /// <summary>Delay of every camera against the master (seconds), in the order of <see cref="Cameras"/>.</summary>
        public List<double> Delays { get; private set; }
        public string Report { get; private set; } = "";

        public MulticamWindow(Window owner, IList<TimelineItem> videoClips, IEnumerable<TimelineItem> preselected)
        {
            Owner = owner;
            Title = L("mc_title");
            Width = 620;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");
            var pre = new HashSet<TimelineItem>(preselected ?? Enumerable.Empty<TimelineItem>());

            var panel = new WControls.StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(UiKit.Text(L("mc_intro")));
            panel.Children.Add(UiKit.Text(L("mc_lbl_cameras"), 10));
            foreach (var c in videoClips)
            {
                var box = UiKit.Check(LF("mc_clip", c.Name ?? "", TrackSet.Name(c.TrackIndex), FormatS(c.Duration)), pre.Contains(c));
                box.Checked += (s, e) => FillMaster();
                box.Unchecked += (s, e) => FillMaster();
                _clips.Add((c, box));
                panel.Children.Add(box);
            }
            _master = UiKit.Combo((L("mc_master_none"), (object)null));
            panel.Children.Add(UiKit.Labelled(L("mc_lbl_master"), _master));
            _sync = UiKit.Combo((L("mc_sync_sound"), "Sound"), (L("mc_sync_place"), "Place"));
            panel.Children.Add(UiKit.Labelled(L("mc_lbl_sync"), _sync));

            _status = UiKit.Status();
            panel.Children.Add(_status);
            var buttons = new WControls.StackPanel { Orientation = WControls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _ok = UiKit.Btn(L("mc_btn_ok"));
            _ok.IsDefault = true;
            _ok.Click += async (s, e) => await MakeAsync();
            var cancel = UiKit.Btn(L("mc_btn_cancel"));
            cancel.IsCancel = true;
            cancel.Click += (s, e) => { if (_cts != null) { _cts.Cancel(); return; } DialogResult = false; Close(); };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            FillMaster();

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto, MaxHeight = 860 };
            Loaded += (s, e) => { if (_clips.Count > 0) _clips[0].Box.Focus(); };
            Closing += (s, e) => _cts?.Cancel();
        }

        private static string FormatS(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss");

        private List<TimelineItem> Checked() => _clips.Where(c => c.Box.IsChecked == true).Select(c => c.Item).ToList();

        private void FillMaster()
        {
            var keep = UiKit.Tag(_master) as TimelineItem;
            _master.Items.Clear();
            var list = Checked();
            int k = 0;
            foreach (var c in list)
                _master.Items.Add(new WControls.ComboBoxItem { Content = LF("mc_cam", ++k, c.Name ?? ""), Tag = c });
            if (list.Count == 0) _master.Items.Add(new WControls.ComboBoxItem { Content = L("mc_master_none"), Tag = null });
            if (keep != null && list.Contains(keep)) UiKit.Select(_master, keep); else _master.SelectedIndex = 0;
        }

        private void Say(string t) => UiKit.Announce(_status, t);

        private async Task MakeAsync()
        {
            if (_cts != null) return;
            var cams = Checked();
            if (cams.Count < 2 || cams.Count > 9) { Say(L("mc_need_2_9")); return; }
            var master = UiKit.Tag(_master) as TimelineItem ?? cams[0];
            _cts = new CancellationTokenSource();
            _ok.IsEnabled = false;
            try
            {
                var delays = new List<double>();
                var lines = new List<string>();
                if ((UiKit.Tag(_sync) as string) == "Place")
                {
                    foreach (var c in cams)
                        delays.Add((c.Start - c.SourceOffset) - (master.Start - master.SourceOffset));
                }
                else
                {
                    Say(L("mc_listening"));
                    // up to three minutes of the master from its in-point; the cameras from a bit earlier
                    double span = Math.Min(180, Math.Max(10, master.Duration));
                    var m = await AudioSync.EnvelopeAsync(master.Path, master.SourceOffset, span, _cts.Token);
                    int k = 0;
                    foreach (var c in cams)
                    {
                        k++;
                        if (ReferenceEquals(c, master)) { delays.Add(0); continue; }
                        Say(LF("mc_syncing", k, cams.Count));
                        // the camera's own guess from its place on the timeline is the centre of the search
                        double guess = (c.Start - c.SourceOffset) - (master.Start - master.SourceOffset);
                        double from = Math.Max(0, master.SourceOffset - guess - 120);
                        var a = await AudioSync.EnvelopeAsync(c.Path, from, span + 240, _cts.Token);
                        var (lag, conf) = AudioSync.Find(m, a, guess + from - master.SourceOffset, 120);
                        // a[j] ↔ m[j + lag]: camera time from + j/100 = master time (master.SourceOffset + (j+lag)/100) − delay
                        double delay = master.SourceOffset + lag - from;
                        if (m.Length < 50 || a.Length < 50 || conf < 0.08)
                        {
                            delay = guess;
                            lines.Add(LF("mc_sync_unsure", k, c.Name ?? ""));
                        }
                        else lines.Add(LF("mc_sync_found", k, c.Name ?? "", (delay >= 0 ? "+" : "") + delay.ToString("0.00", CultureInfo.CurrentCulture),
                                          Math.Round(conf * 100)));
                        delays.Add(delay);
                    }
                }
                Cameras = cams;
                Master = master;
                Delays = delays;
                Report = string.Join(" ", lines);
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException) { Say(L("mc_stopped")); }
            catch (Exception ex) { AppLog.Error("Multicam sync", ex); Say(ex.Message); }
            finally { _cts?.Dispose(); _cts = null; if (IsLoaded) _ok.IsEnabled = true; }
        }
    }
}
