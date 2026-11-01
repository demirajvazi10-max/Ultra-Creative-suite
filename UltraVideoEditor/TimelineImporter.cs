using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — one clip read from another editor's timeline.</summary>
    public class ImportedClip
    {
        public string Name = "";
        public string Path = "";
        /// <summary>Seconds on the timeline, and from the start of the file.</summary>
        public double Start, Duration, SourceOffset;
        /// <summary>1-based number of the video or audio track it was on (after empty tracks are dropped).</summary>
        public int TrackNumber = 1;
        public bool IsAudio;
        /// <summary>A picture clip whose own sound was on the timeline too (linked sound).</summary>
        public bool WithSound;
        public bool Missing;
    }

    /// <summary>v2.0 — what was read, and what had to be left out.</summary>
    public class ImportedTimeline
    {
        public string Name = "";
        public double Fps = 30;
        public List<ImportedClip> Clips = new List<ImportedClip>();
        public int Skipped;          // disabled clips, nested sequences, generators, clips without a file
        public int Transitions;      // transitions are not brought over (the clips are)
        public List<string> MissingFiles = new List<string>();
        public int VideoTracks => Clips.Where(c => !c.IsAudio).Select(c => c.TrackNumber).DefaultIfEmpty(0).Max();
        public int AudioTracks => Clips.Where(c => c.IsAudio).Select(c => c.TrackNumber).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// v2.0 — reads timelines made in other editors, so a cut started in Premiere Pro, DaVinci
    /// Resolve or Final Cut can be finished here:
    ///  • Final Cut Pro 7 XML (.xml, xmeml) — Premiere, Resolve, Final Cut 7, Avid (via tools);
    ///  • OpenTimelineIO (.otio) — Resolve, Premiere (plug-in), Kdenlive, many others;
    ///  • CMX 3600 EDL (.edl) — Video 1 and the audio channels.
    /// The sound of a picture clip (the same file at the same place on an audio track) is
    /// joined back to it, as this editor keeps a video clip's sound with the clip.
    /// </summary>
    public static class TimelineImporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static ImportedTimeline Read(string file)
        {
            string text = File.ReadAllText(file);
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)) ?? "";
            ImportedTimeline t;
            string head = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (ext == ".otio" || head.StartsWith("{")) t = ReadOtio(text);
            else if (ext == ".edl" || head.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)) t = ReadEdl(text);
            else t = ReadXml(text);
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = System.IO.Path.GetFileNameWithoutExtension(file);
            Finish(t, folder);
            return t;
        }

        // ── Final Cut Pro 7 XML ──────────────────────────────────────────────────────

        public static ImportedTimeline ReadXml(string text)
        {
            XDocument doc;
            using (var r = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
                doc = XDocument.Load(r);
            var seq = doc.Root?.Element("sequence") ?? doc.Descendants("sequence").FirstOrDefault()
                      ?? throw new InvalidDataException("No sequence in this XML file.");
            var t = new ImportedTimeline { Name = (string)seq.Element("name") ?? "" };
            double seqFrame = Frame(seq.Element("rate"), 1.0 / 30);
            t.Fps = 1 / seqFrame;

            // files are written in full once and then referred to by id
            var files = new Dictionary<string, string>();
            foreach (var f in seq.Descendants("file"))
            {
                string id = (string)f.Attribute("id");
                string url = (string)f.Element("pathurl");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(url) && !files.ContainsKey(id)) files[id] = PathFromUrl(url);
            }

            var media = seq.Element("media");
            foreach (var (kind, audio) in new[] { ("video", false), ("audio", true) })
            {
                int n = 0;
                foreach (var track in media?.Element(kind)?.Elements("track") ?? Enumerable.Empty<XElement>())
                {
                    n++;
                    if (((string)track.Element("enabled") ?? "TRUE").Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { t.Skipped += track.Elements("clipitem").Count(); continue; }
                    long cursor = 0;
                    foreach (var el in track.Elements())
                    {
                        if (el.Name == "transitionitem") { if (!audio) t.Transitions++; continue; }
                        if (el.Name != "clipitem") continue;
                        if (((string)el.Element("enabled") ?? "TRUE").Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { t.Skipped++; continue; }
                        var fileEl = el.Element("file");
                        string fid = (string)fileEl?.Attribute("id");
                        string path = fid != null && files.TryGetValue(fid, out var p) ? p : null;
                        if (path == null && fileEl?.Element("pathurl") != null) path = PathFromUrl((string)fileEl.Element("pathurl"));
                        if (string.IsNullOrEmpty(path)) { t.Skipped++; continue; }   // nested sequence, generator, title…

                        double clipFrame = Frame(el.Element("rate"), seqFrame);
                        long inF = Long(el, "in", 0), outF = Long(el, "out", -1);
                        long start = Long(el, "start", -1), end = Long(el, "end", -1);
                        if (start < 0) start = cursor;   // -1: the clip begins inside a transition
                        double dur = outF > inF ? (outF - inF) * clipFrame : end > start ? (end - start) * seqFrame : 0;
                        if (dur <= 0.001) { t.Skipped++; continue; }
                        cursor = end >= 0 ? end : start + (long)Math.Round(dur / seqFrame);
                        t.Clips.Add(new ImportedClip
                        {
                            Name = (string)el.Element("name") ?? System.IO.Path.GetFileName(path),
                            Path = path, Start = start * seqFrame, Duration = dur, SourceOffset = inF * clipFrame,
                            TrackNumber = n, IsAudio = audio
                        });
                    }
                }
            }
            return t;
        }

        private static double Frame(XElement rate, double fallback)
        {
            if (rate == null) return fallback;
            if (!double.TryParse((string)rate.Element("timebase"), NumberStyles.Float, Inv, out double tb) || tb <= 0) return fallback;
            bool ntsc = ((string)rate.Element("ntsc") ?? "").Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            return ntsc ? 1001.0 / (tb * 1000) : 1.0 / tb;
        }

        private static long Long(XElement el, string name, long fallback) =>
            long.TryParse((string)el.Element(name), NumberStyles.Integer, Inv, out long v) ? v : fallback;

        // ── OpenTimelineIO ───────────────────────────────────────────────────────────

        public static ImportedTimeline ReadOtio(string text)
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var t = new ImportedTimeline { Name = Str(root, "name") };
            if (!root.TryGetProperty("tracks", out var stack) || !stack.TryGetProperty("children", out var tracks))
                throw new InvalidDataException("No tracks in this OTIO file.");
            int nv = 0, na = 0;
            double rate = 0;
            foreach (var track in tracks.EnumerateArray())
            {
                if (!Schema(track).StartsWith("Track", StringComparison.Ordinal)) { t.Skipped++; continue; }
                bool audio = Str(track, "kind").Equals("Audio", StringComparison.OrdinalIgnoreCase);
                int n = audio ? ++na : ++nv;
                if (track.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                double cursor = 0;
                if (!track.TryGetProperty("children", out var items)) continue;
                foreach (var it in items.EnumerateArray())
                {
                    string schema = Schema(it);
                    if (schema.StartsWith("Transition", StringComparison.Ordinal)) { if (!audio) t.Transitions++; continue; }
                    var (off, dur, r) = Range(it);
                    if (r > 0 && rate == 0) rate = r;
                    if (schema.StartsWith("Gap", StringComparison.Ordinal)) { cursor += dur; continue; }
                    if (!schema.StartsWith("Clip", StringComparison.Ordinal)) { t.Skipped++; cursor += dur; continue; }   // nested stacks and others
                    string path = "";
                    if (it.TryGetProperty("media_reference", out var mr) && mr.ValueKind == JsonValueKind.Object)
                        path = PathFromUrl(Str(mr, "target_url"));
                    else if (it.TryGetProperty("media_references", out var mrs) && mrs.ValueKind == JsonValueKind.Object)
                    {
                        string key = Str(it, "active_media_reference_key");
                        foreach (var p in mrs.EnumerateObject())
                            if (string.IsNullOrEmpty(path) || p.Name == key) path = PathFromUrl(Str(p.Value, "target_url"));
                    }
                    bool enabled = !(it.TryGetProperty("enabled", out var ce) && ce.ValueKind == JsonValueKind.False);
                    if (string.IsNullOrEmpty(path) || !enabled || dur <= 0.001) { t.Skipped++; cursor += dur; continue; }
                    t.Clips.Add(new ImportedClip
                    {
                        Name = Str(it, "name"), Path = path, Start = cursor, Duration = dur, SourceOffset = off,
                        TrackNumber = n, IsAudio = audio
                    });
                    cursor += dur;
                }
            }
            t.Fps = rate > 0 ? rate : 30;
            return t;
        }

        private static string Schema(JsonElement e) => Str(e, "OTIO_SCHEMA");

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static (double start, double dur, double rate) Range(JsonElement it)
        {
            if (!it.TryGetProperty("source_range", out var sr) || sr.ValueKind != JsonValueKind.Object) return (0, 0, 0);
            var (s, r1) = Rt(sr, "start_time");
            var (d, r2) = Rt(sr, "duration");
            return (s, d, r2 > 0 ? r2 : r1);
        }

        private static (double seconds, double rate) Rt(JsonElement range, string name)
        {
            if (!range.TryGetProperty(name, out var rt) || rt.ValueKind != JsonValueKind.Object) return (0, 0);
            double v = rt.TryGetProperty("value", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
            double r = rt.TryGetProperty("rate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0;
            return (r > 0 ? v / r : 0, r);
        }

        // ── CMX 3600 EDL ─────────────────────────────────────────────────────────────

        private static readonly Regex EdlEvent = new Regex(
            @"^\s*(\d+)\s+(\S+)\s+(\S+)\s+(C|D|W\d*|K\S*)\s*(\d+)?\s+(\d\d:\d\d:\d\d[:;]\d\d)\s+(\d\d:\d\d:\d\d[:;]\d\d)\s+(\d\d:\d\d:\d\d[:;]\d\d)\s+(\d\d:\d\d:\d\d[:;]\d\d)",
            RegexOptions.Compiled);

        public static ImportedTimeline ReadEdl(string text, double fps = 0)
        {
            var t = new ImportedTimeline();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var l in lines)
            {
                if (l.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)) t.Name = l.Substring(6).Trim();
            }
            if (fps <= 0) fps = GuessEdlRate(lines);
            t.Fps = fps;
            var events = new List<(string channel, double srcIn, double srcOut, double recIn, double recOut, string name, string path, string reel)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = EdlEvent.Match(lines[i]);
                if (!m.Success) continue;
                string reel = m.Groups[2].Value, channel = m.Groups[3].Value.ToUpperInvariant(), trans = m.Groups[4].Value;
                if (trans != "C") t.Transitions++;
                string name = "", path = "";
                for (int j = i + 1; j < lines.Length && !EdlEvent.IsMatch(lines[j]); j++)
                {
                    string c = lines[j].Trim();
                    if (c.StartsWith("* FROM CLIP NAME:", StringComparison.OrdinalIgnoreCase)) name = c.Substring(17).Trim();
                    else if (c.StartsWith("* SOURCE FILE:", StringComparison.OrdinalIgnoreCase)) path = c.Substring(14).Trim();
                }
                events.Add((channel, Tc(m.Groups[6].Value, fps), Tc(m.Groups[7].Value, fps), Tc(m.Groups[8].Value, fps), Tc(m.Groups[9].Value, fps), name, path, reel));
            }
            if (events.Count == 0) throw new InvalidDataException("No events in this EDL file.");
            double zero = events.Min(e => e.recIn);
            zero = zero >= 3600 - 0.001 ? 3600 * Math.Floor(zero / 3600) : 0;   // record time usually starts at 01:00:00:00
            foreach (var e in events)
            {
                string file = !string.IsNullOrEmpty(e.path) ? e.path : !string.IsNullOrEmpty(e.name) ? e.name : e.reel;
                if (string.IsNullOrEmpty(file) || e.reel.Equals("BL", StringComparison.OrdinalIgnoreCase) || e.recOut - e.recIn <= 0.001) { t.Skipped++; continue; }
                bool video = e.channel.Contains('V') || e.channel == "B";
                bool sound = e.channel.Contains('A') || e.channel == "B";
                var c = new ImportedClip
                {
                    Name = string.IsNullOrEmpty(e.name) ? System.IO.Path.GetFileName(file) : e.name, Path = file,
                    Start = e.recIn - zero, Duration = e.recOut - e.recIn, SourceOffset = e.srcIn
                };
                if (video) { c.TrackNumber = 1; c.WithSound = sound; t.Clips.Add(c); continue; }
                // A, A2, AA (both channels) — the first audio channel number decides the track
                var digits = Regex.Match(e.channel, @"\d+");
                c.IsAudio = true;
                c.TrackNumber = digits.Success ? Math.Max(1, int.Parse(digits.Value, Inv)) : 1;
                t.Clips.Add(c);
            }
            return t;
        }

        private static double GuessEdlRate(string[] lines)
        {
            int maxFrame = 0;
            bool drop = false;
            foreach (var l in lines)
            {
                foreach (Match m in Regex.Matches(l, @"\d\d:\d\d:\d\d([:;])(\d\d)"))
                {
                    maxFrame = Math.Max(maxFrame, int.Parse(m.Groups[2].Value, Inv));
                    if (m.Groups[1].Value == ";") drop = true;
                }
            }
            if (drop) return 30000.0 / 1001;
            return maxFrame >= 30 ? 60 : maxFrame >= 25 ? 30 : maxFrame >= 24 ? 25 : maxFrame >= 1 ? 25 : 30;
        }

        private static double Tc(string tc, double fps)
        {
            var p = tc.Split(':', ';');
            int rate = (int)Math.Round(fps);
            return int.Parse(p[0], Inv) * 3600 + int.Parse(p[1], Inv) * 60 + int.Parse(p[2], Inv) + int.Parse(p[3], Inv) / (double)rate;
        }

        // ── shared ───────────────────────────────────────────────────────────────────

        /// <summary>file://localhost/C%3a/Videos/a.mp4, file:///C:/…, file:///home/… or a plain path → a path.</summary>
        public static string PathFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string s = url.Trim();
            if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(5);
                if (s.StartsWith("//localhost/", StringComparison.OrdinalIgnoreCase)) s = s.Substring(11);
                else if (s.StartsWith("///")) s = s.Substring(2);
                else if (s.StartsWith("//")) s = "\\\\" + s.Substring(2).Replace('/', '\\');   // network share
                s = Uri.UnescapeDataString(s);
                if (Regex.IsMatch(s, @"^/[A-Za-z]:")) s = s.Substring(1);   // /C:/… → C:/…
            }
            if (Regex.IsMatch(s, @"^[A-Za-z]:")) s = s.Replace('/', '\\');
            return s;
        }

        /// <summary>
        /// Joins each picture clip's own sound back to it, drops audio tracks left empty, and
        /// looks for files that moved next to the timeline file.
        /// </summary>
        private static void Finish(ImportedTimeline t, string folder)
        {
            // a clip's own sound may be on two mono tracks (left and right): drop the second copy
            foreach (var a in t.Clips.Where(c => c.IsAudio).ToList())
                if (t.Clips.Any(b => b != a && b.IsAudio && b.TrackNumber < a.TrackNumber && string.Equals(b.Path, a.Path, StringComparison.OrdinalIgnoreCase) &&
                                     Math.Abs(b.Start - a.Start) < 0.05 && Math.Abs(b.SourceOffset - a.SourceOffset) < 0.05 && Math.Abs(b.Duration - a.Duration) < 0.1))
                    t.Clips.Remove(a);
            var pictures = t.Clips.Where(c => !c.IsAudio).ToList();
            foreach (var a in t.Clips.Where(c => c.IsAudio).ToList())
            {
                var v = pictures.FirstOrDefault(p => !p.WithSound &&
                    string.Equals(p.Path, a.Path, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(p.Start - a.Start) < 0.05 && Math.Abs(p.SourceOffset - a.SourceOffset) < 0.05 && Math.Abs(p.Duration - a.Duration) < 0.1);
                if (v == null) continue;
                v.WithSound = true;
                t.Clips.Remove(a);
            }
            foreach (var audio in new[] { false, true })
            {
                var used = t.Clips.Where(c => c.IsAudio == audio).Select(c => c.TrackNumber).Distinct().OrderBy(n => n).ToList();
                foreach (var c in t.Clips.Where(c => c.IsAudio == audio)) c.TrackNumber = used.IndexOf(c.TrackNumber) + 1;
            }
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in t.Clips)
            {
                if (found.TryGetValue(c.Path, out var f)) { c.Path = f; c.Missing = !File.Exists(f); continue; }
                string original = c.Path, path = original;
                if (!File.Exists(path))
                {
                    string name = System.IO.Path.GetFileName(path.Replace('\\', '/').Split('/').Last());
                    string near = string.IsNullOrEmpty(name) ? "" : System.IO.Path.Combine(folder, name);
                    if (near != "" && File.Exists(near)) path = near;
                    else if (!string.IsNullOrEmpty(name) && Directory.Exists(folder))
                    {
                        try { path = Directory.EnumerateFiles(folder, name, SearchOption.AllDirectories).FirstOrDefault() ?? path; } catch { }
                    }
                }
                found[original] = path;
                c.Path = path;
                c.Missing = !File.Exists(path);
                if (c.Missing && !t.MissingFiles.Contains(System.IO.Path.GetFileName(path))) t.MissingFiles.Add(System.IO.Path.GetFileName(path));
            }
            t.Clips = t.Clips.OrderBy(c => c.IsAudio).ThenBy(c => c.TrackNumber).ThenBy(c => c.Start).ToList();
        }
    }
}
