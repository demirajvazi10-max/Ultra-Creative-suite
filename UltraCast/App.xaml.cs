using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using UltraAccessibleKit;
using UltraAccessibleKit.Theming;
using UltraCast.Services;

namespace UltraCast
{
    public partial class App : Application
    {
        public App()
        {
            Bootstrap.Initialize();

            // Every window (main, About, support guide) is translated to the chosen language as it opens.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => L.Apply((DependencyObject)s)));

            // Without these hooks, an unhandled exception on a background
            // thread (the screen-capture loop, the audio pump, an NAudio
            // callback) kills the whole process silently - no dialog, no
            // log line, nothing. That's exactly the "app just bugs out
            // with no feedback" symptom. Catching it here at least logs
            // what happened before anything crashes, and for UI-thread
            // exceptions specifically, lets the app keep running.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                AppLogger.Log("FATAL (unhandled, background thread): " + e.ExceptionObject);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Log("Unobserved task exception: " + e.Exception);
                e.SetObserved();
            };

            DispatcherUnhandledException += (_, e) =>
            {
                AppLogger.Log("Unhandled UI-thread exception: " + e.Exception);
                MessageBox.Show(
                    L.F("Ultra Cast ran into a problem and had to recover:\n\n{0}\n\nDetails were written to:\n{1}",
                        e.Exception.Message, AppLogger.LogFilePath),
                    L.T("Ultra Cast - Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                // Handled = true keeps the app running instead of crashing outright.
                e.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.Apply(AppTheme.Dark);
            AppLogger.Log("Ultra Cast started.");
        }
    }
}
