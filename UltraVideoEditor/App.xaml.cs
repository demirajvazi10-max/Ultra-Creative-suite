using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // ── v1.4: brojevi uvek sa TAČKOM (InvariantCulture) ─────────────────
            // Na Windowsu sa srpskim (ili nemačkim) regionalnim podešavanjima decimalni
            // separator je ZAREZ. Desetine mesta u kodu prave FFmpeg komande sa
            // $"...st={x:F2}..." ili $"-t {dur}" — sa zarezom to postaje "st=12,50", a zarez
            // u FFmpeg filteru znači "sledeći filter", pa fade/Ken Burns/Shorts izvoz/animacije
            // pucaju ili rade pogrešno. Isto važi za JSON od AI-ja ("2.5" se čitalo kao 25).
            // Formatiranje (CurrentCulture) se zato postavlja na Invariant za ceo program;
            // jezik interfejsa (CurrentUICulture) ostaje netaknut. Unos korisnika se i dalje
            // prihvata i sa zarezom (sva polja rade Replace(',', '.') pre parsiranja).
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = invariant;
            System.Threading.Thread.CurrentThread.CurrentCulture = invariant;

            base.OnStartup(e);

            // v1.4.5: every window opens in the interface language — the texts written in
            // its XAML (labels, buttons, tooltips, screen-reader names) are translated when it loads
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, args) =>
                {
                    if (s is Window w && ReferenceEquals(args.OriginalSource, w))
                        XamlTranslator.Translate(w, LanguageManager.CurrentLanguage);
                }));

            // v1.4: text in FFmpeg (titles, outro, subtitles) needs a font configuration
            AppPaths.EnsureFontconfig();

            // Accessibility for screen readers
            // NE koristite AutomationProperties.SetName na App klasi
            // Umjesto toga, postavite naslov prozora

            // ── Globalni hendler za nepredviđene greške ──────────────────────
            // Bez ovoga, ako nešto neočekivano pukne, aplikacija se tiho ugasi:
            // sighted korisnik bar vidi da je prozor nestao, ali blind korisnik
            // nema NIKAKAV signal da se bilo šta desilo. Ovo makar pokuša da
            // najavi/prikaže grešku pre gašenja, umesto potpune tišine.
            DispatcherUnhandledException += (s, args) =>
            {
                AppLog.Error("Unexpected error", args.Exception);   // v1.4: full details in the log file
                try
                {
                    // v1.4: in the interface language (it was always Serbian)
                    string lang = LanguageManager.CurrentLanguage;
                    string msg = string.Format(LanguageManager.GetText("app_unexpected_error_msg", lang),
                                               args.Exception.Message, AppLog.TodayFile);
                    System.Windows.MessageBox.Show(msg, LanguageManager.GetText("app_unexpected_error_title", lang),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { /* i sam MessageBox može teoretski da pukne — ne dozvoli da to sakrije originalnu grešku */ }

                // Handled = true znači "ne gasi aplikaciju" — bolje nastaviti sa
                // mogućim čudnim stanjem nego izgubiti ceo neposaćuvan rad bez upozorenja.
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    AppLog.Error("Serious error", ex);
                    string lang = LanguageManager.CurrentLanguage;
                    System.Windows.MessageBox.Show(
                        string.Format(LanguageManager.GetText("app_serious_error_msg", lang), ex?.Message, AppLog.TodayFile),
                        LanguageManager.GetText("app_serious_error_title", lang), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                // Greške iz "fire and forget" async zadataka koje niko nije čekao (await) —
                // bez ovoga nestaju potpuno bez traga.
                AppLog.Error("Background task error", args.Exception);   // v1.4: no longer lost
                args.SetObserved();
            };
        }
    }
}