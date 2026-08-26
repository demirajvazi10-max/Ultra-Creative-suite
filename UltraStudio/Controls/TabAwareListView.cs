using System;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using UltraStudio.Services;

namespace UltraStudio.Controls
{
    /// <summary>
    /// Obična native WinForms ListView unutar WindowsFormsHost-a "guta"
    /// Tab/Shift+Tab. Fokus ostane zarobljen unutar liste zauvek, WPF-ova
    /// tab-navigacija ga nikad ne vidi — tačno ono što je Demir prijavio
    /// ("Layers, 0 items" pa ništa dalje, ni meni, ni izlaz).
    ///
    /// Presreće se na DVA mesta radi pouzdanosti (svaki poziv se loguje —
    /// DebugLog / Ctrl+Shift+L — da SLEDEĆI put tačno vidimo koji se od njih
    /// stvarno pozove za ovu konkretnu kontrolu/verziju .NET-a):
    ///   1) ProcessCmdKey — poziva se NAJRANIJE, pre bilo kakve dialog-key/
    ///      tab-stop logike, i ne zavisi od postojanja roditeljskog
    ///      ContainerControl-a. Ovo je uobičajeno najpouzdanija tačka za baš
    ///      ovaj scenario (kontrola direktno hostovana, bez Form/ContainerControl
    ///      roditelja koji bi inače vodio tab-red).
    ///   2) ProcessDialogKey — rezervna tačka, za slučaj da ProcessCmdKey iz
    ///      nekog razloga ne uspe da presretne Tab pre native tab-stop koda.
    /// </summary>
    public class TabAwareListView : ListView
    {
        /// <summary>
        /// WindowsFormsHost koji hostuje ovu listu — postavlja se iz WPF
        /// koda (MainWindow) posle InitializeComponent, jer WinForms
        /// kontrola sama po sebi nema referencu na svog WPF domaćina.
        /// </summary>
        public HwndHost? Host { get; set; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (TryEscapeTab(keyData, "ProcessCmdKey"))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (TryEscapeTab(keyData, "ProcessDialogKey"))
                return true;
            return base.ProcessDialogKey(keyData);
        }

        private bool TryEscapeTab(Keys keyData, string source)
        {
            bool isTab = (keyData & Keys.KeyCode) == Keys.Tab;
            if (!isTab) return false;

            bool isShiftTab = (keyData & Keys.Shift) == Keys.Shift;
            DebugLog.Write($"TabAwareListView[{Name}]: Tab uhvaćen u {source} (shift={isShiftTab}, Host={(Host == null ? "NULL!" : "OK")}).");

            if (Host == null) return false; // ništa da presretnemo bez domaćina — pusti dalje

            var direction = isShiftTab ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
            // BeginInvoke — MoveFocus mora da se pozove POSLE što WinForms
            // završi obradu ovog tastera, ne usred nje.
            Host.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool moved = Host.MoveFocus(new TraversalRequest(direction));
                DebugLog.Write($"TabAwareListView[{Name}]: MoveFocus({direction}) vraćeno={moved}.");
            }));
            return true; // obrađeno OVDE — ne prosleđuj dalje internom WinForms tab-redu
        }

        protected override void OnGotFocus(EventArgs e)
        {
            DebugLog.Write($"TabAwareListView[{Name}]: OnGotFocus (Host={(Host == null ? "NULL!" : "OK")}).");
            base.OnGotFocus(e);
        }
    }
}
