using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltraStudio.Services
{
    /// <summary>
    /// Detektuje da li je čitač ekrana (JAWS, NVDA, Narrator, itd.) stvarno
    /// aktivan u trenutnoj sesiji, umesto da app uvek pretpostavi JAWS mod.
    ///
    /// Dva nezavisna izvora, kombinovana sa OR (dovoljan je jedan pozitivan):
    /// 1) Zvanični Windows mehanizam — SPI_GETSCREENREADER. Čitači ekrana
    ///    (uključujući JAWS) postavljaju ovu sistemsku zastavicu kad se
    ///    pokrenu, preko SystemParametersInfo(SPI_SETSCREENREADER, ...).
    /// 2) Provera pokrenutih procesa po imenu, kao rezervni mehanizam — za
    ///    slučaj da neka instalacija čitača ekrana ne postavi zastavicu
    ///    pouzdano (starije/portabilne verzije).
    /// </summary>
    internal static class ScreenReaderDetector
    {
        private const int SPI_GETSCREENREADER = 0x0046;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref bool pvParam, int fWinIni);

        // Poznati procesi čitača ekrana (bez ekstenzije, mala slova).
        // Provera je "sadrži", ne "jednako", da pokrije varijante imena.
        private static readonly string[] KnownScreenReaderProcesses =
        {
            "jfw",          // JAWS glavni proces
            "fsuiagent",    // JAWS pomoćni UI Automation proces
            "nvda",         // NVDA
            "narrator",     // Windows Narrator (ugrađen)
            "dolphinnvda",
            "supernova",    // Dolphin SuperNova
            "zdsr",         // ZDSR
            "windoweyes",   // Window-Eyes (stariji, i dalje u upotrebi kod nekih)
            "cobra",        // Cobra Windows Screen Reader
        };

        public static bool IsScreenReaderRunning()
        {
            try
            {
                bool screenReaderActive = false;
                if (SystemParametersInfo(SPI_GETSCREENREADER, 0, ref screenReaderActive, 0) && screenReaderActive)
                    return true;
            }
            catch
            {
                // Nastavi na proveru procesa ako Win32 poziv iz bilo kog razloga ne uspe.
            }

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string name = process.ProcessName.ToLowerInvariant();
                        foreach (var known in KnownScreenReaderProcesses)
                        {
                            if (name.Contains(known))
                                return true;
                        }
                    }
                    catch
                    {
                        // Pojedinačan proces može baciti izuzetak (proces se ugasio,
                        // nema dozvole za čitanje imena) — preskoči ga, ne ruši app.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Ako čak i nabrajanje procesa ne uspe, tiho nastavi — vraćamo
                // "nije detektovan" ispod, a korisnik uvek može ručno da prebaci
                // mod (Alt+W ili dugmad u traci), tako da ovo nikad ne zaključava
                // nikoga van pristupačnog moda.
            }

            return false;
        }
    }
}
