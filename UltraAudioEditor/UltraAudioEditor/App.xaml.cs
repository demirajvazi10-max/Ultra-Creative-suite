using System.Windows;
using UltraAudioEditor.Localization;

namespace UltraAudioEditor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Ucitaj sacuvani jezik i primeni prevode pre otvaranja glavnog prozora
            Lang.Load();
            Lang.ApplyToResources();
            Services.DebugLog.Write($"=== Ultra Audio Editor {AppInfo.Version} started ===");
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                Services.DebugLog.Write("FATAL (AppDomain): " + ex.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Services.DebugLog.Write("Unobserved task exception: " + ex.Exception);
                ex.SetObserved();
            };
            DispatcherUnhandledException += (s, ex) =>
            {
                Services.DebugLog.Write("Unhandled UI exception: " + ex.Exception);
                MessageBox.Show(string.Format(Lang.T("unexpected_error"), ex.Exception.Message, ex.Exception.StackTrace),
                    Lang.T("unexpected_error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }
    }
}
