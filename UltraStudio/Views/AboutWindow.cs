using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UltraStudio.Localization;

namespace UltraStudio
{
    /// <summary>
    /// Verzija na JEDNOM mestu: &lt;Version&gt; u UltraStudio.csproj
    /// (GitHub Actions ga pri izdanju prepiše verzijom iz taga, npr. studio-v2.0.0 -> 2.0.0).
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
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }
                return asm.GetName().Version?.ToString(3) ?? "2.0.0";
            }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}

namespace UltraStudio.Views
{
    /// <summary>O programu: tekst u read-only polju (čita se red po red) + prava dugmad za linkove.</summary>
    public class AboutWindow : Window
    {
        public AboutWindow(Window? owner)
        {
            Owner = owner;
            Ui.Prepare(this, Lang.T("about_title"), 560, 470);
            ResizeMode = ResizeMode.NoResize;

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var txt = new TextBox
            {
                Text = string.Format(Lang.T("about_text"), AppInfo.Version, AppInfo.SponsorsUrl),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Ui.Res("BrBgMid"), Foreground = Ui.Res("BrText"),
                BorderThickness = new Thickness(0), FontSize = 13, Padding = new Thickness(8)
            };
            txt.SetValue(AutomationProperties.NameProperty, Lang.T("about_title"));
            Grid.SetRow(txt, 0);
            grid.Children.Add(txt);

            var row = Ui.Row(
                Ui.Button(Lang.T("about_btn_sponsors"), () => AppInfo.OpenUrl(AppInfo.SponsorsUrl), primary: true),
                Ui.Button(Lang.T("about_btn_repo"), () => AppInfo.OpenUrl(AppInfo.RepoUrl)),
                Ui.Button(Lang.T("btn_close"), Close, isCancel: true));
            Grid.SetRow(row, 1);
            grid.Children.Add(row);

            Content = grid;
            Loaded += (_, __) => txt.Focus();
        }
    }
}
