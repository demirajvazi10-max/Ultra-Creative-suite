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
        /// v1.4 — the script made from the FINISHED VIDEO itself, like audio description on Netflix:
        /// the video is cut at its real shot changes (FFmpeg scene detection), every shot gets one
        /// short sentence written for viewers, timed at the moment the shot appears. Titles and
        /// credits are read out. This does not depend on the timeline at all, so it is right even
        /// when the project's clip times differ from the rendered video (an older project, an
        /// imported finished spot). Very short shots (under 1.2 s) are joined to the next one;
        /// a shot longer than 12 s gets another description every ~10 s if the picture changed.
        /// </summary>
        public static async Task<List<AdLine>> BuildScriptFromVideoAsync(string videoPath, Action<string> progress, CancellationToken ct)
        {
            double total = await DurationAsync(videoPath, ct);
            if (total < 0.5) throw new InvalidOperationException("The video could not be read.");
            if (!await FrameDescriber.CanDescribeAsync()) throw new InvalidOperationException("no_model");

            progress?.Invoke("0%");
            var cuts = await FrameDescriber.FindShotChangesAsync(videoPath, 0, total, ct);
            var bounds = new List<double> { 0 };
            foreach (double c in cuts.OrderBy(c => c))
                if (c - bounds[^1] >= 1.2 && total - c >= 0.8) bounds.Add(c);   // too short a shot: joined to the next
            bounds.Add(total);

            // shots, with long ones split into ~8 s parts (Part > 0 = the same shot continues)
            var shots = new List<(double Start, double End, int Part)>();
            for (int i = 0; i + 1 < bounds.Count; i++)
            {
                double a = bounds[i], b = bounds[i + 1];
                int pieces = Math.Max(1, (int)Math.Floor((b - a) / 8.0));
                for (int k = 0; k < pieces; k++)
                    shots.Add((a + (b - a) * k / pieces, a + (b - a) * (k + 1) / pieces, k));
            }

            // v1.4.5: planned the way a describer works. The program knows roughly how long each
            // sentence takes to say, so it only describes a shot that is still on screen when the
            // previous description has finished. Before, every shot was described, the sentences
            // were cut to fit ("A girl walks through the forest with a."), and later the ones that
            // did not fit were dropped, which left long silent stretches.
            var lines = new List<AdLine>();
            double free = 0;          // when the previous description is expected to be over
            double lastSpoken = -100; // when the previous description started
            for (int i = 0; i < shots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (s, e, part) = shots[i];
                progress?.Invoke($"{i + 1}/{shots.Count}");

                // the shot is gone (or nearly) before the previous sentence ends: not described
                double start = Math.Max(s, free);
                if (start > e - 0.6) continue;

                // a moment after the cut (the new picture is settled, crossfades are over),
                // or the moment the description can start, if that is later
                double at = Math.Min(e - 0.1, Math.Max(start, s + Math.Min(0.8, (e - s) / 3)));
                var r = await FrameDescriber.DescribeFrameAsync(videoPath, at, forViewers: true, ct);
                if (r.Error == "no_model") throw new InvalidOperationException("no_model");
                if (string.IsNullOrWhiteSpace(r.Text) && e - s > 1.5)
                {
                    // one more try in the middle of the shot (a dark or blurred frame, a busy model)
                    r = await FrameDescriber.DescribeFrameAsync(videoPath, (s + e) / 2, forViewers: true, ct);
                    if (r.Error == "no_model") throw new InvalidOperationException("no_model");
                }
                if (string.IsNullOrWhiteSpace(r.Text))
                {
                    AppLog.Info($"Audio description: no description for the shot at {FormatTime(s)} ({r.Error} {r.Detail})");
                    continue;
                }
                string text = Speakable(r.Text);
                if (text.Length < 2) continue;

                // the same picture again: a continued shot is not described twice; a new shot that
                // looks the same is described again only after a longer quiet stretch
                if (lines.Count > 0 && Similar(lines[^1].Text, text) && (part > 0 || start - lastSpoken < 12))
                {
                    lines[^1].Until = e;
                    continue;
                }
                lines.Add(new AdLine { Time = Math.Round(start, 1), Until = Math.Max(e, start + SpeakSeconds(text)), Text = text });
                lastSpoken = start;
                free = start + SpeakSeconds(text) + 0.4;
            }
            return lines;
        }

        // ── 1b. the script from Gemini watching the whole video (v1.4.5) ─────

        /// <summary>
        /// v1.4.5 — the best mode: Gemini watches the WHOLE video, picture and sound together, the way
        /// a human describer works. It sees movement and what happens across shots, knows who is who,
        /// and hears the singing or the dialogue, so it writes the descriptions into the pauses.
        /// A smaller copy of the video (480p, mono sound) is uploaded to Google; long videos (films,
        /// series) are described in 10-minute parts, each part told what was said before, so the
        /// characters stay the same. The uploaded copy is deleted afterwards.
        /// <paramref name="say"/> gets the stage (spoken), <paramref name="progress"/> "done/total".
        /// </summary>
        /// <summary>How often the descriptions come (v1.4.5).</summary>
        public enum Density { Pauses, Normal, Dense }

        /// <summary>The longest stretch without a description that is filled in afterwards, per density.</summary>
        private static double MaxGap(Density d) => d switch { Density.Dense => 9, Density.Normal => 15, _ => double.MaxValue };

        public static async Task<List<AdLine>> BuildScriptWithGeminiAsync(string videoPath, Density density, Action<string> say, Action<string> progress, CancellationToken ct)
        {
            double total = await DurationAsync(videoPath, ct);
            if (total < 0.5) throw new InvalidOperationException("The video could not be read.");
            string lang = LanguageManager.CurrentLanguage ?? "en";
            var uiProgress = new Progress<string>(s => progress?.Invoke(s));   // the upload reports from another thread

            // a) a small copy: Gemini looks at about one frame a second anyway
            say?.Invoke("prepare");
            string small = Path.Combine(Path.GetTempPath(), $"uve_gemini_{Guid.NewGuid():N}.mp4");
            string fileName = null;
            try
            {
                string args = $"-nostdin -y -i \"{videoPath}\" -map 0:v:0 -map 0:a:0? -vf \"scale=-2:'min(480,ih)',fps=2\" " +
                              "-c:v libx264 -preset veryfast -crf 30 -c:a aac -ac 1 -b:a 48k -movflags +faststart " + $"\"{small}\"";
                if (!await ExportHelpers.RunFfmpegAsync(args, ct) || !File.Exists(small))
                    throw new InvalidOperationException("FFmpeg could not prepare the video for Gemini.");

                // b) upload
                say?.Invoke("upload");
                var (name, uri) = await GeminiClient.UploadVideoAsync(small, s => ((IProgress<string>)uiProgress).Report(s), ct);
                fileName = name;

                // c) ask part by part
                const double partLength = 600;
                int parts = Math.Max(1, (int)Math.Ceiling(total / partLength));
                bool low = total > 900;   // long videos: fewer tokens per frame
                var lines = new List<AdLine>();
                for (int i = 0; i < parts; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    double from = i * partLength, to = Math.Min(total, (i + 1) * partLength);
                    say?.Invoke(parts > 1 ? $"part|{i + 1}|{parts}" : "watch");
                    string before = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 12)).Select(l => $"{FormatClock(l.Time)} | {l.Text}"));
                    string answer = await GeminiClient.AskVideoAsync(uri, from, to, low, GeminiVideoPrompt(lang, total, from, to, before, density), ct);
                    var partLines = ParseGeminiScript(answer, from, to);

                    // v1.4.5: long stretches without any description (a song sung all the way through)
                    // are asked for once more, so the picture is never left undescribed for long
                    var gaps = FindGaps(partLines, from, to, MaxGap(density));
                    if (gaps.Count > 0)
                    {
                        string fill = await GeminiClient.AskVideoAsync(uri, from, to, low,
                            GeminiGapPrompt(lang, total, from, to, partLines, gaps, density), ct);
                        foreach (var l in ParseGeminiScript(fill, from, to))
                            if (gaps.Any(g => l.Time >= g.From - 0.5 && l.Time <= g.To + 0.5)) partLines.Add(l);
                        partLines = partLines.OrderBy(l => l.Time).ToList();
                    }
                    foreach (var l in partLines)
                        if (lines.Count == 0 || l.Time > lines[^1].Time + 1.5) lines.Add(l);
                }
                for (int i = 0; i < lines.Count; i++)
                    lines[i].Until = i + 1 < lines.Count ? lines[i + 1].Time : total;
                return lines;
            }
            finally
            {
                try { File.Delete(small); } catch { }
                if (fileName != null) await GeminiClient.DeleteFileAsync(fileName);
            }
        }

        /// <summary>mm:ss with minutes going past 59 (95:20), as the script uses.</summary>
        private static string FormatClock(double s) => $"{(int)(s / 60)}:{(int)(s % 60):00}";

        /// <summary>Stretches longer than <paramref name="maxGap"/> seconds with no description (from a line's estimated end to the next line).</summary>
        private static List<(double From, double To)> FindGaps(List<AdLine> lines, double from, double to, double maxGap)
        {
            var gaps = new List<(double, double)>();
            if (double.IsInfinity(maxGap) || maxGap == double.MaxValue) return gaps;
            double cursor = from;
            foreach (var l in lines.OrderBy(l => l.Time))
            {
                if (l.Time - cursor > maxGap) gaps.Add((cursor, l.Time));
                cursor = Math.Max(cursor, l.Time + SpeakSeconds(l.Text));
            }
            if (to - cursor > maxGap) gaps.Add((cursor, to));
            return gaps;
        }

        private static string DensityRule(Density d) => d switch
        {
            Density.Pauses =>
                "- Put each description where nobody is speaking or singing, so it does not cover dialogue or lyrics. Start it at the beginning of that pause.",
            Density.Dense =>
                "- Describe every visible change (new place, new person, new action), about every 5 to 8 seconds. Prefer pauses in speech or singing; " +
                "in a song you may speak over the singing (the music is lowered under the description), but never over dialogue in a film or series.",
            _ =>
                "- Prefer pauses where nobody is speaking or singing. In a film or series never cover dialogue. In a song that is sung almost " +
                "all the way through, also describe over the singing (the music is lowered under the description), at every important change of " +
                "picture, so that the picture is never left undescribed for more than about 12 seconds."
        };

        private static string GeminiGapPrompt(string lang, double total, double from, double to, List<AdLine> have,
                                              List<(double From, double To)> gaps, Density density)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a professional audio describer for blind viewers. Watch this video WITH its sound.");
            sb.AppendLine($"An audio description for the part from {FormatClock(from)} to {FormatClock(to)} already exists:");
            foreach (var l in have) sb.AppendLine($"{FormatClock(l.Time)} | {l.Text}");
            sb.AppendLine("These stretches still have NO description, although the picture goes on:");
            foreach (var g in gaps) sb.AppendLine($"{FormatClock(g.From)} – {FormatClock(g.To)}");
            sb.AppendLine("Write the missing descriptions, ONLY inside those stretches, describing what is seen there that has not been described yet.");
            sb.AppendLine(DensityRule(density == Density.Pauses ? Density.Normal : density));
            sb.AppendLine("- Short, complete sentences in the present tense, about 2.5 words per second of the time available. No camera words. Do not repeat the existing descriptions. Keep the same names.");
            sb.AppendLine($"- Write in {GeminiClient.LanguageName(lang)}.");
            sb.AppendLine("Times are measured from the START OF THE WHOLE VIDEO, in minutes:seconds.");
            sb.AppendLine("Answer ONLY with the new lines, in this exact form, nothing else:");
            sb.AppendLine("MM:SS | description");
            return sb.ToString();
        }

        private static string GeminiVideoPrompt(string lang, double total, double from, double to, string before, Density density)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a professional audio describer (like the audio description on Netflix) for blind and visually impaired viewers.");
            sb.AppendLine($"Watch this video WITH its sound. The whole video is {FormatClock(total)} long; describe only the part from {FormatClock(from)} to {FormatClock(to)}.");
            sb.AppendLine("Rules:");
            sb.AppendLine("- Describe what a blind viewer needs to follow it: who is there, what they do, where, important changes, facial expressions and gestures that matter, and any text on screen (titles, signs, credits) — read that text out.");
            sb.AppendLine(DensityRule(density));
            sb.AppendLine("- Each description must fit the time until the next one: about 2.5 words per second. Short, complete sentences in the present tense. Never cut a sentence.");
            sb.AppendLine("- Do not describe what can be heard (the music, what is said). Do not interpret or guess feelings that are not shown.");
            sb.AppendLine("- No camera words (shot, close-up, frame, camera). Do not repeat a description that is still true; describe only changes.");
            sb.AppendLine("- Once a character's name is clear from the dialogue or on screen, use it; until then use a short consistent description (\"the girl in the red coat\").");
            sb.AppendLine("- If the music plays without singing for a long time, describe more.");
            sb.AppendLine($"- Write in {GeminiClient.LanguageName(lang)}.");
            sb.AppendLine("Times are measured from the START OF THE WHOLE VIDEO, in minutes:seconds (minutes may go past 59, for example 95:20).");
            if (!string.IsNullOrWhiteSpace(before))
            {
                sb.AppendLine("The descriptions just before this part were (keep the same names, do not repeat them):");
                sb.AppendLine(before);
            }
            sb.AppendLine("Answer ONLY with lines in this exact form, one description per line, nothing else:");
            sb.AppendLine("MM:SS | description");
            return sb.ToString();
        }

        /// <summary>Reads Gemini's "MM:SS | text" lines (also H:MM:SS), keeping only times inside the part.</summary>
        private static List<AdLine> ParseGeminiScript(string answer, double from, double to)
        {
            var result = new List<AdLine>();
            foreach (string raw in (answer ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(raw.Replace("*", "").Trim().TrimStart('-', ' '), @"^\[?(\d{1,3}):(\d{1,2})(?::(\d{1,2}))?(?:[.,]\d+)?\]?\s*[|\-–:]\s*(.+)$");
                if (!m.Success) continue;
                double t = m.Groups[3].Success
                    ? int.Parse(m.Groups[1].Value, Inv) * 3600 + int.Parse(m.Groups[2].Value, Inv) * 60 + int.Parse(m.Groups[3].Value, Inv)
                    : int.Parse(m.Groups[1].Value, Inv) * 60 + int.Parse(m.Groups[2].Value, Inv);
                string text = m.Groups[4].Value.Trim().Trim('"');
                if (text.Length < 2) continue;
                if ((LanguageManager.CurrentLanguage ?? "en") == "sr") text = SerbianLatin(text);
                result.Add(new AdLine { Time = t, Text = text });
            }
            // times counted from the part instead of the whole video: shift them
            if (from > 0 && result.Count > 0 && result.All(l => l.Time < to - from + 1) && result.Any(l => l.Time < from - 1))
                foreach (var l in result) l.Time += from;
            return result.Where(l => l.Time >= from - 1 && l.Time <= to + 1).OrderBy(l => l.Time).ToList();
        }

        /// <summary>Serbian answers sometimes come in Cyrillic: the script is in Latin letters.</summary>
        private static string SerbianLatin(string s)
        {
            if (!s.Any(c => c >= 'Ѐ' && c <= 'ӿ')) return s;
            var map = new Dictionary<char, string>
            {
                ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['ђ']="đ",['е']="e",['ж']="ž",['з']="z",['и']="i",
                ['ј']="j",['к']="k",['л']="l",['љ']="lj",['м']="m",['н']="n",['њ']="nj",['о']="o",['п']="p",['р']="r",
                ['с']="s",['т']="t",['ћ']="ć",['у']="u",['ф']="f",['х']="h",['ц']="c",['ч']="č",['џ']="dž",['ш']="š"
            };
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                char lower = char.ToLowerInvariant(c);
                if (map.TryGetValue(lower, out string lat)) sb.Append(c == lower ? lat : char.ToUpperInvariant(lat[0]) + lat.Substring(1));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>About how long a sentence takes to say (Serbian and German words are longer).</summary>
        public static double SpeakSeconds(string text)
        {
            int words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            double perSecond = (LanguageManager.CurrentLanguage ?? "en") == "en" ? 2.7 : 2.3;
            return 0.4 + words / perSecond;
        }

        /// <summary>Two descriptions saying nearly the same thing (most of the words shared).</summary>
        private static bool Similar(string a, string b)
        {
            var wa = Regex.Split((a ?? "").ToLowerInvariant(), @"\W+").Where(w => w.Length > 3).ToHashSet();
            var wb = Regex.Split((b ?? "").ToLowerInvariant(), @"\W+").Where(w => w.Length > 3).ToHashSet();
            if (wa.Count == 0 || wb.Count == 0) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return wa.Intersect(wb).Count() >= 0.7 * Math.Min(wa.Count, wb.Count);
        }

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

        /// <summary>
        /// A description short enough to say between two lines of a song: the first whole sentence.
        /// v1.4.5: a sentence is never cut in the middle any more. A very long one is shortened only
        /// at a comma, so what is heard is always a complete thought.
        /// </summary>
        public static string Speakable(string text, int maxWords = 22)
        {
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim().Trim('"', '“', '”', '„');
            // drop the shot size at the end ("Medium shot.") — useful for the editor, not for the viewer
            text = Regex.Replace(text, @"\s*(The shot is (a )?)?(close-up|medium shot|wide shot|krupni plan|srednji plan|široki plan|total)[^.]*\.?\s*$", "", RegexOptions.IgnoreCase).Trim();
            // the first sentence (a dot inside a number or an abbreviation is not a sentence end)
            var end = Regex.Match(text, @"[.!?…](?=\s+\p{Lu}|\s*$)");
            if (end.Success && end.Index > 10) text = text.Substring(0, end.Index + 1);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > maxWords)
            {
                // shorten at the last comma before the limit; with no comma the whole sentence stays
                int cut = -1, count = 0;
                for (int i = 0; i < text.Length && count <= maxWords; i++)
                {
                    if (text[i] == ' ') count++;
                    if (text[i] == ',' && count >= 5) cut = i;
                }
                if (cut > 0) text = text.Substring(0, cut);
            }
            text = text.TrimEnd(',', ';', ':', ' ', '-', '–');
            if (text.Length > 0 && !".!?…".Contains(text[^1])) text += ".";
            return text;
        }

        /// <summary>The script as editable text: "mm:ss.f | description", one line per description.</summary>
        public static string ToScript(IEnumerable<AdLine> lines) =>
            string.Join(Environment.NewLine, lines.Select(l => $"{FormatTime(l.Time)} | {l.Text}"));

        /// <summary>Reads the edited script back. Lines without a time are joined to the previous line.</summary>
        public static List<AdLine> FromScript(string script, double total)
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
            for (int i = 0; i < lines.Count; i++)
            {
                // each description belongs to its shot: until the next description starts
                double next = i + 1 < lines.Count ? lines[i + 1].Time : Math.Max(total, lines[i].Time + 1);
                lines[i].Until = Math.Max(lines[i].Time + 0.5, next);
            }
            return lines;
        }

        // v1.4.5: minutes go past 59 (a film: "95:20.0"); before, an hour was lost ("35:20.0")
        public static string FormatTime(double s)
        {
            s = Math.Max(0, s);
            int min = (int)(s / 60);
            return $"{min:00}:{(s - min * 60).ToString("00.0", Inv)}";
        }

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
            if (voice != null && (voice.StartsWith("ms:") || voice.StartsWith("gc:")))
            {
                // v1.4.5: natural voices (Microsoft neural or Google Cloud Chirp 3 HD), one line at a time;
                // a line the service refuses is spoken with the plain Google voice instead
                string name = voice.Substring(3);
                string key = voice.StartsWith("gc:") ? NeuralTts.GoogleKey : null;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                int fallbacks = 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke($"{i + 1}/{lines.Count}");
                    string path = Path.Combine(dir, $"ad_{i:D3}.mp3");
                    string say = lang == "sr" ? NeuralTts.SerbianToCyrillic(lines[i].Text) : lines[i].Text;
                    byte[] data;
                    try
                    {
                        data = voice.StartsWith("ms:") ? await NeuralTts.MicrosoftAsync(say, name, ct)
                                                       : await NeuralTts.GoogleAsync(say, name, key, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        if (fallbacks++ == 0) AppLog.Error($"Natural voice '{name}' failed, using the plain Google voice", ex);
                        data = await PlainGoogleAsync(http, lines[i].Text, lang, ct);
                    }
                    await File.WriteAllBytesAsync(path, data, ct);
                    lines[i].AudioPath = path;
                }
            }
            else if (string.IsNullOrEmpty(voice) || voice == "google")
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

        /// <summary>The plain (free) Google Translate voice, used as the fallback.</summary>
        private static async Task<byte[]> PlainGoogleAsync(HttpClient http, string text, string lang, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            foreach (string chunk in Chunks(text, 190))
            {
                string url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={Uri.EscapeDataString(chunk)}&tl={lang}&client=tw-ob";
                byte[] data = await http.GetByteArrayAsync(url, ct);
                ms.Write(data, 0, data.Length);
            }
            return ms.ToArray();
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
        public static void Place(List<AdLine> lines, double total)
        {
            // v1.4: like audio description on Netflix — a description is heard the moment its shot
            // appears (the music is lowered under it). If the previous description is still being
            // spoken it waits; if its shot is already over by then, it is left out instead of
            // describing a picture that is no longer there (OverSinging = skipped).
            // v1.4.5: a description that comes a little late is still spoken (up to 2 seconds late);
            // only one that would start long after its moment, when the next one is already due,
            // is left out. Before, every late one was dropped, which left long silent stretches.
            double prevEnd = 0;
            foreach (var l in lines.OrderBy(l => l.Time))
            {
                if (l.Duration <= 0.05) continue;
                double t = Math.Max(l.Time, prevEnd + 0.2);
                if (t > l.Time + 2.0 && t >= l.Until - 0.3) { l.OverSinging = true; l.Duration = 0; continue; }
                if (total > 0 && t + l.Duration > total) t = Math.Max(l.Time, total - l.Duration);
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
            foreach (var l in spoken) txt.AppendLine($"{FormatClock(l.Time)} {l.Text}");
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
