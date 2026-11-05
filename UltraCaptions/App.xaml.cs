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

            // Every window (main, About, support guide) is translated to the chosen language as it opens.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => L.Apply((DependencyObject)s)));
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
