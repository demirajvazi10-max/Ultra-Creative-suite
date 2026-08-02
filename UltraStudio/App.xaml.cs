using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using UltraStudio.Localization;

namespace UltraStudio
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Lang.Load();

            // Globalni hendler za nepredviđene greške — naučeno večeras (Video/Audio
            // Editor su ovo dobili tek naknadno). Bez ovoga, neočekivana greška tiho
            // ugasi program — blind korisnik nema NIKAKAV signal da se bilo šta desilo.
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    MessageBox.Show(
                        $"An unexpected error occurred: {args.Exception.Message}\n\n" +
                        "The application will try to keep running.",
                        "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    MessageBox.Show($"A serious error occurred: {ex?.Message}\n\nThe app may need to close.",
                        "Serious error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, args) => args.SetObserved();
        }
    }
}
