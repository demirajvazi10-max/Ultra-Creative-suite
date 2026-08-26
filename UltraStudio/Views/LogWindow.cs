using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraStudio.Localization;
using UltraStudio.Services;

namespace UltraStudio.Views
{
    /// <summary>
    /// Prozor sa dnevnikom dijagnostike (Ctrl+Shift+L) — isti obrazac kao u
    /// Video Editoru. Obična read-only TextBox (ne TextBlock!) sa celim
    /// tekstom odjednom — JAWS to čita standardno kao "edit, read only, sa
    /// tekstom", pouzdano i bez potrebe za live-region trikovima, jer se
    /// tekst NE menja dok je prozor otvoren (osim na eksplicitni "Osveži").
    /// </summary>
    public partial class LogWindow : Window
    {
        private readonly TextBox _logBox;

        public LogWindow(Window owner)
        {
            Title = Lang.T("log_window_title");
            Owner = owner;
            Width = 720; Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var pathLabel = new TextBlock
            {
                Text = string.Format(Lang.T("log_window_path"), DebugLog.LogFilePath),
                Foreground = (Brush)Application.Current.Resources["BrTextMuted"],
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(pathLabel, 0);
            grid.Children.Add(pathLabel);

            _logBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = (Brush)Application.Current.Resources["BrBgPanel"],
                Foreground = (Brush)Application.Current.Resources["BrText"],
                BorderThickness = new Thickness(1)
            };
            _logBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, Lang.T("log_window_title"));
            Grid.SetRow(_logBox, 1);
            grid.Children.Add(_logBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var refresh = new Button
            {
                Content = Lang.T("log_window_refresh"), Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["StdButton"]
            };
            var copyAll = new Button
            {
                Content = Lang.T("log_window_copy"), Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["StdButton"]
            };
            var close = new Button
            {
                Content = Lang.T("btn_close"), Width = 90, Height = 30,
                Style = (Style)Application.Current.Resources["AIButton"], IsCancel = true
            };
            refresh.Click += (_, __) => LoadContent();
            copyAll.Click += (_, __) =>
            {
                Clipboard.SetText(_logBox.Text);
                SetTitleConfirm();
            };
            close.Click += (_, __) => Close();
            btnPanel.Children.Add(refresh);
            btnPanel.Children.Add(copyAll);
            btnPanel.Children.Add(close);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (_, __) => { LoadContent(); _logBox.Focus(); };
        }

        private void LoadContent()
        {
            // Namerno se čita iz memorije (DebugLog.GetRecentText), ne sa diska —
            // svaki DebugLog.Write ide u oba, ali memorija je trenutna i ne zavisi
            // od toga da li je upis na disk uspeo (dozvole, zauzet fajl, itd.).
            _logBox.Text = DebugLog.GetRecentText();
            _logBox.CaretIndex = _logBox.Text.Length;
            _logBox.ScrollToEnd();
        }

        private void SetTitleConfirm()
        {
            string original = Title;
            Title = Lang.T("log_window_copied");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, __) => { Title = original; timer.Stop(); };
            timer.Start();
        }
    }
}
