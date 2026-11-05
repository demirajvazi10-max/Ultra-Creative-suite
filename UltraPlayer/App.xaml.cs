using System.Windows;
using UltraAccessibleKit;
using UltraAccessibleKit.Theming;

namespace UltraPlayer
{
    public partial class App : Application
    {
        public App()
        {
            Bootstrap.Initialize();

            // Every window (main, About, support guide) is translated to the chosen language as it opens.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => L.Apply((DependencyObject)s)));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.Apply(AppTheme.Dark);
        }
    }
}
