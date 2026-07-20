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
            DispatcherUnhandledException += (s, ex) =>
            {
                MessageBox.Show(string.Format(Lang.T("unexpected_error"), ex.Exception.Message, ex.Exception.StackTrace),
                    Lang.T("unexpected_error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };
        }
    }
}
