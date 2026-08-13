using System;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace UltraStudio.Services
{
    /// <summary>
    /// Win32 ListView (koji koristimo za JAWS-pristupačne liste kroz
    /// WindowsFormsHost) ignoriše BackColor/ForeColor na svom zaglavlju
    /// kolona kad je Windows temizacija (visual styles) uključena — zaglavlje
    /// ostaje belo bez obzira na temu aplikacije. Ovo isključuje temizaciju
    /// samo za zaglavlje te kontrole, čime ono prelazi na klasično (sivo,
    /// ne belo) iscrtavanje koje se mnogo bolje uklapa u tamnu temu.
    /// </summary>
    internal static class NativeTheme
    {
        private const int LVM_GETHEADER = 0x1000 + 31;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void DisableListViewHeaderTheme(WF.ListView listView)
        {
            if (!listView.IsHandleCreated) return;
            try
            {
                IntPtr headerHandle = SendMessage(listView.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (headerHandle != IntPtr.Zero)
                    SetWindowTheme(headerHandle, "", "");
            }
            catch
            {
                // Kozmetička sitnica — ako ovo ikad zapne (npr. buduća verzija
                // Windows-a promeni ponašanje), nema razloga da srušimo app.
            }
        }
    }
}
