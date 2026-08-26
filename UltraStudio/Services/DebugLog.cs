using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UltraStudio.Services
{
    /// <summary>
    /// Opšti dnevnik dijagnostike za CELU aplikaciju (ne samo SAM izdvajanje) —
    /// prati isti obrazac koji Video Editor već koristi (Ctrl+Shift+L prozor).
    /// Svaki red se piše ODMAH, pre rizičnog koraka koji sledi, ne tek na kraju
    /// operacije — ako nešto padne native (van domašaja try/catch) ili se
    /// fokus/tastatura ponašaju neobjašnjivo, poslednji redovi ovde pokazuju
    /// TAČNO šta se dešavalo neposredno pre toga.
    ///
    /// Uz upis na disk, drži se i mala kružna kopija u memoriji — LogWindow
    /// (Ctrl+Shift+L) je čita direktno, bez ponovnog otvaranja fajla, i JAWS-u
    /// je to običan čitljiv tekst, bez potrebe za live-region trikovima.
    /// </summary>
    internal static class DebugLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraStudio", "debug.log");

        private const int MaxInMemoryLines = 500;
        private static readonly List<string> _recent = new();
        private static readonly object _lock = new();

        public static string LogFilePath => LogPath;

        public static void Write(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

            lock (_lock)
            {
                _recent.Add(line);
                if (_recent.Count > MaxInMemoryLines)
                    _recent.RemoveAt(0);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logovanje nikad ne sme samo da postane novi izvor problema —
                // ako upis na disk ne uspe (npr. nema dozvole), tiho nastavi.
                // Redovi ostaju bar u memoriji (_recent) za LogWindow.
            }
        }

        /// <summary>Poslednjih do MaxInMemoryLines redova, najstariji prvi.</summary>
        public static string GetRecentText()
        {
            lock (_lock)
            {
                return _recent.Count > 0
                    ? string.Join(Environment.NewLine, _recent)
                    : "(još nema zapisa u ovoj sesiji)";
            }
        }
    }
}
