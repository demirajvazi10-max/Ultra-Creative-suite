using System.Windows;
using UltraAccessibleKit;
using UltraAccessibleKit.Theming;

namespace UltraRecord
{
    public partial class App : Application
    {
        public App()
        {
            Bootstrap.Initialize();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.Apply(AppTheme.Dark);
        }
    }
}
