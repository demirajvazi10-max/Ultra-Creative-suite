using System;
using System.Collections.Generic;
using System.Linq;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // TRANSITION ENGINE  —  Faza 3 / B
    //
    // Automatski bira i primenjuje prelaze između highlight segmenata.
    // Prelazi su sinhronizovani na beat, a tip prelaza se bira na osnovu
    // sadržaja susednih kadrova (ContentTag, ArcBonus, Motion).
    //
    // Integracija: pozovi ApplyTransitions() na listi segmenata
    // odmah pre prosleđivanja RenderEnginu — metoda upisuje
    // xfade tag u AudioDescription svakog TimelineItem-a.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Tip xfade prelaza podržan od strane ffmpeg xfade filtera.</summary>
    public enum XfadeType
    {
        Fade,        // klasičan cross-dissolve
        Dissolve,    // teksturalni dissolve
        WipeLeft,    // brisanje s desna na lijevo
        WipeRight,
        WipeUp,
        WipeDown,
        SlideLeft,   // klizanje
        SlideRight,
        ZoomIn,      // zoom punch
        FadeBlack,   // crni frame između
        FadeWhite,   // bijeli frame između (flash)
        Pixelize,    // pixelizacija — energičan prelaz
        Diagtl,      // dijagonalni wipe
        Diagtr,
    }

    /// <summary>Jedan odlučen prelaz između dva segmenta.</summary>
    public class TransitionDecision
    {
        /// <summary>Redni broj segmenta POSLE kojeg ide ovaj prelaz (0-based).</summary>
        public int     AfterSegmentIndex { get; set; }

        /// <summary>Odabrani tip prelaza.</summary>
        public XfadeType Type            { get; set; }

        /// <summary>Trajanje prelaza u sekundama (0.2–0.8s).</summary>
        public double  Duration          { get; set; }

        /// <summary>Zašto je odabran ovaj prelaz (za debug / report).</summary>
        public string  Reason            { get; set; }
    }

    public static class TransitionEngine
    {
        // ── Tuning ──────────────────────────────────────────────────
        private const double FastCutDuration  = 0.20;   // energičan rez
        private const double NormalDuration   = 0.40;   // standardni prelaz
        private const double SlowDuration     = 0.65;   // emotivni prelaz

        // ── Javni API ────────────────────────────────────────────────

        /// <summary>
        /// Analizira listu segmenata i vraća odluke o prelazima.
        /// </summary>
        public static List<TransitionDecision> Decide(
            List<HighlightSegment> segments,
            BeatInfo beats = null)
        {
            var decisions = new List<TransitionDecision>();
            if (segments == null || segments.Count < 2) return decisions;

            for (int i = 0; i < segments.Count - 1; i++)
            {
                var current = segments[i];
                var next    = segments[i + 1];
                decisions.Add(DecideOne(i, current, next, beats));
            }
            return decisions;
        }

        /// <summary>
        /// Primenjuje odluke o prelazima na TimelineItem listu upisivanjem
        /// xfade taga u AudioDescription — RenderEngine ga čita automatski.
        /// </summary>
        public static void ApplyToTimeline(
            List<TimelineItem>       items,
            List<TransitionDecision> decisions)
        {
            if (items == null || decisions == null) return;

            foreach (var dec in decisions)
            {
                int idx = dec.AfterSegmentIndex;
                if (idx < 0 || idx >= items.Count) continue;

                var item = items[idx];
                string xfadeTag  = $"xfade={XfadeToString(dec.Type)}";
                string durTag    = $"xfade_dur={dec.Duration:F2}";

                // Upisujemo u AudioDescription — RenderEngine to vec cita
                item.AudioDescription = AppendTag(item.AudioDescription, xfadeTag);
                item.AudioDescription = AppendTag(item.AudioDescription, durTag);
            }
        }

        // ── Logika odlučivanja ───────────────────────────────────────

        private static TransitionDecision DecideOne(
            int index,
            HighlightSegment current,
            HighlightSegment next,
            BeatInfo beats)
        {
            // Određujemo trajanje prelaza na osnovu BPM-a
            double duration = NormalDuration;
            if (beats != null && beats.IsValid)
            {
                if (beats.BPM > 140)      duration = FastCutDuration;
                else if (beats.BPM < 80)  duration = SlowDuration;
            }

            // Biramo tip na osnovu sadržaja
            var type   = ChooseType(current, next);
            string reason = BuildReason(current, next, type);

            return new TransitionDecision
            {
                AfterSegmentIndex = index,
                Type              = type,
                Duration          = duration,
                Reason            = reason,
            };
        }

        private static XfadeType ChooseType(
            HighlightSegment current,
            HighlightSegment next)
        {
            string tagA = (current.ContentDescription ?? "").ToLowerInvariant();
            string tagB = (next.ContentDescription ?? "").ToLowerInvariant();

            bool currentAction  = tagA.Contains("action")  || (current.Motion?.HasStrongMotion == true);
            bool nextAction     = tagB.Contains("action")  || (next.Motion?.HasStrongMotion    == true);
            bool currentPortrait= tagA.Contains("portrait") || tagA.Contains("face") || tagA.Contains("person");
            bool nextPortrait   = tagB.Contains("portrait") || tagB.Contains("face") || tagB.Contains("person");
            bool arcTransition  = current.ArcBonus > 8.0 || next.ArcBonus > 8.0;
            bool sameContent    = tagA == tagB && !string.IsNullOrEmpty(tagA);

            // Action → Action: energičan flash cut
            if (currentAction && nextAction)
                return XfadeType.FadeWhite;

            // Action → Portrait ili Portrait → Action: zoom punch
            if (currentAction && nextPortrait)
                return XfadeType.ZoomIn;

            // Portrait → Portrait: emotivni dissolve
            if (currentPortrait && nextPortrait)
                return XfadeType.Dissolve;

            // Arc prelaz (dinamičan razvoj kadra): wipe u smeru kretanja
            if (arcTransition)
            {
                if (current.Motion != null && current.Motion.Direction == MotionDirection.Left)   return XfadeType.WipeLeft;
                if (current.Motion != null && current.Motion.Direction == MotionDirection.Right)  return XfadeType.WipeRight;
                if (current.Motion != null && current.Motion.Direction == MotionDirection.Up)     return XfadeType.WipeUp;
                return XfadeType.WipeDown;
            }

            // Isti sadržaj zaredom: dijagonalni wipe da ne bude monotono
            if (sameContent)
                return (current.Order % 2 == 0) ? XfadeType.Diagtl : XfadeType.Diagtr;

            // Visoko→nisko scored ili vice versa: slide
            if (Math.Abs(current.ImportanceScore - next.ImportanceScore) > 30)
                return XfadeType.SlideLeft;

            // Default: klasičan cross-dissolve
            return XfadeType.Fade;
        }

        private static string BuildReason(
            HighlightSegment current,
            HighlightSegment next,
            XfadeType type)
        {
            return $"{current.ContentDescription} → {next.ContentDescription} | " +
                   $"Motion: {current.Motion?.HasStrongMotion.ToString() ?? "?"}/{next.Motion?.HasStrongMotion.ToString() ?? "?"} | " +
                   $"Arc: {current.ArcBonus:F0}/{next.ArcBonus:F0} | " +
                   $"Odabrano: {type}";
        }

        // ── Helpers ──────────────────────────────────────────────────

        public static string XfadeToString(XfadeType t) => t switch
        {
            XfadeType.Fade      => "fade",
            XfadeType.Dissolve  => "dissolve",
            XfadeType.WipeLeft  => "wipeleft",
            XfadeType.WipeRight => "wiperight",
            XfadeType.WipeUp    => "wipeup",
            XfadeType.WipeDown  => "wipedown",
            XfadeType.SlideLeft => "slideleft",
            XfadeType.SlideRight=> "slideright",
            XfadeType.ZoomIn    => "zoomin",
            XfadeType.FadeBlack => "fadeblack",
            XfadeType.FadeWhite => "fadewhite",
            XfadeType.Pixelize  => "pixelize",
            XfadeType.Diagtl    => "diagtl",
            XfadeType.Diagtr    => "diagtr",
            _                   => "fade",
        };

        private static string AppendTag(string existing, string tag)
        {
            if (string.IsNullOrEmpty(existing)) return tag;
            if (existing.Contains(tag.Split('=')[0])) return existing; // već postoji
            return existing + ";" + tag;
        }
    }
}
