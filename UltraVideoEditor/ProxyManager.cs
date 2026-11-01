using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — preview copies ("proxies"). Large clips (taller than 1080 lines, e.g. 4K) get a
    /// small 540p copy that the player uses while editing, so playback, scrubbing and J/K/L stay
    /// smooth on a laptop. The render always uses the original files. The copies keep the
    /// full length and timing of the original, so in-points and cuts are the same.
    /// </summary>
    public static class ProxyManager
    {
        public static string Folder => Path.Combine(AppPaths.DataDir, "Proxies");
        private static string CfgPath => Path.Combine(AppPaths.DataDir, "proxies.cfg");

        private static bool? _enabled;

        /// <summary>The player uses the preview copies (remembered).</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabled == null)
                {
                    try { _enabled = File.Exists(CfgPath) && File.ReadAllText(CfgPath).Trim() == "on"; }
                    catch { _enabled = false; }
                }
                return _enabled == true;
            }
            set
            {
                _enabled = value;
                try { Directory.CreateDirectory(AppPaths.DataDir); File.WriteAllText(CfgPath, value ? "on" : "off"); }
                catch (Exception ex) { AppLog.Error("Saving the proxy setting", ex); }
            }
        }

        /// <summary>Where the preview copy of a file is (or would be).</summary>
        public static string ProxyPathOf(string original)
        {
            long size = 0, stamp = 0;
            try { var fi = new FileInfo(original); size = fi.Length; stamp = fi.LastWriteTimeUtc.Ticks; } catch { }
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{Path.GetFullPath(original).ToLowerInvariant()}|{size}|{stamp}"));
            return Path.Combine(Folder, Path.GetFileNameWithoutExtension(original) + "_" + BitConverter.ToString(hash, 0, 6).Replace("-", "") + "_proxy.mp4");
        }

        /// <summary>The file the player should open: the preview copy when it exists and proxies are on.</summary>
        public static string PathFor(string original)
        {
            if (!Enabled || string.IsNullOrEmpty(original)) return original;
            try
            {
                string p = ProxyPathOf(original);
                return File.Exists(p) ? p : original;
            }
            catch { return original; }
        }

        /// <summary>
        /// Makes preview copies of the video clips that are taller than <paramref name="minHeight"/>.
        /// Returns (made, alreadyThere, failed).
        /// </summary>
        public static async Task<(int made, int existed, int failed)> CreateAsync(IEnumerable<TimelineItem> items, IProgress<string> progress,
                                                                                    CancellationToken ct, int minHeight = 1081)
        {
            Directory.CreateDirectory(Folder);
            var files = items.Where(i => (i.Type == "Video" || i.IsVideo) && i.Type != "Audio" && !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path))
                             .Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int made = 0, existed = 0, failed = 0, n = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                string proxy = ProxyPathOf(f);
                if (File.Exists(proxy)) { existed++; continue; }
                int h = await ProbeHeightAsync(f, ct);
                if (h > 0 && h < minHeight) continue;
                progress?.Report($"{n}/{files.Count}|{Path.GetFileName(f)}");
                string args = $"-nostdin -y -i \"{f}\" -vf \"scale=-2:540\" -c:v libx264 -preset ultrafast -crf 28 -g 15 -pix_fmt yuv420p -c:a aac -b:a 128k \"{proxy}\"";
                if (await ExportHelpers.RunFfmpegAsync(args, ct) && File.Exists(proxy) && new FileInfo(proxy).Length > 1000) made++;
                else { failed++; try { if (File.Exists(proxy)) File.Delete(proxy); } catch { } }
            }
            return (made, existed, failed);
        }

        private static async Task<int> ProbeHeightAsync(string path, CancellationToken ct)
        {
            try
            {
                using var p = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo(AppPaths.FFprobe,
                        $"-v error -select_streams v:0 -show_entries stream=height -of csv=p=0 \"{path}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
                };
                p.Start();
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                await e;
                return int.TryParse((await o).Trim().Split('\n')[0].Trim(), out int h) ? h : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }

        /// <summary>Deletes all preview copies; returns how much space was freed (bytes).</summary>
        public static long DeleteAll()
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                foreach (var f in Directory.GetFiles(Folder, "*_proxy.mp4"))
                {
                    try { freed += new FileInfo(f).Length; File.Delete(f); } catch { }
                }
            }
            catch (Exception ex) { AppLog.Error("Deleting the preview copies", ex); }
            return freed;
        }
    }
}
