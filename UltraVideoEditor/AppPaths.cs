using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 — one place for the external tools and the app's own folders.
    /// Before, "BaseDirectory\Ffmpeg\ffmpeg.exe" was written out in more than 40 places and only
    /// the video review window also looked in PATH and the usual install folders. Now every part
    /// of the program finds FFmpeg the same way.
    /// </summary>
    public static class AppPaths
    {
        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        private static string _ffmpeg;

        /// <summary>
        /// ffmpeg.exe: next to the program (Ffmpeg\, ffmpeg\, tools\ or the program folder),
        /// then PATH, then the usual install folders. When nothing is found, the expected
        /// location (Ffmpeg\ffmpeg.exe next to the program) is returned so error messages can
        /// say where to put it.
        /// </summary>
        public static string FFmpeg
        {
            get
            {
                if (_ffmpeg != null && File.Exists(_ffmpeg)) return _ffmpeg;
                _ffmpeg = FindTool("ffmpeg.exe");
                return _ffmpeg;
            }
        }

        /// <summary>ffprobe.exe, preferably from the same folder as ffmpeg.exe.</summary>
        public static string FFprobe
        {
            get
            {
                string sibling = Path.Combine(Path.GetDirectoryName(FFmpeg) ?? BaseDir, "ffprobe.exe");
                return File.Exists(sibling) ? sibling : FindTool("ffprobe.exe");
            }
        }

        public static bool FFmpegAvailable => File.Exists(FFmpeg);

        private static string FindTool(string exe)
        {
            var candidates = new List<string>
            {
                Path.Combine(BaseDir, "Ffmpeg", exe),
                Path.Combine(BaseDir, "ffmpeg", exe),
                Path.Combine(BaseDir, "tools", exe),
                Path.Combine(BaseDir, exe),
            };

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { candidates.Add(Path.Combine(dir.Trim(), exe)); } catch { /* invalid PATH entry */ }
            }

            candidates.Add(Path.Combine(@"C:\ffmpeg\bin", exe));
            candidates.Add(Path.Combine(@"C:\Program Files\ffmpeg\bin", exe));
            candidates.Add(Path.Combine(@"C:\Program Files (x86)\ffmpeg\bin", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", exe));

            foreach (string c in candidates)
                if (File.Exists(c)) return c;

            return Path.Combine(BaseDir, "Ffmpeg", exe);
        }

        /// <summary>
        /// v1.4 — FFmpeg builds for Windows (gyan.dev "full") use fontconfig for text (drawtext,
        /// subtitles), but ship without a fontconfig configuration. Every text step then printed
        /// "Fontconfig error: Cannot load default config file" and FFmpeg crashed (exit
        /// -1073741819), so titles, the outro text and subtitles failed. A small fonts.conf that
        /// points to the Windows font folders is written once and handed to every FFmpeg run
        /// through FONTCONFIG_FILE (child processes inherit the environment).
        /// </summary>
        public static void EnsureFontconfig()
        {
            try
            {
                string dir = Path.Combine(DataDir, "fontconfig");
                string cache = Path.Combine(dir, "cache");
                Directory.CreateDirectory(cache);
                string conf = Path.Combine(dir, "fonts.conf");

                static string X(string p) => System.Security.SecurityElement.Escape(p.Replace('\\', '/'));
                string systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                string userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
                string xml =
                    "<?xml version=\"1.0\"?>\n" +
                    "<!DOCTYPE fontconfig SYSTEM \"fonts.dtd\">\n" +
                    "<fontconfig>\n" +
                    $"  <dir>{X(systemFonts)}</dir>\n" +
                    $"  <dir>{X(userFonts)}</dir>\n" +
                    $"  <cachedir>{X(cache)}</cachedir>\n" +
                    "  <alias><family>Sans</family><prefer><family>Arial</family><family>Segoe UI</family></prefer></alias>\n" +
                    "  <alias><family>sans-serif</family><prefer><family>Arial</family><family>Segoe UI</family></prefer></alias>\n" +
                    "</fontconfig>\n";
                if (!File.Exists(conf) || File.ReadAllText(conf) != xml)
                    File.WriteAllText(conf, xml);

                Environment.SetEnvironmentVariable("FONTCONFIG_FILE", conf);
                Environment.SetEnvironmentVariable("FONTCONFIG_PATH", dir);
            }
            catch (Exception ex)
            {
                AppLog.Error("Preparing the font configuration for FFmpeg", ex);
            }
        }

        /// <summary>%LOCALAPPDATA%\UltraVideoEditor — settings, keys, logs, autosave.</summary>
        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraVideoEditor");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string LogsDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "Logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string AutosaveDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "Autosave");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
    }

    /// <summary>
    /// v1.4 — encrypted (Windows DPAPI, current user) storage for API keys and tokens that are
    /// not media providers (Cloudflare token and account, ...). Same file names as before
    /// ("&lt;name&gt;_key.bin" in %LOCALAPPDATA%\UltraVideoEditor), so saved keys keep working.
    /// Media provider keys (Pixabay, Pexels, Coverr) live in <see cref="MediaProviderSettings"/>.
    /// </summary>
    public static class KeyStore
    {
        private static string FileFor(string name) => Path.Combine(AppPaths.DataDir, $"{name}_key.bin");

        public static string Load(string name)
        {
            try
            {
                string file = FileFor(name);
                if (!File.Exists(file)) return null;
                byte[] dec = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
                string key = Encoding.UTF8.GetString(dec).Trim();
                return key.Length == 0 ? null : key;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Reading the saved key '{name}'", ex);
                return null;
            }
        }

        public static bool Save(string name, string key)
        {
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes((key ?? "").Trim()), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FileFor(name), enc);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Saving the key '{name}'", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// v1.4 — a log file for errors that used to be swallowed silently.
    /// Written to %LOCALAPPDATA%\UltraVideoEditor\Logs\ultra_yyyy-MM-dd.log, and also shown in the
    /// log window (without a screen reader announcement, so nothing interrupts the work).
    /// </summary>
    public static class AppLog
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, DateTime> _lastSeen = new();

        public static string TodayFile => Path.Combine(AppPaths.LogsDir, $"ultra_{DateTime.Now:yyyy-MM-dd}.log");

        public static void Error(string context, Exception ex) =>
            Write("ERROR", $"{context}: {ex?.GetType().Name}: {ex?.Message}", ex?.ToString());

        public static void Warn(string message) => Write("WARN", message, null);

        public static void Info(string message) => Write("INFO", message, null);

        private static void Write(string level, string message, string details)
        {
            // The same message within 30 seconds is written once (some of these happen in loops).
            lock (_lock)
            {
                if (_lastSeen.TryGetValue(message, out var last) && (DateTime.Now - last).TotalSeconds < 30)
                    return;
                _lastSeen[message] = DateTime.Now;
                if (_lastSeen.Count > 500) _lastSeen.Clear();
            }

            try
            {
                lock (_lock)
                {
                    var sb = new StringBuilder();
                    sb.Append($"[{DateTime.Now:HH:mm:ss}] {level} {message}").AppendLine();
                    if (!string.IsNullOrEmpty(details) && level == "ERROR")
                        sb.AppendLine(details);
                    File.AppendAllText(TodayFile, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { /* logging must never break the program */ }

            try
            {
                var app = System.Windows.Application.Current;
                app?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (app.MainWindow is MainWindow mw)
                        mw.LogMessage($"{level}: {message}", false);
                }));
            }
            catch { /* the window may already be closed */ }
        }
    }
}
