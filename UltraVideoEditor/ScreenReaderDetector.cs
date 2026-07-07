using System.Linq;
using System.Runtime.InteropServices;

namespace UltraVideoEditor
{
    /// <summary>
    /// Detects whether a screen reader (JAWS, NVDA, Windows Narrator, etc.) is
    /// currently running, so the app can automatically start in the right mode.
    ///
    /// No detection method is 100% reliable — this is "best available signal",
    /// not a guarantee. That's why the app always keeps a manual override
    /// (View > Display mode menu, Ctrl+Shift+A) regardless of what this returns.
    /// </summary>
    public static class ScreenReaderDetector
    {
        // SPI_GETSCREENREADER (0x0046): the standard Windows-wide flag that
        // JAWS, NVDA and Narrator all set when they start, specifically so
        // apps can detect them this way. WPF doesn't expose this directly
        // (there is no SystemParameters.IsScreenReaderRunning), so we call the
        // native API ourselves.
        private const uint SPI_GETSCREENREADER = 0x0046;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

        private static bool IsScreenReaderFlagSet()
        {
            bool result = false;
            try
            {
                if (SystemParametersInfo(SPI_GETSCREENREADER, 0, ref result, 0))
                    return result;
            }
            catch { }
            return false;
        }

        // Process name fragments for common Windows screen readers / magnifiers.
        // Checked as a Contains() match against the lowercase process name.
        private static readonly string[] KnownProcessNames =
        {
            "nvda",         // NVDA
            "jfw",          // JAWS (jfw.exe)
            "jhook",        // JAWS helper process
            "narrator",     // Windows Narrator
            "zoomtext",     // ZoomText
            "fusion",       // Fusion (JAWS + ZoomText combined)
            "dolphin",      // Dolphin / Supernova
            "supernova",
            "windoweyes",
            "guide",        // Guide (older reader)
        };

        public static bool IsScreenReaderActive()
        {
            // Step 1: Standard Windows-wide flag.
            if (IsScreenReaderFlagSet())
                return true;

            // Step 2: Fallback — look for known screen reader processes directly, in
            //    case the SPI flag wasn't set for some reason.
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName?.ToLowerInvariant() ?? "";
                        if (name == "sa" || KnownProcessNames.Any(p => name.Contains(p)))
                            return true;
                    }
                    catch { /* process may have exited mid-enumeration, or access denied */ }
                }
            }
            catch { /* ignore — enumeration itself can fail in locked-down environments */ }

            return false;
        }
    }
}
