using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using WControls = System.Windows.Controls;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 (roadmap 4.2) — the "Audio description for viewers" window. Built in code, every
    /// control labelled, the keyboard order is the work order:
    ///   1. write the script (from the picture descriptions),
    ///   2. read and change it in a plain text field ("mm:ss.f | text" per line),
    ///   3. choose the voice and how much the music goes down,
    ///   4. choose the finished video and create the described version.
    /// Every step is announced through the main window, so JAWS/NVDA hear the progress.
    /// </summary>
    public class AudioDescriptionWindow : Window
    {
        private static string L(string key) => LanguageManager.GetText(key, LanguageManager.CurrentLanguage ?? "en");
        private static string LF(string key, params object[] a) => string.Format(L(key), a);

        private readonly IList<TimelineItem> _items;
        private readonly Func<IEnumerable<(double Start, double End)>> _busy;
        private readonly Action<string> _announce;
        private readonly WControls.TextBox _script, _video;
        private readonly WControls.ComboBox _voice, _duck, _mode, _density;
        private readonly WControls.TextBlock _status;
        private readonly WControls.Button _btnScript, _btnCreate, _btnClose, _btnStop;
        private readonly WControls.PasswordBox _gcKey, _gemKey;
        private CancellationTokenSource _cts;

        /// <summary>The script text, kept by the main window while the program runs.</summary>
        public string ScriptText => _script.Text;

        public AudioDescriptionWindow(Window owner, IList<TimelineItem> items, Func<IEnumerable<(double, double)>> busy,
                                      string videoPath, string savedScript, Action<string> announce,
                                      IList<(string Label, string Path)> timelineVideos = null)
        {
            Owner = owner;
            _items = items;
            _busy = () => busy();
            _announce = announce;
            Title = L("ad_title");
            Width = 760;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "ControlBg");

            var panel = new WControls.StackPanel { Margin = new Thickness(16) };

            var intro = new WControls.TextBlock { Text = L("ad_intro"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            intro.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            panel.Children.Add(intro);

            // v1.4.5: how the script is written — Gemini watching the whole video (best), or shot by shot
            _mode = new WControls.ComboBox();
            _mode.Items.Add(new WControls.ComboBoxItem { Content = L("ad_mode_gemini"), Tag = "gemini" });
            _mode.Items.Add(new WControls.ComboBoxItem { Content = L("ad_mode_frames"), Tag = "frames" });
            _mode.SelectedIndex = GeminiClient.HasKey ? 0 : 1;
            panel.Children.Add(Labelled(L("ad_lbl_mode"), _mode));

            // v1.4.5: how often the descriptions come (Gemini mode)
            _density = new WControls.ComboBox();
            _density.Items.Add(new WControls.ComboBoxItem { Content = L("ad_density_normal"), Tag = AudioDescriptionEngine.Density.Normal });
            _density.Items.Add(new WControls.ComboBoxItem { Content = L("ad_density_dense"), Tag = AudioDescriptionEngine.Density.Dense });
            _density.Items.Add(new WControls.ComboBoxItem { Content = L("ad_density_pauses"), Tag = AudioDescriptionEngine.Density.Pauses });
            _density.SelectedIndex = 0;
            panel.Children.Add(Labelled(L("ad_lbl_density"), _density));

            _btnScript = Btn(L("ad_btn_script"));
            _btnScript.Click += async (s, e) => await MakeScriptAsync();
            panel.Children.Add(_btnScript);

            _script = new WControls.TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 260, FontSize = 13,
                VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto,
                Text = savedScript ?? ""
            };
            panel.Children.Add(Labelled(L("ad_lbl_script"), _script));

            _voice = new WControls.ComboBox();
            FillVoices();
            panel.Children.Add(Labelled(L("ad_lbl_voice"), _voice));

            // v1.4.5: the Google Cloud key for the HD voices (saved encrypted on this computer)
            _gcKey = new WControls.PasswordBox();
            var saveKey = Btn(L("ad_btn_save_key"));
            saveKey.Click += async (s, e) => await SaveGoogleKeyAsync();
            var keyBox = Labelled(L("ad_lbl_gc_key"), _gcKey);
            var keyHint = new WControls.TextBlock { Text = L("ad_gc_hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            keyHint.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            keyBox.Children.Add(keyHint);
            keyBox.Children.Add(saveKey);
            panel.Children.Add(keyBox);

            // v1.4.5: the Gemini key — Gemini then looks at the pictures and writes the descriptions
            // directly in the program language (much better Serbian than the local model)
            _gemKey = new WControls.PasswordBox();
            var saveGem = Btn(L("ad_btn_save_key"));
            saveGem.Click += async (s, e) => await SaveGeminiKeyAsync();
            var gemBox = Labelled(L("ad_lbl_gemini_key"), _gemKey);
            var gemHint = new WControls.TextBlock { Text = GeminiClient.HasKey ? L("ad_gemini_active") : L("ad_gemini_hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            gemHint.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            gemBox.Children.Add(gemHint);
            gemBox.Children.Add(saveGem);
            panel.Children.Add(gemBox);

            _duck = new WControls.ComboBox();
            _duck.Items.Add(new WControls.ComboBoxItem { Content = L("ad_duck_light"), Tag = AudioDescriptionEngine.Ducking.Light });
            _duck.Items.Add(new WControls.ComboBoxItem { Content = L("ad_duck_medium"), Tag = AudioDescriptionEngine.Ducking.Medium });
            _duck.Items.Add(new WControls.ComboBoxItem { Content = L("ad_duck_strong"), Tag = AudioDescriptionEngine.Ducking.Strong });
            _duck.SelectedIndex = 1;
            panel.Children.Add(Labelled(L("ad_lbl_duck"), _duck));

            _video = new WControls.TextBox { Text = videoPath ?? "" };
            var browse = Btn(L("ad_btn_browse"));
            browse.Margin = new Thickness(0, 4, 0, 0);
            browse.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            browse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "MP4 video|*.mp4|All files|*.*", Title = L("ad_lbl_video") };
                if (File.Exists(_video.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_video.Text);
                bool chosen = dlg.ShowDialog(this) == true;
                // v1.4.5: back to THIS window (the file dialog could hand the focus to the main
                // window), straight on the next step, and say what happened
                Activate();
                if (chosen)
                {
                    _video.Text = dlg.FileName;
                    _btnScript.Focus();
                    Say(LF("ad_video_chosen", Path.GetFileName(dlg.FileName)));
                }
                else _video.Focus();
            };
            // Enter in the video field writes the script
            _video.KeyDown += async (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await MakeScriptAsync(); }
            };
            var videoBox = Labelled(L("ad_lbl_video"), _video);
            videoBox.Children.Add(browse);
            // v1.4: the finished video comes first — the script is made from its shots
            panel.Children.Insert(1, videoBox);

            // v1.4.5: or any video clip already on the timeline, without browsing for the file
            if (timelineVideos != null && timelineVideos.Count > 0)
            {
                var fromTimeline = new WControls.ComboBox();
                fromTimeline.Items.Add(new WControls.ComboBoxItem { Content = L("ad_timeline_pick"), Tag = null });
                int pre = 0;
                foreach (var (label, path) in timelineVideos)
                {
                    fromTimeline.Items.Add(new WControls.ComboBoxItem { Content = label, Tag = path });
                    if (string.Equals(path, videoPath, StringComparison.OrdinalIgnoreCase)) pre = fromTimeline.Items.Count - 1;
                }
                fromTimeline.SelectedIndex = pre;
                fromTimeline.SelectionChanged += (s, e) =>
                {
                    if ((fromTimeline.SelectedItem as WControls.ComboBoxItem)?.Tag is string p && File.Exists(p))
                    {
                        _video.Text = p;
                        // spoken only when the list is closed (arrowing through it reads the items anyway)
                        if (!fromTimeline.IsDropDownOpen) _status.Text = LF("ad_video_chosen", Path.GetFileName(p));
                    }
                };
                fromTimeline.DropDownClosed += (s, e) =>
                {
                    if ((fromTimeline.SelectedItem as WControls.ComboBoxItem)?.Tag is string p) { _btnScript.Focus(); Say(LF("ad_video_chosen", Path.GetFileName(p))); }
                };
                fromTimeline.KeyDown += async (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter && !fromTimeline.IsDropDownOpen) { e.Handled = true; await MakeScriptAsync(); }
                };
                panel.Children.Insert(1, Labelled(L("ad_lbl_timeline_video"), fromTimeline));
            }

            _btnCreate = Btn(L("ad_btn_create"));
            _btnCreate.Click += async (s, e) => await CreateAsync();
            panel.Children.Add(_btnCreate);

            _status = new WControls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Text = " " };
            _status.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Assertive);
            panel.Children.Add(_status);

            // v1.4.5: while the work runs the buttons are disabled; the focus goes to Stop, so the
            // screen reader never loses its place (a disabled focused button leaves it nowhere)
            _btnStop = Btn(L("ad_btn_stop"));
            _btnStop.Visibility = Visibility.Collapsed;
            _btnStop.Click += (s, e) => _cts?.Cancel();
            panel.Children.Add(_btnStop);

            _btnClose = Btn(L("about_btn_close"));
            _btnClose.IsCancel = true;
            _btnClose.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            _btnClose.Click += (s, e) => Close();
            panel.Children.Add(_btnClose);

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto };
            Loaded += async (s, e) =>
            {
                // the first thing to do: the script if there is one, otherwise write it, or choose the video
                (_script.Text.Length > 0 ? (UIElement)_script : File.Exists(_video.Text.Trim().Trim('"')) ? _btnScript : _video).Focus();
                foreach (string v in await AudioDescriptionEngine.WindowsVoicesAsync())
                    _voice.Items.Add(new WControls.ComboBoxItem { Content = LF("ad_voice_windows", v.Replace("|", ", ")), Tag = v });
            };
            Closing += (s, e) => _cts?.Cancel();
        }

        /// <summary>
        /// The voice list: Google HD voices first when a key is saved, then the Microsoft natural
        /// voices (free), the plain Google voice and the installed Windows voices.
        /// </summary>
        private void FillVoices()
        {
            string lang = LanguageManager.CurrentLanguage ?? "en";
            var windows = _voice.Items.Cast<WControls.ComboBoxItem>()
                .Where(i => i.Tag is string t && !t.StartsWith("ms:") && !t.StartsWith("gc:") && t != "google").ToList();
            _voice.Items.Clear();
            if (!string.IsNullOrEmpty(NeuralTts.GoogleKey))
                foreach (var (tag, name) in NeuralTts.GoogleVoices(lang))
                    _voice.Items.Add(new WControls.ComboBoxItem { Content = LF("ad_voice_gc", name), Tag = tag });
            foreach (var (tag, name) in NeuralTts.MicrosoftVoices(lang))
                _voice.Items.Add(new WControls.ComboBoxItem { Content = LF("ad_voice_ms", name), Tag = tag });
            _voice.Items.Add(new WControls.ComboBoxItem { Content = L("ad_voice_google"), Tag = "google" });
            foreach (var w in windows) _voice.Items.Add(w);
            _voice.SelectedIndex = 0;
        }

        private async Task SaveGoogleKeyAsync()
        {
            string key = _gcKey.Password.Trim();
            if (key.Length == 0) { Say(L("ad_gc_empty")); _gcKey.Focus(); return; }
            Say(L("ad_gc_testing"));
            string problem = await NeuralTts.TestGoogleKeyAsync(key, LanguageManager.CurrentLanguage ?? "en", CancellationToken.None);
            if (problem != null) { Sound(false); Say(LF("ad_gc_bad", problem)); _gcKey.Focus(); return; }
            KeyStore.Save(NeuralTts.GoogleKeyName, key);
            _gcKey.Clear();
            FillVoices();
            Sound(true);
            Say(L("ad_gc_saved"));
            _voice.Focus();
        }

        private async Task SaveGeminiKeyAsync()
        {
            string key = _gemKey.Password.Trim();
            if (key.Length == 0) { Say(L("ad_gc_empty")); _gemKey.Focus(); return; }
            Say(L("ad_gc_testing"));
            string problem = await GeminiClient.TestKeyAsync(key, CancellationToken.None);
            if (problem != null) { Sound(false); Say(LF("ad_gc_bad", problem)); _gemKey.Focus(); return; }
            KeyStore.Save(GeminiClient.KeyName, key);
            _gemKey.Clear();
            Sound(true);
            Say(L("ad_gemini_saved"));
            _btnScript.Focus();
        }

        private static WControls.Button Btn(string text)
        {
            var b = new WControls.Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            b.SetResourceReference(WControls.Control.BackgroundProperty, "PanelBg");
            b.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            return b;
        }

        private static WControls.StackPanel Labelled(string label, WControls.Control control)
        {
            var sp = new WControls.StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var lbl = new WControls.Label { Content = label, Target = control, Padding = new Thickness(0, 0, 0, 3) };
            lbl.SetResourceReference(WControls.Control.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetName(control, label.Replace("_", ""));
            sp.Children.Add(lbl);
            sp.Children.Add(control);
            return sp;
        }

        /// <summary>
        /// v1.4.5: shown in the window and spoken by the screen reader from THIS window. Before,
        /// the message went to the main window's status line, and a screen reader does not read
        /// a live region of a window that is behind a dialog, so the work started and ended silently.
        /// </summary>
        private void Say(string text)
        {
            _status.Text = text;
            _announce?.Invoke(text);   // the main window's log
            try
            {
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_status);
                if (peer != null)
                {
                    peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
                    // JAWS 2022+, NVDA and Narrator read UI Automation notifications
                    peer.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.ActionCompleted,
                        System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, text, "UltraVideoEditor.AudioDescription");
                }
            }
            catch (Exception ex) { AppLog.Error("Audio description announcement", ex); }
        }

        /// <summary>Progress: shown every time, spoken only at every quarter (no constant talking).</summary>
        private Action<string> Progress(string key)
        {
            int lastQuarter = 0;
            return p =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(p ?? "", @"^(\d+)/(\d+)$");
                if (!m.Success) { _status.Text = p; return; }
                int done = int.Parse(m.Groups[1].Value), all = Math.Max(1, int.Parse(m.Groups[2].Value));
                int percent = done * 100 / all;
                _status.Text = LF(key, percent);
                int quarter = percent / 25;
                if (quarter > lastQuarter && percent < 100) { lastQuarter = quarter; Say(LF(key, percent)); }
            };
        }

        private static void Sound(bool ok)
        {
            try { (ok ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); } catch { }
        }

        private void Busy(bool busy)
        {
            _btnStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) { _btnStop.UpdateLayout(); _btnStop.Focus(); }
            _btnScript.IsEnabled = !busy;
            _btnCreate.IsEnabled = !busy;
            _video.IsEnabled = !busy;
        }

        private async Task MakeScriptAsync()
        {
            // v1.4: the script is made from the finished video itself (its real shots)
            string video = _video.Text.Trim().Trim('"');
            if (!File.Exists(video)) { Say(L("ad_no_video")); _video.Focus(); return; }
            if (_script.Text.Trim().Length > 0 &&
                System.Windows.MessageBox.Show(this, L("ad_replace_script"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            bool gemini = (_mode.SelectedItem as WControls.ComboBoxItem)?.Tag as string == "gemini";
            if (gemini && !GeminiClient.HasKey) { Say(L("ad_gemini_need_key")); _gemKey.Focus(); return; }
            _cts = new CancellationTokenSource();
            Busy(true);
            if (!gemini) Say(L("ad_writing"));
            UIElement next = _btnScript;
            try
            {
                var lines = gemini
                    ? await AudioDescriptionEngine.BuildScriptWithGeminiAsync(video,
                      (_density.SelectedItem as WControls.ComboBoxItem)?.Tag is AudioDescriptionEngine.Density dn ? dn : AudioDescriptionEngine.Density.Normal,
                      stage =>
                      {
                          var p = stage.Split('|');
                          Say(p[0] switch
                          {
                              "prepare" => L("ad_gemini_prepare"),
                              "upload" => L("ad_gemini_upload"),
                              "part" => LF("ad_gemini_part", p[1], p[2]),
                              _ => L("ad_gemini_watch")
                          });
                      }, Progress("ad_progress_upload"), _cts.Token)
                    : await AudioDescriptionEngine.BuildScriptFromVideoAsync(video, Progress("ad_progress"), _cts.Token);
                _script.Text = AudioDescriptionEngine.ToScript(lines);
                Sound(true);
                next = _script;
                Say(LF("ad_script_ready", lines.Count));
            }
            catch (InvalidOperationException ex) when (ex.Message == "no_model") { Sound(false); Say(L("desc_no_model")); }
            catch (OperationCanceledException) { Say(L("ad_stopped")); }
            catch (Exception ex) { Sound(false); AppLog.Error("Writing the audio description script", ex); Say(LF("ad_failed", ex.Message)); }
            finally
            {
                Busy(false);
                next.Focus();
                if (next == _script) _script.CaretIndex = 0;
            }
        }

        private async Task CreateAsync()
        {
            string video = _video.Text.Trim().Trim('"');
            if (!File.Exists(video)) { Say(L("ad_no_video")); _video.Focus(); return; }
            var lines = AudioDescriptionEngine.FromScript(_script.Text, await AudioDescriptionEngine.DurationAsync(video, CancellationToken.None));
            if (lines.Count == 0) { Say(L("ad_no_script")); _btnScript.Focus(); return; }

            string voice = (_voice.SelectedItem as WControls.ComboBoxItem)?.Tag as string ?? "google";
            var duck = (_duck.SelectedItem as WControls.ComboBoxItem)?.Tag is AudioDescriptionEngine.Ducking d ? d : AudioDescriptionEngine.Ducking.Medium;
            string lang = LanguageManager.CurrentLanguage ?? "en";
            string work = Path.Combine(Path.GetTempPath(), $"UVE_AD_{Guid.NewGuid():N}".Substring(0, 15));

            _cts = new CancellationTokenSource();
            Busy(true);
            try
            {
                Say(L("ad_creating"));
                await AudioDescriptionEngine.SpeakAllAsync(lines, voice, lang, work, Progress("ad_progress_create"), _cts.Token);
                int spoken = lines.Count(l => l.Duration > 0.05);
                if (spoken == 0) { Sound(false); Say(L("ad_tts_failed")); return; }

                double total = await AudioDescriptionEngine.DurationAsync(video, _cts.Token);
                AudioDescriptionEngine.Place(lines, total);
                int over = lines.Count(l => l.OverSinging);
                spoken = lines.Count(l => l.Duration > 0.05);

                Say(L("ad_mixing"));
                string outPath = await AudioDescriptionEngine.RenderAsync(lines, video, Path.GetFileNameWithoutExtension(video), duck, work, _cts.Token);
                Sound(true);
                Say(LF("ad_done", Path.GetFileName(outPath), spoken, over));
                if (System.Windows.MessageBox.Show(this, LF("ad_done_open", outPath), Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{outPath}\"") { UseShellExecute = true });
            }
            catch (OperationCanceledException) { Say(L("ad_stopped")); }
            catch (Exception ex) { Sound(false); AppLog.Error("Creating the audio description", ex); Say(LF("ad_failed", ex.Message)); }
            finally
            {
                Busy(false);
                _btnCreate.Focus();
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }
    }
}
