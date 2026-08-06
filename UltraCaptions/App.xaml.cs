using System.Windows;
using UltraAccessibleKit;
using UltraAccessibleKit.Theming;

namespace UltraCaptions
{
    public partial class App : Application
    {
        public App()
        {
            // Auto-fills any missing screen-reader labels across every window.
            Bootstrap.Initialize();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Default theme. The user can switch via the theme picker in the
            // toolbar (UltraAccessibleKit.Theming.ThemeSwitcher) at any time.
            ThemeManager.Apply(AppTheme.Dark);
        }
    }
}
