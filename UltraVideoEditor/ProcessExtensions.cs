using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// Pomoćne metode za spoljne procese (ffmpeg, whisper, yt-dlp).
    ///
    /// Zašto postoje: ako je izlaz procesa preusmeren (RedirectStandardError/Output = true),
    /// a niko ga ne čita, Windows pipe bafer (nekoliko KB) se napuni i proces STANE —
    /// zauvek čeka da neko pročita izlaz. ffmpeg stalno piše napredak u stderr, pa je
    /// konverzija dužeg videa (npr. izvlačenje zvuka za transkripciju, export u 9:16)
    /// mogla da "zamrzne" bez ikakve greške. DrainStdout/DrainStderr čitaju i odbacuju izlaz.
    /// </summary>
    internal static class ProcessExtensions
    {
        /// <summary>Čita i odbacuje standardni izlaz (poziva se odmah posle Start()).</summary>
        public static void DrainStdout(this Process p)
        {
            if (p == null || !p.StartInfo.RedirectStandardOutput) return;
            p.OutputDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
        }

        /// <summary>Čita i odbacuje standardni izlaz za greške (poziva se odmah posle Start()).</summary>
        public static void DrainStderr(this Process p)
        {
            if (p == null || !p.StartInfo.RedirectStandardError) return;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
        }

        /// <summary>Isto što i DrainStdout + DrainStderr.</summary>
        public static void DrainAll(this Process p)
        {
            p.DrainStdout();
            p.DrainStderr();
        }

        /// <summary>
        /// Čeka kraj procesa; ako se operacija otkaže, UBIJA proces (i njegove pod-procese)
        /// umesto da ga ostavi da radi u pozadini, pa baca OperationCanceledException.
        /// </summary>
        public static async Task WaitForExitOrKillAsync(this Process p, CancellationToken ct)
        {
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
        }
    }
}
