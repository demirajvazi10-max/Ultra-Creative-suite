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
            base.OnStartup(e);

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
                try
                {
                    string msg = $"Došlo je do neočekivane greške: {args.Exception.Message}\n\n" +
                                 "Aplikacija će pokušati da nastavi sa radom. Ako se ovo ponavlja, " +
                                 "sačuvajte projekat pod novim imenom kao bezbednosnu meru.";
                    System.Windows.MessageBox.Show(msg, "Neočekivana greška",
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
                    System.Windows.MessageBox.Show(
                        $"Došlo je do ozbiljne greške: {ex?.Message}\n\nAplikacija se možda mora zatvoriti.",
                        "Ozbiljna greška", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                // Greške iz "fire and forget" async zadataka koje niko nije čekao (await) —
                // bez ovoga nestaju potpuno bez traga.
                args.SetObserved();
            };
        }
    }
}