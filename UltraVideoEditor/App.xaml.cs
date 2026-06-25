using System.Windows;
using System.Windows.Automation;

namespace UltraVideoEditor
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Accessibility for screen readers
            // NE koristite AutomationProperties.SetName na App klasi
            // Umjesto toga, postavite naslov prozora
        }
    }
}