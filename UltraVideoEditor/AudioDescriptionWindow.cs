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
        private readonly WControls.ComboBox _voice, _duck;
        private readonly WControls.TextBlock _status;
        private readonly WControls.Button _btnScript, _btnCreate, _btnClose;
        private CancellationTokenSource _cts;

        /// <summary>The script text, kept by the main window while the program runs.</summary>
        public string ScriptText => _script.Text;

        public AudioDescriptionWindow(Window owner, IList<TimelineItem> items, Func<IEnumerable<(double, double)>> busy,
                                      string videoPath, string savedScript, Action<string> announce)
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
            _voice.Items.Add(new WControls.ComboBoxItem { Content = L("ad_voice_google"), Tag = "google" });
            _voice.SelectedIndex = 0;
            panel.Children.Add(Labelled(L("ad_lbl_voice"), _voice));

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
                if (dlg.ShowDialog(this) == true) { _video.Text = dlg.FileName; _video.Focus(); }
            };
            var videoBox = Labelled(L("ad_lbl_video"), _video);
            videoBox.Children.Add(browse);
            panel.Children.Add(videoBox);

            _btnCreate = Btn(L("ad_btn_create"));
            _btnCreate.Click += async (s, e) => await CreateAsync();
            panel.Children.Add(_btnCreate);

            _status = new WControls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Text = " " };
            _status.SetResourceReference(WControls.TextBlock.ForegroundProperty, "TextPrimary");
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            panel.Children.Add(_status);

            _btnClose = Btn(L("about_btn_close"));
            _btnClose.IsCancel = true;
            _btnClose.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            _btnClose.Click += (s, e) => Close();
            panel.Children.Add(_btnClose);

            Content = new WControls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WControls.ScrollBarVisibility.Auto };
            Loaded += async (s, e) =>
            {
                (_script.Text.Length > 0 ? (UIElement)_script : _btnScript).Focus();
                foreach (string v in await AudioDescriptionEngine.WindowsVoicesAsync())
                    _voice.Items.Add(new WControls.ComboBoxItem { Content = LF("ad_voice_windows", v.Replace("|", ", ")), Tag = v });
            };
            Closing += (s, e) => _cts?.Cancel();
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

        private void Say(string text)
        {
            _status.Text = text;
            _announce?.Invoke(text);
        }

        private void Busy(bool busy)
        {
            _btnScript.IsEnabled = !busy;
            _btnCreate.IsEnabled = !busy;
        }

        private async Task MakeScriptAsync()
        {
            if (_script.Text.Trim().Length > 0 &&
                System.Windows.MessageBox.Show(this, L("ad_replace_script"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _cts = new CancellationTokenSource();
            Busy(true);
            Say(L("ad_writing"));
            try
            {
                int calls = 0;
                // shown every time, spoken every fifth step, so a long describe is not silent
                var lines = await AudioDescriptionEngine.BuildScriptAsync(_items, p =>
                {
                    _status.Text = LF("ad_describing", p);
                    if (++calls % 5 == 0) _announce?.Invoke(_status.Text);
                }, _cts.Token);
                _script.Text = AudioDescriptionEngine.ToScript(lines);
                Say(LF("ad_script_ready", lines.Count));
                _script.Focus();
                _script.CaretIndex = 0;
            }
            catch (InvalidOperationException ex) when (ex.Message == "no_model") { Say(L("desc_no_model")); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Writing the audio description script", ex); Say(LF("ad_failed", ex.Message)); }
            finally { Busy(false); }
        }

        private async Task CreateAsync()
        {
            var lines = AudioDescriptionEngine.FromScript(_script.Text, _items);
            if (lines.Count == 0) { Say(L("ad_no_script")); _btnScript.Focus(); return; }
            string video = _video.Text.Trim().Trim('"');
            if (!File.Exists(video)) { Say(L("ad_no_video")); _video.Focus(); return; }

            string voice = (_voice.SelectedItem as WControls.ComboBoxItem)?.Tag as string ?? "google";
            var duck = (_duck.SelectedItem as WControls.ComboBoxItem)?.Tag is AudioDescriptionEngine.Ducking d ? d : AudioDescriptionEngine.Ducking.Medium;
            string lang = LanguageManager.CurrentLanguage ?? "en";
            string work = Path.Combine(Path.GetTempPath(), $"UVE_AD_{Guid.NewGuid():N}".Substring(0, 15));

            _cts = new CancellationTokenSource();
            Busy(true);
            try
            {
                Say(LF("ad_speaking", lines.Count));
                await AudioDescriptionEngine.SpeakAllAsync(lines, voice, lang, work, p => _status.Text = LF("ad_speaking_n", p), _cts.Token);
                int spoken = lines.Count(l => l.Duration > 0.05);
                if (spoken == 0) { Say(L("ad_tts_failed")); return; }

                double total = await AudioDescriptionEngine.DurationAsync(video, _cts.Token);
                AudioDescriptionEngine.Place(lines, _busy(), total);
                int over = lines.Count(l => l.OverSinging);

                Say(L("ad_mixing"));
                string outPath = await AudioDescriptionEngine.RenderAsync(lines, video, Path.GetFileNameWithoutExtension(video), duck, work, _cts.Token);
                Say(LF("ad_done", Path.GetFileName(outPath), spoken, over));
                if (System.Windows.MessageBox.Show(this, LF("ad_done_open", outPath), Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{outPath}\"") { UseShellExecute = true });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Error("Creating the audio description", ex); Say(LF("ad_failed", ex.Message)); }
            finally
            {
                Busy(false);
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }
    }
}
