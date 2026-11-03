using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UltraAudioEditor.Localization;

namespace UltraAudioEditor
{
    /// <summary>
    /// Verzija aplikacije na JEDNOM mestu: &lt;Version&gt; u UltraAudioEditor.csproj
    /// (GitHub Actions ga pri izdanju prepiše verzijom iz taga, npr. audio-v1.1.0 -> 1.1.0).
    /// </summary>
    public static class AppInfo
    {
        public const string SponsorsUrl = "https://github.com/sponsors/demirajvazi10-max";
        public const string RepoUrl = "https://github.com/demirajvazi10-max/Ultra-Creative-suite";

        public static string Version
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');   // .NET 8 dodaje "+<git hash>"
                    return plus > 0 ? info[..plus] : info;
                }
                return asm.GetName().Version?.ToString(3) ?? "1.0.0";
            }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}

namespace UltraAudioEditor.Views
{
    /// <summary>
    /// About prozor. Tekst je u read-only TextBox-u (čitač ekrana ga čita red po red),
    /// a GitHub Sponsors i repozitorijum su prava dugmad — u MessageBox-u se link ne može kliknuti.
    /// </summary>
    public class AboutWindow : Window
    {
        public AboutWindow(Window? owner)
        {
            Owner = owner;
            Title = Lang.T("about_title");
            Width = 560; Height = 460;
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = (Brush)Application.Current.Resources["BrBgDark"];

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txt = new TextBox
            {
                Text = string.Format(Lang.T("about_text"), AppInfo.Version, AppInfo.SponsorsUrl),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = (Brush)Application.Current.Resources["BrBgMid"],
                Foreground = (Brush)Application.Current.Resources["BrText"],
                BorderThickness = new Thickness(0), FontSize = 13, Padding = new Thickness(8)
            };
            txt.SetValue(AutomationProperties.NameProperty, Lang.T("about_title"));
            Grid.SetRow(txt, 0);
            grid.Children.Add(txt);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Button Make(string text, string style, Action onClick, bool isCancel = false)
            {
                var b = new Button
                {
                    Content = text, Height = 32, Padding = new Thickness(12, 0, 12, 0),
                    Margin = new Thickness(8, 0, 0, 0), IsCancel = isCancel,
                    Style = (Style)Application.Current.Resources[style]
                };
                b.SetValue(AutomationProperties.NameProperty, text);
                b.Click += (_, __) => onClick();
                buttons.Children.Add(b);
                return b;
            }
            Make(Lang.T("about_btn_sponsors"), "AIButton", () => AppInfo.OpenUrl(AppInfo.SponsorsUrl));
            Make(Lang.T("about_btn_repo"), "StdButton", () => AppInfo.OpenUrl(AppInfo.RepoUrl));
            Make(Lang.T("btn_close"), "StdButton", Close, isCancel: true);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);

            Content = grid;
            Loaded += (_, __) => txt.Focus();
        }
    }
}
