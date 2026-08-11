using System;
using System.IO;

namespace UltraCast.Services
{
    /// <summary>
    /// Centralized logging so failures are never just a system sound with
    /// no explanation. Every line goes to a log file AND raises an event
    /// the UI subscribes to, so the same information is available whether
    /// you're looking at the screen, listening with a screen reader, or
    /// reading the log file afterwards (e.g. when running from Visual
    /// Studio, where the installer never ran and dependencies like
    /// Ffmpeg\ffmpeg.exe simply aren't there yet).
    /// </summary>
    public static class AppLogger
    {
        public static string LogFilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraCast", "log.txt");

        public static event Action<string>? LineLogged;

        public static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            try
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // If we can't even write the log file, there's nothing more
                // useful we can do here - the UI event below still fires.
            }

            LineLogged?.Invoke(line);
        }
    }
}
