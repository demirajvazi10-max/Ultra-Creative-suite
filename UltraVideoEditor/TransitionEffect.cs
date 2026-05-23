using System;

namespace UltraVideoEditor
{
    public enum TransitionType
    {
        None,
        Fade,
        Crossfade,
        SlideLeft,
        SlideRight,
        SlideUp,
        SlideDown,
        WipeLeft,
        WipeRight,
        WipeUp,
        WipeDown,
        ZoomIn,
        ZoomOut
    }

    public class TransitionEffect
    {
        public string Name { get; set; }
        public TransitionType Type { get; set; }
        public double Duration { get; set; } = 1.0;
        public int ClipIndex1 { get; set; }
        public int ClipIndex2 { get; set; }

        public string Icon => Type switch
        {
            TransitionType.Fade      => "🌅",
            TransitionType.Crossfade => "🔄",
            TransitionType.SlideLeft  => "⬅️",
            TransitionType.SlideRight => "➡️",
            TransitionType.SlideUp    => "⬆️",
            TransitionType.SlideDown  => "⬇️",
            TransitionType.WipeLeft   => "🧹",
            TransitionType.WipeRight  => "🧹",
            TransitionType.ZoomIn     => "🔍➕",
            TransitionType.ZoomOut    => "🔍➖",
            _ => "📄"
        };

        public string FFmpegFilter
        {
            get
            {
                string transitionType = Type switch
                {
                    TransitionType.Fade       => "fade",
                    TransitionType.Crossfade  => "fade",
                    TransitionType.SlideLeft  => "slideleft",
                    TransitionType.SlideRight => "slideright",
                    TransitionType.SlideUp    => "slideup",
                    TransitionType.SlideDown  => "slidedown",
                    TransitionType.WipeLeft   => "wipeleft",
                    TransitionType.WipeRight  => "wiperight",
                    TransitionType.ZoomIn     => "zoom",
                    TransitionType.ZoomOut    => "zoom",
                    _ => "fade"
                };
                return $"xfade=transition={transitionType}:duration={Duration}:offset={Duration}";
            }
        }

        public string AudioCrossfadeFilter => $"acrossfade=d={Duration}:c1=1:c2=1";

        /// <summary>
        /// Bira tip tranzicije na osnovu energije scene i sezone.
        /// Energy 1-2 (slow): mekani fade — ne remeti mirne scene.
        /// Energy 3 (standard): naizmjenično wipe i crossfade — daje ritam.
        /// Energy 4-5 (fast): slide/wipe — prati akciju.
        /// Sezona winter: preferira slidedown (kao snijeg koji pada).
        /// Sezona spring/summer: preferira slideup (rast, energija).
        /// Seed je baziran na indeksu klipa — determinstički ali raznovrstan.
        /// </summary>
        public static TransitionType PickForEnergy(int energy, string season, int clipIndex)
        {
            int seed = clipIndex * 7 + energy * 13;
            // Ne koristimo Random(seed) jer C# Random nije garantovano isti između sessija,
            // ali seed % N daje stabilan, raznovrstan odabir koji nije uvijek isti.

            if (energy <= 2)
            {
                // Tihe scene — samo fade, nikad slide/wipe
                return TransitionType.Fade;
            }
            else if (energy == 3)
            {
                // Standardne scene — 4 opcije, naizmjenično
                var options = new[]
                {
                    TransitionType.Crossfade,
                    TransitionType.WipeLeft,
                    TransitionType.WipeRight,
                    TransitionType.Fade
                };
                // Sezonska modulacija
                if (season == "spring" || season == "summer")
                {
                    options = new[]
                    {
                        TransitionType.WipeLeft,
                        TransitionType.WipeRight,
                        TransitionType.SlideUp,
                        TransitionType.Crossfade
                    };
                }
                else if (season == "winter")
                {
                    options = new[]
                    {
                        TransitionType.Fade,
                        TransitionType.SlideDown,
                        TransitionType.WipeLeft,
                        TransitionType.Crossfade
                    };
                }
                return options[seed % options.Length];
            }
            else
            {
                // Energične scene — dinamični slides
                var options = new[]
                {
                    TransitionType.SlideLeft,
                    TransitionType.SlideRight,
                    TransitionType.WipeLeft,
                    TransitionType.WipeRight,
                    TransitionType.ZoomIn
                };
                if (season == "winter")
                {
                    options = new[]
                    {
                        TransitionType.SlideLeft,
                        TransitionType.SlideRight,
                        TransitionType.SlideDown,
                        TransitionType.WipeLeft,
                        TransitionType.WipeRight
                    };
                }
                return options[seed % options.Length];
            }
        }
    }
}
