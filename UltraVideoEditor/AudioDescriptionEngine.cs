using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>v1.4 (4.2) — one spoken description: when it should be heard and what it says.</summary>
    public class AdLine
    {
        /// <summary>Timeline time the described picture starts.</summary>
        public double Time;
        /// <summary>Until when the picture lasts (the description should fit before it).</summary>
        public double Until;
        public string Text = "";
        // filled while rendering
        public string AudioPath;
        public double Duration;
        public double PlacedAt;
        public bool OverSinging;
    }

    /// <summary>
    /// v1.4 (roadmap 4.2) — AUDIO DESCRIPTION FOR VIEWERS.
    ///
    /// From the picture descriptions of the clips (R4) the program writes a description script:
    /// what can be seen, shot by shot, with times. The editor can read and change every line.
    /// Each line is then spoken (Google voice online, or a Windows voice offline), placed in the
    /// pauses of the singing or speech (the subtitle times) where possible, and mixed over the
    /// finished video, with the music lowered a little under every description.
    ///
    /// Result, next to the finished video:
    ///   "&lt;name&gt; (audio description).mp4"  — the same picture with the described sound,
    ///   "&lt;name&gt; (audio description).txt"  — the descriptions as text, for the YouTube description,
    ///   "&lt;name&gt; (audio description).vtt"  — a WebVTT description track.
    /// So a blind child can follow the song's video too, not only its sound.
    /// </summary>
    public static class AudioDescriptionEngine
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ── 1. the script ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the description script from the timeline. Clips described by parts give one line
        /// per part, others one line; a clip without any description is described now (middle frame).
        /// Picture text (titles, captions) is read as well. <paramref name="progress"/> gets a short
        /// status text.
        /// </summary>
        public static async Task<List<AdLine>> BuildScriptAsync(IList<TimelineItem> items, Action<string> progress, CancellationToken ct)
        {
            var lines = new List<AdLine>();
            var visual = items.Where(i => i != null && (i.IsVideo || i.IsImage || (i.TrackIndex <= 1 && !i.IsAudio)))
                              .OrderBy(i => i.Start).ThenByDescending(i => i.TrackIndex).ToList();
            int n = 0;
            foreach (var it in visual)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                double end = it.Start + it.Duration;
                string overlay = it.TextOverlay?.Enabled == true && !string.IsNullOrWhiteSpace(it.TextOverlay.Text) ? it.TextOverlay.Text.Trim() : null;
                bool textCard = string.IsNullOrEmpty(it.Path) || !File.Exists(it.Path);
                if (textCard)
                {
                    // intro / outro / title cards: their text is the description
                    string t = overlay ?? TitleText(it.Name);
                    if (!string.IsNullOrWhiteSpace(t))
                        lines.Add(new AdLine { Time = it.Start, Until = end, Text = Speakable(t, 40) });
                    continue;
                }

                // v1.4: a longer video (a whole finished spot, a long recording) gets described part by
                // part at its shot changes first — one description for three minutes was useless
                if ((it.FrameDescriptions == null || it.FrameDescriptions.Count == 0) && it.IsVideo && it.Duration > 12)
                {
                    string nm = Path.GetFileNameWithoutExtension(it.Name ?? "");
                    var parts = await FrameDescriber.DescribeByPartsAsync(it, (d, t) => progress?.Invoke($"{nm}: {d}/{t}"), _ => { }, ct);
                    if (parts == null) throw new InvalidOperationException("no_model");
                    if (parts.Count > 0) it.FrameDescriptions = parts;   // remembered: Ctrl+Alt+D does not need to run again
                }

                if (it.FrameDescriptions != null && it.FrameDescriptions.Count > 0)
                {
                    foreach (var p in it.FrameDescriptions.OrderBy(p => p.Start))
                        lines.Add(new AdLine { Time = it.Start + p.Start, Until = Math.Min(end, it.Start + p.End), Text = Speakable(p.Text) });
                }
                else
                {
                    string d = UsableDescription(it.AudioDescription);
                    if (d == null)
                    {
                        progress?.Invoke($"{n}/{visual.Count}: {Path.GetFileNameWithoutExtension(it.Name ?? "")}");
                        double speed = it.VideoEffect?.Speed is double s && s > 0.05 ? s : 1.0;
                        var r = await FrameDescriber.DescribeFrameAsync(it.Path, it.IsImage ? 0 : it.SourceOffset + it.Duration / 2 * speed, ct);
                        if (r.Error == "no_model") throw new InvalidOperationException("no_model");
                        d = r.Text;
                        if (d != null) it.AudioDescription = d;   // remembered for next time
                    }
                    if (!string.IsNullOrWhiteSpace(d))
                        lines.Add(new AdLine { Time = it.Start, Until = end, Text = Speakable(d) });
                }
                if (overlay != null)
                    lines.Add(new AdLine { Time = it.Start + 0.1, Until = end, Text = Speakable(overlay, 40) });
            }

            // the same picture described twice in a row (one long shot cut into parts) is said once
            var result = new List<AdLine>();
            foreach (var l in lines.OrderBy(l => l.Time))
            {
                if (result.Count > 0 && string.Equals(result[^1].Text, l.Text, StringComparison.OrdinalIgnoreCase))
                { result[^1].Until = Math.Max(result[^1].Until, l.Until); continue; }
                result.Add(l);
            }
            return result;
        }

        private static string TitleText(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            int c = name.IndexOf(':');
            return c >= 0 && c < name.Length - 1 ? name.Substring(c + 1).Trim() : null;
        }

        private static string UsableDescription(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return null;
            if (d.Contains("context:") || d.Contains("mood:") || d.Contains("energy=") || d.Contains("static=")) return null;
            foreach (string bad in new[] { "Could not extract", "Error while describing", "Description not available", "File does not exist" })
                if (d.StartsWith(bad, StringComparison.OrdinalIgnoreCase)) return null;
            return d.Trim();
        }

        /// <summary>A description short enough to say between two lines of a song: the first sentence, at most ~22 words.</summary>
        public static string Speakable(string text, int maxWords = 22)
        {
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            // drop the shot size at the end ("Medium shot.") — useful for the editor, not for the viewer
            text = Regex.Replace(text, @"\s*(The shot is (a )?)?(close-up|medium shot|wide shot|krupni plan|srednji plan|široki plan|total)[^.]*\.?\s*$", "", RegexOptions.IgnoreCase).Trim();
            int dot = text.IndexOfAny(new[] { '.', '!', '?' });
            if (dot > 20) text = text.Substring(0, dot + 1);
            var words = text.Split(' ');
            if (words.Length > maxWords) text = string.Join(" ", words.Take(maxWords)).TrimEnd(',', ';') + ".";
            return text;
        }

        /// <summary>The script as editable text: "mm:ss.f | description", one line per description.</summary>
        public static string ToScript(IEnumerable<AdLine> lines) =>
            string.Join(Environment.NewLine, lines.Select(l => $"{FormatTime(l.Time)} | {l.Text}"));

        /// <summary>Reads the edited script back. Lines without a time are joined to the previous line.</summary>
        public static List<AdLine> FromScript(string script, IList<TimelineItem> items)
        {
            var lines = new List<AdLine>();
            foreach (string raw in (script ?? "").Replace("\r", "").Split('\n'))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                var m = Regex.Match(s, @"^(\d{1,3}):(\d{1,2}(?:[.,]\d+)?)\s*[|\-–]\s*(.*)$");
                if (m.Success)
                {
                    double t = int.Parse(m.Groups[1].Value, Inv) * 60 + double.Parse(m.Groups[2].Value.Replace(',', '.'), Inv);
                    if (m.Groups[3].Value.Trim().Length > 0)
                        lines.Add(new AdLine { Time = t, Text = m.Groups[3].Value.Trim() });
                }
                else if (lines.Count > 0) lines[^1].Text += " " + s;
            }
            lines = lines.OrderBy(l => l.Time).ToList();
            double total = items.Count == 0 ? 0 : items.Max(i => i.Start + i.Duration);
            for (int i = 0; i < lines.Count; i++)
            {
                // a line may use the time until the next description, or until its picture ends
                double next = i + 1 < lines.Count ? lines[i + 1].Time : total;
                var shot = TimelineMixPreview.VideoAt(items, lines[i].Time + 0.01);
                double shotEnd = shot != null ? shot.Start + shot.Duration : next;
                lines[i].Until = Math.Max(lines[i].Time + 1, Math.Min(next, Math.Max(shotEnd, lines[i].Time + 1)));
            }
            return lines;
        }

        public static string FormatTime(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"mm\:ss\.f", Inv);

        // ── 2. voices ─────────────────────────────────────────────────────────

        /// <summary>"google" for the online voice, otherwise the name of an installed Windows voice.</summary>
        public static async Task<List<string>> WindowsVoicesAsync()
        {
            var list = new List<string>();
            try
            {
                string outText = await RunPowerShellAsync(
                    "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    "$s.GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo.Name + '|' + $_.VoiceInfo.Culture.Name }; $s.Dispose()",
                    CancellationToken.None);
                foreach (string l in outText.Split('\n'))
                    if (l.Trim().Length > 0) list.Add(l.Trim());
            }
            catch (Exception ex) { AppLog.Error("Listing Windows voices", ex); }
            return list;
        }

        /// <summary>Speaks every line into its own file (MP3 for the Google voice, WAV for a Windows voice).</summary>
        public static async Task SpeakAllAsync(List<AdLine> lines, string voice, string lang, string dir, Action<string> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            if (string.IsNullOrEmpty(voice) || voice == "google")
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                for (int i = 0; i < lines.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke($"{i + 1}/{lines.Count}");
                    string path = Path.Combine(dir, $"ad_{i:D3}.mp3");
                    using var ms = new MemoryStream();
                    foreach (string chunk in Chunks(lines[i].Text, 190))
                    {
                        string url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(chunk)}&tl={lang}&client=tw-ob";
                        byte[] data = await http.GetByteArrayAsync(url, ct);
                        ms.Write(data, 0, data.Length);
                    }
                    await File.WriteAllBytesAsync(path, ms.ToArray(), ct);
                    lines[i].AudioPath = path;
                }
            }
            else
            {
                // one PowerShell run for all lines; texts go through a UTF-8 file, never the command line
                string listFile = Path.Combine(dir, "ad_lines.txt");
                await File.WriteAllLinesAsync(listFile, lines.Select(l => l.Text.Replace('\n', ' ')), new UTF8Encoding(false), ct);
                string voiceName = voice.Split('|')[0].Replace("'", "''");
                string script =
                    "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                    $"$s.SelectVoice('{voiceName}'); $s.Rate = 0; $i = 0; " +
                    $"Get-Content -Encoding UTF8 -LiteralPath '{listFile.Replace("'", "''")}' | ForEach-Object {{ " +
                    $"$f = Join-Path '{dir.Replace("'", "''")}' ('ad_{{0:D3}}.wav' -f $i); $s.SetOutputToWaveFile($f); $s.Speak($_); $i++ }}; " +
                    "$s.SetOutputToNull(); $s.Dispose()";
                progress?.Invoke(voice.Split('|')[0]);
                await RunPowerShellAsync(script, ct);
                for (int i = 0; i < lines.Count; i++)
                    lines[i].AudioPath = Path.Combine(dir, $"ad_{i:D3}.wav");
            }

            foreach (var l in lines)
                l.Duration = File.Exists(l.AudioPath) ? await DurationAsync(l.AudioPath, ct) : 0;
        }

        private static IEnumerable<string> Chunks(string text, int max)
        {
            var sb = new StringBuilder();
            foreach (string w in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0 && sb.Length + 1 + w.Length > max) { yield return sb.ToString(); sb.Clear(); }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        // ── 3. placing the descriptions in the pauses ─────────────────────────

        /// <summary>
        /// Puts every description where it does not cover singing or speech (the subtitle times),
        /// as early as possible after its picture starts and never over the previous description.
        /// When no pause is long enough before the picture ends, it is placed right after the
        /// previous one anyway (the music is lowered under it) and marked as over the singing.
        /// </summary>
        public static void Place(List<AdLine> lines, IEnumerable<(double Start, double End)> busy, double total)
        {
            var occupied = busy.Where(b => b.End > b.Start).OrderBy(b => b.Start).ToList();
            double prevEnd = 0;
            foreach (var l in lines.Where(l => l.Duration > 0.05).OrderBy(l => l.Time))
            {
                double earliest = Math.Max(l.Time, prevEnd + 0.25);
                double latestEnd = Math.Max(l.Until, earliest + l.Duration) + 1.0;
                double t = earliest;
                bool fits = false;
                for (int guard = 0; guard < 200; guard++)
                {
                    var hit = occupied.FirstOrDefault(o => o.Start < t + l.Duration && o.End > t);
                    if (hit == default) { fits = t + l.Duration <= latestEnd; break; }
                    t = hit.End + 0.15;
                    if (t + l.Duration > latestEnd) break;
                }
                if (!fits) { t = earliest; l.OverSinging = occupied.Any(o => o.Start < t + l.Duration && o.End > t); }
                if (total > 0 && t + l.Duration > total) t = Math.Max(earliest, total - l.Duration);
                l.PlacedAt = t;
                prevEnd = t + l.Duration;
            }
        }

        // ── 4. the finished files ─────────────────────────────────────────────

        public enum Ducking { Light, Medium, Strong }

        /// <summary>Mixes the placed descriptions over the finished video and writes the .mp4, .txt and .vtt.</summary>
        public static async Task<string> RenderAsync(List<AdLine> lines, string videoPath, string title, Ducking duck, string workDir, CancellationToken ct)
        {
            var spoken = lines.Where(l => l.Duration > 0.05 && File.Exists(l.AudioPath)).OrderBy(l => l.PlacedAt).ToList();
            if (spoken.Count == 0) throw new InvalidOperationException("No description could be spoken.");
            double total = await DurationAsync(videoPath, ct);
            if (total < 0.5) throw new InvalidOperationException("The video could not be read.");

            // a) the description track alone (silent bed + every line at its place), in passes of 32
            string narration = Path.Combine(workDir, "ad_track.wav");
            await MixTrackAsync(spoken, total, narration, workDir, ct);

            // b) the video with the description: the program sound is lowered under the voice
            string ratio = duck switch { Ducking.Light => "1.6", Ducking.Strong => "4", _ => "2.2" };
            string baseName = Path.Combine(Path.GetDirectoryName(videoPath) ?? "", Path.GetFileNameWithoutExtension(videoPath) + " (audio description)");
            string outMp4 = baseName + ".mp4";
            string graph =
                "[0:a]aformat=sample_rates=48000:channel_layouts=stereo[p];" +
                "[1:a]aformat=sample_rates=48000:channel_layouts=stereo,asplit=2[sc][nv];" +
                $"[p][sc]sidechaincompress=threshold=0.015:ratio={ratio}:attack=30:release=500:makeup=1[duck];" +
                "[nv]volume=1.5[nv2];" +
                "[duck][nv2]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[aout]";
            string args = $"-nostdin -y -i \"{videoPath}\" -i \"{narration}\" -filter_complex \"{graph}\" " +
                          $"-map 0:v -map \"[aout]\" -c:v copy -c:a aac -b:a 256k -ar 48000 " +
                          $"-metadata:s:a:0 title=\"Audio description\" -movflags +faststart \"{outMp4}\"";
            if (!await ExportHelpers.RunFfmpegAsync(args, ct) || !File.Exists(outMp4))
                throw new InvalidOperationException("FFmpeg could not mix the description into the video.");

            // c) text for the YouTube description, and a WebVTT description track
            var txt = new StringBuilder();
            txt.AppendLine($"{title} — audio description");
            txt.AppendLine();
            foreach (var l in spoken) txt.AppendLine($"{TimeSpan.FromSeconds(l.Time):m\\:ss} {l.Text}");
            await File.WriteAllTextAsync(baseName + ".txt", txt.ToString(), new UTF8Encoding(false), ct);

            var vtt = new StringBuilder("WEBVTT\n\n");
            int n = 1;
            foreach (var l in spoken)
            {
                vtt.AppendLine((n++).ToString(Inv));
                vtt.AppendLine($"{Vtt(l.PlacedAt)} --> {Vtt(l.PlacedAt + Math.Max(1, l.Duration))}");
                vtt.AppendLine(l.Text);
                vtt.AppendLine();
            }
            await File.WriteAllTextAsync(baseName + ".vtt", vtt.ToString(), new UTF8Encoding(false), ct);
            return outMp4;
        }

        private static string Vtt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\.fff", Inv);

        private static async Task MixTrackAsync(List<AdLine> spoken, double total, string outPath, string workDir, CancellationToken ct)
        {
            var current = spoken.Select(l => (Path: l.AudioPath, At: l.PlacedAt)).ToList();
            var temps = new List<string>();
            try
            {
                while (true)
                {
                    bool last = current.Count <= 32;
                    var next = new List<(string Path, double At)>();
                    foreach (var group in current.Select((p, i) => (p, i)).GroupBy(x => x.i / 32))
                    {
                        string target = last ? outPath : Path.Combine(workDir, $"ad_part_{Guid.NewGuid():N}.wav");
                        if (!last) temps.Add(target);
                        var args = new StringBuilder($"-nostdin -y -f lavfi -t {total.ToString("0.###", Inv)} -i anullsrc=r=48000:cl=stereo ");
                        var g = new StringBuilder();
                        int k = 1;
                        foreach (var (p, _) in group)
                        {
                            args.Append($"-i \"{p.Path}\" ");
                            long ms = (long)Math.Round(p.At * 1000);
                            g.Append($"[{k}:a]aformat=sample_rates=48000:channel_layouts=stereo,adelay={ms}:all=1[a{k}];");
                            k++;
                        }
                        g.Append("[0:a]");
                        for (int j = 1; j < k; j++) g.Append($"[a{j}]");
                        g.Append($"amix=inputs={k}:duration=first:normalize=0[out]");
                        args.Append($"-filter_complex \"{g}\" -map \"[out]\" -c:a pcm_f32le \"{target}\"");
                        if (!await ExportHelpers.RunFfmpegAsync(args.ToString(), ct) || !File.Exists(target))
                            throw new InvalidOperationException("FFmpeg could not build the description track.");
                        next.Add((target, 0));
                    }
                    if (last) return;
                    current = next;
                }
            }
            finally
            {
                foreach (var t in temps) try { File.Delete(t); } catch { }
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────

        public static async Task<double> DurationAsync(string path, CancellationToken ct)
        {
            string probe = AppPaths.FFprobe;
            if (!File.Exists(probe)) return 0;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(probe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
            };
            p.Start();
            var o = p.StandardOutput.ReadToEndAsync();
            p.DrainStderr();
            await p.WaitForExitOrKillAsync(ct);
            return double.TryParse((await o).Trim(), NumberStyles.Float, Inv, out double d) ? d : 0;
        }

        private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
        {
            // -EncodedCommand: no quoting problems at all
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            p.Start();
            var o = p.StandardOutput.ReadToEndAsync();
            p.DrainStderr();
            await p.WaitForExitOrKillAsync(ct);
            return await o;
        }
    }
}
