using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — one timeline clip as the exporter sees it.</summary>
    public class TlClip
    {
        public string Name = "";
        public string Path = "";
        public double Start, Duration, SourceOffset;
        public int Track;
        public bool IsAudio, IsImage;
        /// <summary>A video clip whose own sound is heard (not muted).</summary>
        public bool WithSound;
    }

    /// <summary>
    /// v2.0 — writes the timeline for other editors, so a cut made here can be finished in
    /// DaVinci Resolve, Premiere Pro, Final Cut (through OTIO) or any tool that reads EDL:
    ///  • OpenTimelineIO (.otio) — every track, gaps kept, the in-point of every clip;
    ///  • CMX 3600 EDL (.edl) — Video 1 and the two audio tracks.
    /// Only plain JSON / text is written; no library is needed.
    /// </summary>
    public static class TimelineExporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string TrackName(int track) => TrackSet.Name(track);   // v2.0: unlimited, named tracks

        public static bool IsAudioTrack(int track) => TrackSet.IsAudio(track);

        // ── OpenTimelineIO ───────────────────────────────────────────────────────────

        public static string ToOtio(string timelineName, IEnumerable<TlClip> clips, double fps)
        {
            fps = fps > 1 ? fps : 30;
            var all = clips.Where(c => c.Duration > 0.001).ToList();
            var tracks = new List<string>();

            // picture tracks first (lowest first), then the audio tracks
            foreach (var group in all.GroupBy(c => c.Track).OrderBy(g => IsAudioTrack(g.Key) ? 100 + g.Key : g.Key))
            {
                bool audio = IsAudioTrack(group.Key);
                var items = group.Where(c => audio ? true : !c.IsAudio).OrderBy(c => c.Start).ToList();
                if (items.Count == 0) continue;
                tracks.Add(Track(TrackName(group.Key), audio ? "Audio" : "Video", items, fps));
            }

            // the sound of the video clips on Video 1, as its own audio track (as editors link it)
            var v1Sound = all.Where(c => c.Track == 0 && c.WithSound && !c.IsImage && !c.IsAudio).OrderBy(c => c.Start).ToList();
            if (v1Sound.Count > 0) tracks.Add(Track("Video 1 sound", "Audio", v1Sound, fps));

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"OTIO_SCHEMA\": \"Timeline.1\",\n");
            sb.Append($"  \"metadata\": {{ \"Ultra Video Editor\": {{ \"exported\": {Q(DateTime.Now.ToString("s", Inv))} }} }},\n");
            sb.Append($"  \"name\": {Q(timelineName)},\n");
            sb.Append("  \"global_start_time\": null,\n");
            sb.Append("  \"tracks\": {\n");
            sb.Append("    \"OTIO_SCHEMA\": \"Stack.1\",\n");
            sb.Append("    \"metadata\": {},\n");
            sb.Append("    \"name\": \"tracks\",\n");
            sb.Append("    \"source_range\": null,\n");
            sb.Append("    \"effects\": [],\n");
            sb.Append("    \"markers\": [],\n");
            sb.Append("    \"children\": [\n");
            sb.Append(string.Join(",\n", tracks));
            sb.Append("\n    ]\n  }\n}\n");
            return sb.ToString();
        }

        private static string Track(string name, string kind, List<TlClip> items, double fps)
        {
            var children = new List<string>();
            double cursor = 0;
            foreach (var c in items)
            {
                double start = c.Start, offset = c.SourceOffset, dur = c.Duration;
                if (start < cursor - 0.0005)
                {
                    // overlapping clip on one track: keep only the part after the previous clip
                    double cut = cursor - start;
                    if (cut >= dur - 0.0005) continue;
                    start = cursor; offset += cut; dur -= cut;
                }
                if (start - cursor > 0.0005) children.Add(Gap(start - cursor, fps));
                children.Add(Clip(c, offset, dur, fps));
                cursor = start + dur;
            }
            return "      {\n" +
                   "        \"OTIO_SCHEMA\": \"Track.1\",\n" +
                   "        \"metadata\": {},\n" +
                   $"        \"name\": {Q(name)},\n" +
                   "        \"source_range\": null,\n" +
                   "        \"effects\": [],\n" +
                   "        \"markers\": [],\n" +
                   $"        \"kind\": {Q(kind)},\n" +
                   "        \"children\": [\n" + string.Join(",\n", children) + "\n        ]\n" +
                   "      }";
        }

        private static string Gap(double seconds, double fps) =>
            "          {\n" +
            "            \"OTIO_SCHEMA\": \"Gap.1\",\n" +
            "            \"metadata\": {},\n" +
            "            \"name\": \"\",\n" +
            $"            \"source_range\": {Range(0, seconds, fps)},\n" +
            "            \"effects\": [],\n" +
            "            \"markers\": []\n" +
            "          }";

        private static string Clip(TlClip c, double offset, double dur, double fps) =>
            "          {\n" +
            "            \"OTIO_SCHEMA\": \"Clip.1\",\n" +
            "            \"metadata\": {},\n" +
            $"            \"name\": {Q(string.IsNullOrWhiteSpace(c.Name) ? System.IO.Path.GetFileName(c.Path) : c.Name)},\n" +
            $"            \"source_range\": {Range(offset, dur, fps)},\n" +
            "            \"effects\": [],\n" +
            "            \"markers\": [],\n" +
            "            \"media_reference\": {\n" +
            "              \"OTIO_SCHEMA\": \"ExternalReference.1\",\n" +
            "              \"metadata\": {},\n" +
            "              \"name\": \"\",\n" +
            "              \"available_range\": null,\n" +
            $"              \"target_url\": {Q(FileUrl(c.Path))}\n" +
            "            }\n" +
            "          }";

        private static string Range(double start, double dur, double fps) =>
            "{ \"OTIO_SCHEMA\": \"TimeRange.1\", " +
            $"\"start_time\": {Rt(start, fps)}, \"duration\": {Rt(dur, fps)} }}";

        private static string Rt(double seconds, double fps) =>
            $"{{ \"OTIO_SCHEMA\": \"RationalTime.1\", \"rate\": {fps.ToString("0.###", Inv)}, \"value\": {Math.Round(seconds * fps, MidpointRounding.AwayFromZero).ToString(Inv)} }}";

        internal static string FileUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri; }
            catch { return "file:///" + path.Replace('\\', '/'); }
        }

        private static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ── Final Cut Pro 7 XML (xmeml) ──────────────────────────────────────────────

        /// <summary>
        /// v2.0 — the timeline as Final Cut Pro 7 XML (xmeml version 5), which Premiere Pro,
        /// DaVinci Resolve, Final Cut Pro 7 and many other tools import: every video track (layers
        /// included), every audio track, the sound of the clips on Video 1, in-points and gaps.
        /// </summary>
        public static string ToXml(string name, IEnumerable<TlClip> clips, double fps, int width, int height)
        {
            fps = fps > 1 ? fps : 30;
            bool ntsc = Math.Abs(fps - Math.Round(fps)) > 0.01;
            int timebase = (int)Math.Round(ntsc ? fps * 1001 / 1000 : fps);
            double frame = ntsc ? 1001.0 / (timebase * 1000) : 1.0 / timebase;
            long F(double seconds) => (long)Math.Round(seconds / frame, MidpointRounding.AwayFromZero);
            string Rate() => $"<rate><timebase>{timebase}</timebase><ntsc>{(ntsc ? "TRUE" : "FALSE")}</ntsc></rate>";
            string X(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

            var all = clips.Where(c => c.Duration > 0.001 && !string.IsNullOrEmpty(c.Path)).ToList();
            var fileIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int clipNo = 0;

            string ClipItem(TlClip c, double start, double offset, double dur, bool audio)
            {
                string id = $"clipitem-{++clipNo}";
                bool first = !fileIds.TryGetValue(c.Path, out string fid);
                if (first) { fid = $"file-{fileIds.Count + 1}"; fileIds[c.Path] = fid; }
                long inF = c.IsImage ? 0 : F(offset), outF = inF + Math.Max(1, F(dur));
                string file = first
                    ? $"<file id=\"{fid}\"><name>{X(System.IO.Path.GetFileName(c.Path))}</name><pathurl>{X(FileUrl(c.Path))}</pathurl>{Rate()}" +
                      $"<duration>{Math.Max(outF, F(offset + dur) + 1)}</duration><media>" +
                      (c.IsAudio ? "" : $"<video><samplecharacteristics><width>{width}</width><height>{height}</height></samplecharacteristics></video>") +
                      (c.IsImage ? "" : "<audio><channelcount>2</channelcount></audio>") + "</media></file>"
                    : $"<file id=\"{fid}\"/>";
                return $"<clipitem id=\"{id}\"><name>{X(string.IsNullOrWhiteSpace(c.Name) ? System.IO.Path.GetFileName(c.Path) : c.Name)}</name>" +
                       $"<enabled>TRUE</enabled><duration>{Math.Max(outF, F(offset + dur) + 1)}</duration>{Rate()}" +
                       $"<start>{F(start)}</start><end>{F(start) + (outF - inF)}</end><in>{inF}</in><out>{outF}</out>{file}" +
                       (audio ? "<sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>" : "") + "</clipitem>";
            }

            string TrackXml(List<TlClip> items, bool audio)
            {
                var sb = new StringBuilder("<track>");
                double cursor = 0;
                foreach (var c in items.OrderBy(i => i.Start))
                {
                    double start = c.Start, offset = c.SourceOffset, dur = c.Duration;
                    if (start < cursor - 0.0005)
                    {
                        double cut = cursor - start;
                        if (cut >= dur - 0.0005) continue;
                        start = cursor; offset += cut; dur -= cut;
                    }
                    sb.Append(ClipItem(c, start, offset, dur, audio));
                    cursor = start + dur;
                }
                return sb.Append("</track>").ToString();
            }

            var video = new StringBuilder();
            foreach (var g in all.Where(c => !IsAudioTrack(c.Track) && !c.IsAudio).GroupBy(c => c.Track).OrderBy(g => TrackSet.SortKey(g.Key)))
                video.Append(TrackXml(g.ToList(), false));
            var audioXml = new StringBuilder();
            var v1Sound = all.Where(c => c.Track == 0 && c.WithSound && !c.IsImage && !c.IsAudio).ToList();
            if (v1Sound.Count > 0) audioXml.Append(TrackXml(v1Sound, true));
            foreach (var g in all.Where(c => IsAudioTrack(c.Track) || c.IsAudio).GroupBy(c => c.Track).OrderBy(g => TrackSet.SortKey(g.Key)))
                audioXml.Append(TrackXml(g.ToList(), true));

            double total = all.Count == 0 ? 0 : all.Max(c => c.Start + c.Duration);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE xmeml>\n" +
                   $"<xmeml version=\"5\"><sequence id=\"sequence-1\"><name>{X(name)}</name><duration>{F(total)}</duration>{Rate()}" +
                   "<media><video><format><samplecharacteristics>" +
                   $"<width>{width}</width><height>{height}</height><pixelaspectratio>square</pixelaspectratio>{Rate()}" +
                   $"</samplecharacteristics></format>{video}</video><audio>{audioXml}</audio></media></sequence></xmeml>\n";
        }

        // ── CMX 3600 EDL ─────────────────────────────────────────────────────────────

        /// <summary>Video 1 and Audio 1 / Audio 2 as an EDL. Record time starts at 01:00:00:00.</summary>
        public static string ToEdl(string title, IEnumerable<TlClip> clips, double fps)
        {
            int rate = (int)Math.Max(1, Math.Round(fps > 1 ? fps : 30));
            var sb = new StringBuilder();
            sb.Append("TITLE: ").Append(Ascii(title)).Append("\r\n");
            sb.Append("FCM: NON-DROP FRAME\r\n\r\n");
            int n = 1;
            var events = new List<(TlClip c, string ch)>();
            events.AddRange(clips.Where(c => c.Track == 0 && !c.IsAudio && c.Duration > 0.001).Select(c => (c, c.WithSound && !c.IsImage ? "B" : "V")));
            events.AddRange(clips.Where(c => c.Track == 2 && c.Duration > 0.001).Select(c => (c, "A")));
            events.AddRange(clips.Where(c => c.Track == 3 && c.Duration > 0.001).Select(c => (c, "A2")));
            foreach (var (c, ch) in events.OrderBy(e => e.c.Start).ThenBy(e => e.ch))
            {
                string reel = c.IsImage ? "AX" : "AX";
                double srcIn = c.SourceOffset, srcOut = c.SourceOffset + c.Duration;
                double recIn = 3600 + c.Start, recOut = 3600 + c.Start + c.Duration;
                sb.Append($"{n:000}  {reel,-8} {ch,-5} C        {Tc(srcIn, rate)} {Tc(srcOut, rate)} {Tc(recIn, rate)} {Tc(recOut, rate)}\r\n");
                sb.Append("* FROM CLIP NAME: ").Append(Ascii(System.IO.Path.GetFileName(c.Path))).Append("\r\n");
                sb.Append("* SOURCE FILE: ").Append(Ascii(c.Path)).Append("\r\n\r\n");
                n++;
            }
            return sb.ToString();
        }

        internal static string Tc(double seconds, int rate)
        {
            long frames = (long)Math.Round(Math.Max(0, seconds) * rate);
            long f = frames % rate, totalSec = frames / rate;
            return $"{totalSec / 3600:00}:{totalSec / 60 % 60:00}:{totalSec % 60:00}:{f:00}";
        }

        /// <summary>EDL readers expect plain ASCII; letters like č, ć, š, ž, đ become c, c, s, z, dj.</summary>
        private static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("đ", "dj").Replace("Đ", "Dj");
            var norm = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char ch in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(ch < 128 ? ch : '_');
            }
            return sb.ToString();
        }
    }
}
