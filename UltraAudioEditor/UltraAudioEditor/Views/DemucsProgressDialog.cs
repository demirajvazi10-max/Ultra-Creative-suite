using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

using UltraAudioEditor.Localization;

namespace UltraAudioEditor.Views
{
    /// <summary>
    /// Poseban modalni prozor za praćenje Demucs razdvajanja — po uzoru na
    /// instalacione čarobnjake (Windows Installer i slično). Zaseban prozor je
    /// namerno biran umesto deljene statusne trake: kad se prozor otvori, JAWS
    /// ga pouzdano prijavi (fokus/naslov), pa se progres čita iz jasnog,
    /// izolovanog konteksta umesto da se oslanja na live region negde u
    /// pozadini glavnog prozora.
    /// </summary>
    public partial class DemucsProgressDialog : Window
    {
        private readonly TextBlock _statusText;
        private readonly ProgressBar _bar;
        private readonly Button _cancelBtn;
        private bool _done;

        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();
        public bool WasCancelled { get; private set; }

        public DemucsProgressDialog(string title)
        {
            Width = 440; Height = 230;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Title = title;
            Background = (Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid { Margin = new Thickness(24) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _statusText = new TextBlock
            {
                Text = Lang.T("demucs_dialog_starting"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["BrText"],
                Margin = new Thickness(0, 0, 0, 14)
            };
            SetLiveName(_statusText, _statusText.Text);
            Grid.SetRow(_statusText, 0);
            grid.Children.Add(_statusText);

            _bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 22, IsIndeterminate = true };
            _bar.SetValue(AutomationProperties.NameProperty, Lang.T("demucs_dialog_progress_indeterminate"));
            Grid.SetRow(_bar, 1);
            grid.Children.Add(_bar);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 16, 0, 0)
            };
            _cancelBtn = new Button
            {
                Content = Lang.T("btn_cancel"), Width = 100, Height = 32,
                Style = (Style)Application.Current.Resources["StdButton"]
            };
            _cancelBtn.SetValue(AutomationProperties.NameProperty, Lang.T("demucs_dialog_cancel_hint"));
            _cancelBtn.Click += (_, __) =>
            {
                if (_done) { Close(); return; }
                WasCancelled = true;
                Cts.Cancel();
                _cancelBtn.IsEnabled = false;
                SetStatus(Lang.T("demucs_dialog_cancelling"));
            };
            btnPanel.Children.Add(_cancelBtn);
            Grid.SetRow(btnPanel, 3);
            grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (_, __) => _cancelBtn.Focus();

            // Sprečava zatvaranje preko Alt+F4/X dugmeta dok traje obrada — samo
            // Cancel dugme (koje uredno otkazuje) sme da zatvori dok se ne završi.
            Closing += (_, e) => { if (!_done && !WasCancelled) e.Cancel = true; };
        }

        /// <summary>Ažurira tekst i procenat; prelazi u determinisani (brojčani) prikaz čim stigne prvi realan procenat.</summary>
        public void SetProgress(int percent, string? statusLine = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (percent >= 0)
                {
                    _bar.IsIndeterminate = false;
                    _bar.Value = percent;
                    _bar.SetValue(AutomationProperties.NameProperty, string.Format(Lang.T("demucs_dialog_progress_name"), percent));
                    RaiseLive(_bar);
                    SetStatus(string.Format(Lang.T("demucs_progress"), percent));
                }
                else if (statusLine != null)
                {
                    SetStatus($"Demucs: {statusLine}");
                }
            });
        }

        public void SetStatus(string text)
        {
            Dispatcher.Invoke(() =>
            {
                _statusText.Text = text;
                SetLiveName(_statusText, text);
                RaiseLive(_statusText);
            });
        }

        public void Finish(string finalMessage)
        {
            Dispatcher.Invoke(() =>
            {
                _done = true;
                _bar.IsIndeterminate = false;
                _bar.Value = 100;
                SetStatus(finalMessage);
                _cancelBtn.Content = Lang.T("btn_close");
                _cancelBtn.IsEnabled = true;
                _cancelBtn.Focus();
            });
        }

        private static void SetLiveName(FrameworkElement el, string text)
        {
            el.SetValue(AutomationProperties.NameProperty, text);
            el.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Polite);
        }

        private static void RaiseLive(UIElement el)
        {
            var peer = UIElementAutomationPeer.FromElement(el) ?? UIElementAutomationPeer.CreatePeerForElement(el);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
