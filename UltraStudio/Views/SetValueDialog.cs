using System.Windows;
using System.Windows.Controls;
using UltraStudio.Localization;

namespace UltraStudio.Views
{
    public partial class SetValueDialog : Window
    {
        private readonly TextBox _input;
        public string ResultValue { get; private set; } = "";

        public SetValueDialog(string title, string prompt, string currentValue, string unit)
        {
            Title = title;
            Width = 360; Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrText"] };
            Grid.SetRow(lbl, 0); grid.Children.Add(lbl);

            _input = new TextBox { Text = currentValue, Height = 30 };
            _input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, prompt);
            Grid.SetRow(_input, 1); grid.Children.Add(_input);

            if (!string.IsNullOrEmpty(unit))
            {
                var unitLbl = new TextBlock { Text = unit, Margin = new Thickness(0, 4, 0, 0),
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["BrTextMuted"], FontSize = 10 };
                Grid.SetRow(unitLbl, 2); grid.Children.Add(unitLbl);
            }

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = Lang.T("btn_apply"), Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["AIButton"], IsDefault = true };
            var cancel = new Button { Content = Lang.T("btn_cancel"), Width = 90, Height = 30,
                Style = (Style)Application.Current.Resources["StdButton"], IsCancel = true };
            ok.Click += (_, __) => { ResultValue = _input.Text; DialogResult = true; };
            btnPanel.Children.Add(ok); btnPanel.Children.Add(cancel);
            Grid.SetRow(btnPanel, 3); grid.Children.Add(btnPanel);

            Content = grid;
            Loaded += (_, __) => { _input.Focus(); _input.SelectAll(); };
        }
    }
}
