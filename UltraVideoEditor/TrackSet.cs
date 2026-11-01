using System;
using System.Collections.Generic;
using System.Linq;

namespace UltraVideoEditor
{
    /// <summary>v2.0 — what the project remembers about its tracks (saved in the project file).</summary>
    public class TrackSetData
    {
        public int VideoTracks { get; set; } = 2;
        public int AudioTracks { get; set; } = 2;
        public List<TrackNameData> Names { get; set; } = new List<TrackNameData>();
        public List<int> Locked { get; set; } = new List<int>();
        public List<int> Hidden { get; set; } = new List<int>();
    }

    public class TrackNameData
    {
        public int Track { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// v2.0 — unlimited tracks. Track numbers stay compatible with older projects:
    /// 0 = Video 1 (the main track), 1 = Video 2, 2 = Audio 1, 3 = Audio 2,
    /// 4 and up = Video 3, Video 4…, 100 and up = Audio 3, Audio 4…
    /// Video 2 and higher are layers over the main track (the higher the number, the
    /// nearer the top of the picture). Every track can have its own name, can be locked
    /// against changes, and a layer track can be hidden from the picture.
    /// </summary>
    public static class TrackSet
    {
        public const int FirstExtraAudio = 100;
        public const int MaxVideoTracks = 60, MaxAudioTracks = 60;

        private static int _video = 2, _audio = 2;
        private static readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private static readonly HashSet<int> _locked = new HashSet<int>();
        private static readonly HashSet<int> _hidden = new HashSet<int>();

        /// <summary>Raised after tracks are added, removed, renamed, locked or hidden.</summary>
        public static event Action Changed;

        // ── numbering ────────────────────────────────────────────────────────────

        public static bool IsAudio(int t) => t == 2 || t == 3 || t >= FirstExtraAudio;
        public static bool IsVideo(int t) => t >= 0 && !IsAudio(t);
        /// <summary>A picture track over the main track (Video 2 and higher).</summary>
        public static bool IsLayer(int t) => t == 1 || (t >= 4 && t < FirstExtraAudio);

        /// <summary>The number people see: Video 1, Video 2, Video 3… / Audio 1, Audio 2, Audio 3…</summary>
        public static int Number(int t) =>
            IsAudio(t) ? (t == 2 ? 1 : t == 3 ? 2 : t - FirstExtraAudio + 3)
                       : (t == 0 ? 1 : t == 1 ? 2 : t - 1);

        public static int VideoTrack(int number) => number <= 1 ? 0 : number == 2 ? 1 : number + 1;
        public static int AudioTrack(int number) => number <= 1 ? 2 : number == 2 ? 3 : FirstExtraAudio + number - 3;

        /// <summary>Reading order: Video 1, 2, 3… before Audio 1, 2, 3….</summary>
        public static int SortKey(int t) => IsAudio(t) ? 1000 + Number(t) : Number(t);

        public static int VideoCount => _video;
        public static int AudioCount => _audio;

        /// <summary>"Video 3" / "Audio 1" — without the own name.</summary>
        public static string BaseName(int t) => (IsAudio(t) ? "Audio " : "Video ") + Number(t);

        /// <summary>"Video 3" or "Video 3: Titles" when the track has its own name.</summary>
        public static string Name(int t) =>
            _names.TryGetValue(t, out var n) && !string.IsNullOrWhiteSpace(n) ? BaseName(t) + ": " + n : BaseName(t);

        public static string OwnName(int t) => _names.TryGetValue(t, out var n) ? n : "";

        public static bool IsLocked(int t) => _locked.Contains(t);
        public static bool IsHidden(int t) => IsLayer(t) && _hidden.Contains(t);

        /// <summary>Video tracks Video 1, 2, 3…</summary>
        public static IEnumerable<int> VideoTracks() => Enumerable.Range(1, _video).Select(VideoTrack);
        /// <summary>Audio tracks Audio 1, 2, 3…</summary>
        public static IEnumerable<int> AudioTracks() => Enumerable.Range(1, _audio).Select(AudioTrack);
        /// <summary>All tracks in reading order: Video 1, Video 2… then Audio 1, Audio 2…</summary>
        public static List<int> All() => VideoTracks().Concat(AudioTracks()).ToList();

        /// <summary>Top to bottom as drawn: the highest video track first, down to Video 1, then Audio 1, 2…</summary>
        public static List<int> DisplayOrder() => VideoTracks().Reverse().Concat(AudioTracks()).ToList();

        // ── changes ──────────────────────────────────────────────────────────────

        /// <summary>Makes sure every track that holds a clip exists (older projects, undo, paste).</summary>
        public static void Sync(IEnumerable<TimelineItem> items)
        {
            if (items == null) return;
            bool changed = false;
            foreach (int t in items.Select(i => i.TrackIndex).Distinct())
            {
                if (t < 0) continue;
                int n = Number(t);
                if (IsAudio(t) && n > _audio) { _audio = Math.Min(MaxAudioTracks, n); changed = true; }
                else if (IsVideo(t) && n > _video) { _video = Math.Min(MaxVideoTracks, n); changed = true; }
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>Adds a track at the end; returns its track index, or -1 at the limit.</summary>
        public static int Add(bool audio)
        {
            if (audio)
            {
                if (_audio >= MaxAudioTracks) return -1;
                _audio++;
                Changed?.Invoke();
                return AudioTrack(_audio);
            }
            if (_video >= MaxVideoTracks) return -1;
            _video++;
            Changed?.Invoke();
            return VideoTrack(_video);
        }

        /// <summary>
        /// Removes an empty track. Tracks after it move up one number, with their clips,
        /// names, locks and mixer settings. The first two video and audio tracks stay.
        /// </summary>
        public static bool RemoveEmpty(int t, IList<TimelineItem> items)
        {
            bool audio = IsAudio(t);
            int n = Number(t);
            if (n <= 2 || items.Any(i => i.TrackIndex == t)) return false;
            int count = audio ? _audio : _video;
            if (n > count) return false;
            Func<int, int> track = audio ? (Func<int, int>)AudioTrack : VideoTrack;

            for (int k = n + 1; k <= count; k++)
            {
                int from = track(k), to = track(k - 1);
                foreach (var it in items.Where(i => i.TrackIndex == from)) it.TrackIndex = to;
                Move(_names, from, to);
                MoveFlag(_locked, from, to);
                MoveFlag(_hidden, from, to);
                foreach (var m in TrackMixer.Current.Where(m => m.Track == to).ToList()) TrackMixer.Current.Remove(m);
                foreach (var m in TrackMixer.Current.Where(m => m.Track == from)) m.Track = to;
            }
            int last = track(count);
            _names.Remove(last); _locked.Remove(last); _hidden.Remove(last);
            TrackMixer.Current.RemoveAll(m => m.Track == last);
            if (audio) _audio--; else _video--;
            Changed?.Invoke();
            return true;
        }

        private static void Move(Dictionary<int, string> d, int from, int to)
        {
            d.Remove(to);
            if (d.TryGetValue(from, out var v)) { d[to] = v; d.Remove(from); }
        }

        private static void MoveFlag(HashSet<int> s, int from, int to)
        {
            s.Remove(to);
            if (s.Remove(from)) s.Add(to);
        }

        public static void Rename(int t, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) _names.Remove(t); else _names[t] = name.Length > 40 ? name.Substring(0, 40) : name;
            Changed?.Invoke();
        }

        /// <summary>Locks or unlocks a track; returns the new state.</summary>
        public static bool ToggleLock(int t)
        {
            bool now = !_locked.Remove(t);
            if (now) _locked.Add(t);
            Changed?.Invoke();
            return now;
        }

        /// <summary>Hides or shows a layer track's picture; returns true when it is now hidden.</summary>
        public static bool ToggleHidden(int t)
        {
            if (!IsLayer(t)) return false;
            bool now = !_hidden.Remove(t);
            if (now) _hidden.Add(t);
            Changed?.Invoke();
            return now;
        }

        // ── project file ─────────────────────────────────────────────────────────

        public static void Reset()
        {
            _video = 2; _audio = 2;
            _names.Clear(); _locked.Clear(); _hidden.Clear();
            Changed?.Invoke();
        }

        public static TrackSetData Save() => new TrackSetData
        {
            VideoTracks = _video,
            AudioTracks = _audio,
            Names = _names.Select(kv => new TrackNameData { Track = kv.Key, Name = kv.Value }).OrderBy(n => n.Track).ToList(),
            Locked = _locked.OrderBy(x => x).ToList(),
            Hidden = _hidden.OrderBy(x => x).ToList()
        };

        public static void Load(TrackSetData data, IEnumerable<TimelineItem> items)
        {
            _names.Clear(); _locked.Clear(); _hidden.Clear();
            _video = Math.Clamp(data?.VideoTracks ?? 2, 2, MaxVideoTracks);
            _audio = Math.Clamp(data?.AudioTracks ?? 2, 2, MaxAudioTracks);
            foreach (var n in data?.Names ?? new List<TrackNameData>())
                if (!string.IsNullOrWhiteSpace(n?.Name)) _names[n.Track] = n.Name;
            foreach (int t in data?.Locked ?? new List<int>()) _locked.Add(t);
            foreach (int t in data?.Hidden ?? new List<int>()) _hidden.Add(t);
            Sync(items);
            Changed?.Invoke();
        }
    }
}
