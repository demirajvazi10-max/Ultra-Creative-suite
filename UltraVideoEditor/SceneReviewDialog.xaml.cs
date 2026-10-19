using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
// WinForms is also imported globally (UseWindowsForms + ImplicitUsings) — alias the clashing names
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfCursors = System.Windows.Input.Cursors;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 — Scene review for the AI music video creator.
    ///
    /// Shown after every lyric scene has its clip and before the video is assembled.
    /// For each scene the user hears/sees: the lyric, what the clip shows (vision AI
    /// description), the quality score and the search words. Any scene can get a new clip,
    /// with the same or edited search words, without regenerating the whole video.
    ///
    /// Built for screen readers first: one list, each item's accessible name is a complete
    /// sentence; results of "Find another clip" are announced through a live region and the
    /// focus returns to the same scene in the list.
    /// </summary>
    public partial class SceneReviewDialog : Window
    {
        private string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private string L(string key) => LanguageManager.GetText(key, _LangCode);
        private string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private readonly List<TimelineSegment> _scenes;
        private readonly IList<string> _lyrics;
        private readonly Func<TimelineSegment, string, Task<bool>> _reroll;
        private readonly Func<double, string> _formatTime;
        private bool _busy;

        public SceneReviewDialog(List<TimelineSegment> lyricScenes,
                                 IList<string> lyrics,
                                 Func<TimelineSegment, string, Task<bool>> reroll,
                                 Func<double, string> formatTime)
        {
            InitializeComponent();
            UiScaling.Register(this);

            _scenes = lyricScenes ?? new List<TimelineSegment>();
            _lyrics = lyrics ?? new List<string>();
            _reroll = reroll;
            _formatTime = formatTime ?? (t => TimeSpan.FromSeconds(t).ToString(@"m\:ss"));

            Title = L("review_title");
            txtInstructions.Text = L("review_instructions");
            lblQuery.Text = L("review_query_label");
            btnReroll.Content = L("review_reroll");
            btnOpenClip.Content = L("review_open_clip");
            btnBuild.Content = L("review_build");
            btnCancel.Content = L("review_cancel");
            AutomationProperties.SetName(lstScenes, L("review_scene_list"));
            AutomationProperties.SetHelpText(lstScenes, L("review_instructions"));
            AutomationProperties.SetName(txtQuery, L("review_query_label"));

            foreach (var seg in _scenes)
                lstScenes.Items.Add(MakeItem(seg));

            Loaded += (s, e) =>
            {
                if (lstScenes.Items.Count > 0)
                {
                    lstScenes.SelectedIndex = 0;
                    (lstScenes.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                }
                Announce(LF("review_opened", _scenes.Count));
            };
        }

        // ── Items ────────────────────────────────────────────────────────────
        private string Describe(TimelineSegment seg, int index)
        {
            string lyric = seg.LyricIndex >= 0 && seg.LyricIndex < _lyrics.Count ? _lyrics[seg.LyricIndex] : seg.LyricText;
            string what = string.IsNullOrWhiteSpace(seg.VisionDescription) ? L("review_no_description") : seg.VisionDescription.Trim();
            string query = string.IsNullOrWhiteSpace(seg.Query) ? "—" : seg.Query;
            return LF("review_item",
                index + 1, _scenes.Count, _formatTime(seg.StartTime),
                lyric ?? "", what, seg.VisionScore.ToString("0.#"), query);
        }

        private ListBoxItem MakeItem(TimelineSegment seg)
        {
            int index = _scenes.IndexOf(seg);
            string text = Describe(seg, index);
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                Tag = seg,
                Padding = new Thickness(4, 6, 4, 6)
            };
            AutomationProperties.SetName(item, text);
            return item;
        }

        private void RefreshItem(int index)
        {
            if (index < 0 || index >= lstScenes.Items.Count) return;
            if (lstScenes.Items[index] is not ListBoxItem item || item.Tag is not TimelineSegment seg) return;
            string text = Describe(seg, index);
            if (item.Content is TextBlock tb) tb.Text = text;
            AutomationProperties.SetName(item, text);
        }

        private TimelineSegment Selected => (lstScenes.SelectedItem as ListBoxItem)?.Tag as TimelineSegment;

        private void lstScenes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            txtQuery.Text = Selected?.Query ?? "";
        }

        // ── Actions ──────────────────────────────────────────────────────────
        private async void btnReroll_Click(object sender, RoutedEventArgs e) => await RerollSelectedAsync();

        private async void txtQuery_KeyDown(object sender, WpfKeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RerollSelectedAsync();
        }

        private async Task RerollSelectedAsync()
        {
            var seg = Selected;
            int index = lstScenes.SelectedIndex;
            if (seg == null || _reroll == null || _busy) return;

            string query = string.IsNullOrWhiteSpace(txtQuery.Text) ? seg.Query : txtQuery.Text.Trim();
            if (string.IsNullOrWhiteSpace(query)) { Announce(L("review_enter_query")); txtQuery.Focus(); return; }

            SetBusy(true);
            Announce(LF("review_searching", index + 1, query));
            bool ok = false;
            try
            {
                ok = await _reroll(seg, query);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Announce(LF("generic_error", ex.Message));
            }
            finally
            {
                SetBusy(false);
            }

            if (ok)
            {
                RefreshItem(index);
                string what = string.IsNullOrWhiteSpace(seg.VisionDescription) ? L("review_no_description") : seg.VisionDescription.Trim();
                Announce(LF("review_new_clip", index + 1, what, seg.VisionScore.ToString("0.#")));
            }
            else
            {
                Announce(LF("review_no_clip", query));
            }

            lstScenes.SelectedIndex = index;
            (lstScenes.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
        }

        private void btnOpenClip_Click(object sender, RoutedEventArgs e)
        {
            var seg = Selected;
            if (seg == null || string.IsNullOrEmpty(seg.Path) || !File.Exists(seg.Path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(seg.Path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Announce(LF("generic_error", ex.Message));
            }
        }

        private void btnBuild_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            DialogResult = false;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Don't close in the middle of a download — the clip would be half-replaced.
            if (_busy) { e.Cancel = true; Announce(L("review_wait")); }
            base.OnClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            btnReroll.IsEnabled = !busy;
            btnBuild.IsEnabled = !busy;
            btnCancel.IsEnabled = !busy;
            btnOpenClip.IsEnabled = !busy;
            Cursor = busy ? WpfCursors.Wait : null;
        }

        private void Announce(string message)
        {
            txtStatus.Text = message;
            var peer = UIElementAutomationPeer.CreatePeerForElement(txtStatus) ?? UIElementAutomationPeer.FromElement(txtStatus);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
