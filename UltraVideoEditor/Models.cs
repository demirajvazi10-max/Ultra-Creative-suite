using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return System.Windows.Visibility.Visible;
        }
    }

    // Keyframe za animaciju
    public class AnimationKeyframe
    {
        public double Time { get; set; } = 0;
        public double Zoom { get; set; } = 1.0;
        public double Rotation { get; set; } = 0;
        public double X { get; set; } = 0;
        public double Y { get; set; } = 0;
        public double Opacity { get; set; } = 1.0;
        /// <summary>
        /// v2.0 — how the movement from this keyframe to the next one goes: Linear (even),
        /// EaseIn (starts slowly), EaseOut (ends slowly), EaseInOut (both), Hold (stays, then jumps).
        /// </summary>
        public string Easing { get; set; } = "Linear";

        public static readonly string[] Easings = { "Linear", "EaseIn", "EaseOut", "EaseInOut", "Hold" };

        /// <summary>v2.0 — what a screen reader says for a keyframe in the list.</summary>
        public override string ToString()
        {
            string T(string k) => LanguageManager.GetText(k, LanguageManager.CurrentLanguage ?? "en");
            var inv = System.Globalization.CultureInfo.CurrentCulture;
            return string.Format(T("kf_item"), Time.ToString("0.0", inv), Zoom.ToString("0.00", inv), Math.Round(Rotation),
                                 Math.Round(X), Math.Round(Y), Opacity.ToString("0.00", inv), T("kf_ease_" + (Easing ?? "Linear")));
        }
    }

    // Scene za AI animaciju
    public class AnimationScene
    {
        public string ImageName { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public double Duration { get; set; } = 5.0;
        public string Effect { get; set; } = "Fade In";
        public string Description { get; set; } = "";
        public List<string> AvailableImages { get; set; } = new List<string>();
        public List<string> AvailableEffects { get; set; } = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" };

        // v1.4: list items use ToString() as their screen-reader name (otherwise the class name is read)
        public override string ToString() =>
            $"{ImageName}, {Duration:0.#} s, {Effect}" + (string.IsNullOrWhiteSpace(Description) ? "" : ", " + Description);
    }

    // Podaci za tekst na slici (overlay)
    public class TextOverlayData
    {
        public string Text { get; set; } = "";
        public string Font { get; set; } = "Arial";
        public int SelectedFontSize { get; set; } = 48;
        public string Color { get; set; } = "#FFFFFF";
        public string Position { get; set; } = "Centar";
        public bool Enabled { get; set; } = false;

        // v2.0 — titles: drawn in the render (before, text on a clip was stored but never drawn)
        /// <summary>A second, smaller line under the text (a name's role, a place…).</summary>
        public string Subtitle { get; set; } = "";
        /// <summary>None, Fade, SlideUp, SlideLeft, Zoom, Typewriter.</summary>
        public string Animation { get; set; } = "Fade";
        /// <summary>A dark band behind the text so it reads on any picture.</summary>
        public bool Box { get; set; } = false;
        /// <summary>Seconds after the clip begins when the text appears.</summary>
        public double StartIn { get; set; } = 0;
        /// <summary>How long it stays, in seconds; 0 = until the clip ends.</summary>
        public double Length { get; set; } = 0;

        public TextOverlayData Clone() => (TextOverlayData)MemberwiseClone();
    }

    /// <summary>v1.4 (R4) — one described part of a clip.</summary>
    public class FrameDescription
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = "";
    }

    // Timeline Item with support for multiple tracks and keyframes
    public class TimelineItem
    {
        private static string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public double FixedPosition { get; set; } = 0;
        public string Path { get; set; } = "";
        public double Duration { get; set; } = 5.0;

        /// <summary>
        /// v1.4 — where in the SOURCE file this clip begins, in seconds (in-point).
        /// 0 for a whole file. Set when a clip is cut (the second part continues where the
        /// first ended) and for highlight segments. The render seeks here before playing.
        /// </summary>
        public double SourceOffset { get; set; } = 0;

        /// <summary>v1.4 — xfade transition INTO the next clip (e.g. "slideleft"); null = automatic.</summary>
        public string TransitionOut { get; set; }

        /// <summary>v1.4 — length of that transition in seconds; 0 = automatic.</summary>
        public double TransitionOutDuration { get; set; } = 0;

        // DODATO ZA SELEKCIJU
        public bool IsSelected { get; set; } = false;

        // DODATO ZA FIKSNU POZICIJU (POSTAVI NA OSU)
        public bool UseFixedPosition { get; set; } = false;

        // DODATO ZA TEKST NA SLICI (OVERLAY)
        public TextOverlayData TextOverlay { get; set; } = new TextOverlayData();

        [JsonIgnore]
        public double Start { get; set; } = 0;

        [JsonIgnore]
        public double End { get; set; } = 5.0;

        [JsonIgnore]
        public int Index { get; set; } = 0;

        [JsonIgnore]
        public string TimeRange { get; set; } = "";

        public string Name { get; set; } = "";
        public string Type { get; set; } = "User";
        public double Volume { get; set; } = 100;
        public int TrackIndex { get; set; } = 0;

        [JsonIgnore]
        public string AccessibilityDescription { get; set; } = "";

        public string AudioDescription { get; set; }
        public string AmbientSoundPath { get; set; } = "";

        /// <summary>
        /// v1.4 — the media file's path relative to the project file, written on every save.
        /// When a project is moved to another folder, disk or computer, Path no longer exists
        /// and the file is found again through this.
        /// </summary>
        public string RelativePath { get; set; } = "";
        public string ContentTag { get; set; }  // Hybrid Content Selector: Emotional|Portrait|Action|Nature
        public VideoEffectData VideoEffect { get; set; } = new VideoEffectData();
        public List<AnimationKeyframe> Keyframes { get; set; } = new List<AnimationKeyframe>();

        /// <summary>
        /// v1.4 (R4) — what is in the picture, part by part (times are seconds from the clip's
        /// start on the timeline). Made by "Describe the clip by parts"; saved with the project.
        /// </summary>
        public List<FrameDescription> FrameDescriptions { get; set; }

        /// <summary>v1.4 (R5) — volume over time; null/empty = the clip volume all the way.</summary>
        public List<VolumePoint> VolumePoints { get; set; }

        /// <summary>v1.4 (R5) — bass, treble, reverb and compressor of this clip (Effects tab).</summary>
        public AudioSettingsData AudioFx { get; set; }

        /// <summary>
        /// v2.0 — how this clip sits over the picture below when it is on a layer track (Video 2):
        /// position, size, opacity, green/blue screen, own sound. Null = full frame, solid.
        /// </summary>
        public LayerSettings Layer { get; set; }

        /// <summary>v2.0 — a colour look-up table (.cube) applied to this clip in the render; null = none.</summary>
        public string LutPath { get; set; }

        /// <summary>v2.0 — an object followed through this clip, and what is done with it (blur, pixelate…); null = none.</summary>
        public TrackingData Tracking { get; set; }

        /// <summary>v2.0 — this clip is part of a multicam clip (its id); null = no.</summary>
        public string MulticamId { get; set; }
        /// <summary>v2.0 — which camera (1 … 9) this multicam part shows; 0 = the multicam sound.</summary>
        public int MulticamAngle { get; set; }

        /// <summary>
        /// v2.0 — a compound clip: the clips it was made of (their FixedPosition is relative to
        /// the compound's start). Break it apart to get them back; null = an ordinary clip.
        /// </summary>
        public List<TimelineItem> CompoundItems { get; set; }

        /// <summary>The saved picture description at a position inside the clip, or null.</summary>
        public FrameDescription DescriptionAt(double positionInClip) =>
            FrameDescriptions?.FirstOrDefault(d => positionInClip >= d.Start - 0.001 && positionInClip < d.End);

        [JsonIgnore]
        public string TrackName => (TrackSet.IsAudio(TrackIndex) ? "🎵 " : "🎬 ") + TrackSet.Name(TrackIndex);   // v2.0: unlimited, named tracks

        // v1.4: extensions compared case-insensitively — phones and cameras write ".MP4",
        // ".JPG", ".MOV"; those clips were treated as "unknown" (not video, not image)
        // and skipped by the render and by most commands.
        private string Ext => System.IO.Path.GetExtension(Path ?? "").ToLowerInvariant();

        [JsonIgnore]
        public string TypeIcon
        {
            get
            {
                if (IsAudio) return "🎵";
                if (IsImage) return "🖼️";
                if (IsVideo) return "🎬";
                return "📄";
            }
        }

        [JsonIgnore]
        public bool IsAudio => Type == "Audio" || Ext is ".mp3" or ".wav" or ".m4a" or ".flac" or ".ogg" or ".aac" or ".wma";   // v2.0: also the detached sound of a video

        [JsonIgnore]
        public bool IsImage => Ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";

        [JsonIgnore]
        public bool IsVideo => Type != "Audio" && Ext is ".mp4" or ".avi" or ".mov" or ".mkv" or ".webm" or ".m4v" or ".wmv";

        [JsonIgnore]
        public bool IsVideoTrack => TrackSet.IsVideo(TrackIndex);

        [JsonIgnore]
        public bool IsAudioTrack => TrackSet.IsAudio(TrackIndex);

        public override string ToString()
        {
            string trackInfo = TrackName + " ";

            string typeInfo = IsAudio ? "Audio" : (IsVideo ? "Video" : "Image");
            string volumeInfo = IsAudio ? string.Format(L("models_volume_sfx"), Volume) : "";
            string audioDescInfo = !string.IsNullOrEmpty(AudioDescription) ? $", description: {AudioDescription}" : "";
            string textOverlayInfo = TextOverlay.Enabled && !string.IsNullOrEmpty(TextOverlay.Text) ? $", text: {TextOverlay.Text}" : "";

            return $"{trackInfo}{Index}. {typeInfo}: {Name} (duration: {TimeSpan.FromSeconds(Duration):mm\\:ss}{volumeInfo}{audioDescInfo}{textOverlayInfo})";
        }

        public void AddKeyframe(AnimationKeyframe keyframe)
        {
            Keyframes.Add(keyframe);
            Keyframes = Keyframes.OrderBy(k => k.Time).ToList();
        }

        public void RemoveKeyframeAt(double time)
        {
            var kf = Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.01);
            if (kf != null) Keyframes.Remove(kf);
        }

        [JsonIgnore]
        public string AccessibilityText
        {
            get
            {
                string tip = IsAudio ? "Audio" : (IsImage ? L("model_image2") : "Video");
                string opis = !string.IsNullOrEmpty(AudioDescription) ? string.Format(L("model_desc"), AudioDescription) : "";
                string tekst = TextOverlay.Enabled && !string.IsNullOrEmpty(TextOverlay.Text) ? string.Format(L("model_txt_on"), TextOverlay.Text) : "";
                string track = TrackName;
                return string.Format(L("model_acc_text"), track, Index, tip, Name,
                    TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss"),
                    TimeSpan.FromSeconds(Start).ToString(@"mm\:ss"),
                    TimeSpan.FromSeconds(End).ToString(@"mm\:ss"),
                    Volume, opis, tekst);
            }
        }
    }

    public class SubtitleItem
    {
        public string Text { get; set; } = "";
        public double Start { get; set; } = 0;
        public double End { get; set; } = 5.0;

        /// <summary>
        /// v1.4 — optional karaoke timing: each word of Text with its time relative to Start.
        /// When present (and karaoke is on), the render colours each word as it is sung.
        /// Null for ordinary subtitles.
        /// </summary>
        public List<KaraokeWord> Words { get; set; }
    }

    /// <summary>v1.4 — one sung word for karaoke subtitles.</summary>
    public class KaraokeWord
    {
        public string Text { get; set; } = "";
        /// <summary>Seconds from the subtitle's Start to the moment the word is sung.</summary>
        public double Offset { get; set; }
        /// <summary>How long the word lasts, in seconds.</summary>
        public double Duration { get; set; }
    }

    public class VideoEffectData
    {
        public double Brightness { get; set; } = 0;
        public double Contrast { get; set; } = 0;
        public double Blur { get; set; } = 0;
        public double Speed { get; set; } = 1.0;

        // v2.0 — colour correction (all 0 = unchanged; -100 … 100 unless noted)
        public double Saturation { get; set; } = 0;
        /// <summary>Positive = warmer (orange), negative = cooler (blue).</summary>
        public double Temperature { get; set; } = 0;
        /// <summary>Positive = towards magenta, negative = towards green.</summary>
        public double Tint { get; set; } = 0;
        /// <summary>Mid-tones: positive = lighter, negative = darker.</summary>
        public double Gamma { get; set; } = 0;
        public double Shadows { get; set; } = 0;
        public double Highlights { get; set; } = 0;
        /// <summary>0 … 100, darker corners.</summary>
        public double Vignette { get; set; } = 0;
        /// <summary>0 … 100.</summary>
        public double Sharpen { get; set; } = 0;

        // v2.0 — picture
        /// <summary>Steadies a shaky clip (two passes in the render).</summary>
        public bool Stabilize { get; set; } = false;
        /// <summary>1 … 10, how shaky the clip is (more = stronger smoothing).</summary>
        public double StabilizeStrength { get; set; } = 5;
        /// <summary>Slow motion with new frames calculated between the real ones (optical flow).</summary>
        public bool SmoothSlowMotion { get; set; } = false;

        // v2.0 — colour balance per tonal range (like colour wheels), -100 … 100:
        // R = red (+) ↔ cyan (−), G = green (+) ↔ magenta (−), B = blue (+) ↔ yellow (−)
        public double ShadowR { get; set; }
        public double ShadowG { get; set; }
        public double ShadowB { get; set; }
        public double MidR { get; set; }
        public double MidG { get; set; }
        public double MidB { get; set; }
        public double HighR { get; set; }
        public double HighG { get; set; }
        public double HighB { get; set; }

        // v2.0 — one colour only (a qualifier): "" = off, or r, y, g, c, b, m
        public string SelColor { get; set; } = "";
        /// <summary>Hue shift of that colour in degrees (-180 … 180).</summary>
        public double SelHue { get; set; }
        public double SelSaturation { get; set; }
        public double SelBrightness { get; set; }
        /// <summary>Keep only that colour; everything else becomes black and white.</summary>
        public bool SelKeepOnly { get; set; }
        /// <summary>v2.0 — a colour picked from the picture ("#RRGGBB"); with "keep only", exactly that colour stays.</summary>
        public string SelPicked { get; set; } = "";
        /// <summary>v2.0 — how close to the picked colour a pixel must be, 5 … 60.</summary>
        public double SelSimilarity { get; set; } = 25;

        // v2.0 — power window: the colour correction only inside (or outside) one area.
        /// <summary>"" = off, Oval, Rectangle, Top, Bottom, Left, Right (the last four are soft graduated areas).</summary>
        public string PwShape { get; set; } = "";
        /// <summary>Centre and size in percent of the picture; Feather = the soft edge in percent of the size.</summary>
        public double PwX { get; set; } = 50;
        public double PwY { get; set; } = 50;
        public double PwW { get; set; } = 50;
        public double PwH { get; set; } = 50;
        public double PwFeather { get; set; } = 20;
        /// <summary>true = correct everything except the area.</summary>
        public bool PwInvert { get; set; }
        public bool HasWindow => !string.IsNullOrEmpty(PwShape) && PowerWindow.Shapes.Contains(PwShape);

        // v2.0 — curves: the output at 0, 25, 50, 75 and 100 % input, as "0 25 50 75 100"
        // (empty = straight). All = every channel together; R, G, B = one colour channel.
        public string CurveAll { get; set; } = "";
        public string CurveR { get; set; } = "";
        public string CurveG { get; set; } = "";
        public string CurveB { get; set; } = "";

        /// <summary>v2.0 — a speed ramp: the speed changes inside the clip (null / empty = the steady Speed).</summary>
        public List<SpeedPoint> SpeedPoints { get; set; }

        public bool HasCurves => !Curves.IsStraight(CurveAll) || !Curves.IsStraight(CurveR) || !Curves.IsStraight(CurveG) || !Curves.IsStraight(CurveB);

        public VideoEffectData Clone()
        {
            var c = (VideoEffectData)MemberwiseClone();
            c.SpeedPoints = SpeedPoints?.Select(p => new SpeedPoint { Time = p.Time, Speed = p.Speed }).ToList();   // v2.0
            return c;
        }

        public bool HasBalance => ShadowR != 0 || ShadowG != 0 || ShadowB != 0 || MidR != 0 || MidG != 0 || MidB != 0 ||
                                  HighR != 0 || HighG != 0 || HighB != 0;

        /// <summary>True when any colour setting differs from the original.</summary>
        public bool HasColor => Saturation != 0 || Temperature != 0 || Tint != 0 || Gamma != 0 ||
                                Shadows != 0 || Highlights != 0 || Vignette != 0 || Sharpen != 0 ||
                                HasBalance || !string.IsNullOrEmpty(SelColor) || HasCurves;
    }

    public class AudioSettingsData
    {
        public double BassBoost { get; set; } = 0;
        public double TrebleBoost { get; set; } = 0;
        public double ReverbAmount { get; set; } = 0;
        public bool CompressorEnabled { get; set; } = false;

        // v2.0 — three-band equaliser in dB (-12 … 12)
        public double LowGain { get; set; } = 0;
        public double MidGain { get; set; } = 0;
        public double HighGain { get; set; } = 0;
        /// <summary>0 = off, 1 = light, 2 = strong.</summary>
        public int NoiseReduction { get; set; } = 0;
        /// <summary>Clearer speech: low rumble removed, presence raised, levels evened.</summary>
        public bool VoiceEnhance { get; set; } = false;
        /// <summary>Seconds of fade at the start and the end of the clip's sound.</summary>
        public double FadeIn { get; set; } = 0;
        public double FadeOut { get; set; } = 0;

        public AudioSettingsData Clone() => (AudioSettingsData)MemberwiseClone();
    }

    public class ExportSettingsData
    {
        public string Format { get; set; } = "MP4";
        /// <summary>Low, Medium, High or Maximum (v2.0).</summary>
        public string Quality { get; set; } = "Medium";
        public bool ExportAudioOnly { get; set; } = false;

        /// <summary>v2.0 — H264, H265, H265_10 (Main10), ProRes, DNxHR, DNxHR10 (HQX) or FFV1. ProRes and DNxHR are written as .mov, FFV1 as .mkv.</summary>
        public string VideoCodec { get; set; } = "H264";

        /// <summary>v2.0 — "Auto" (the frame rate of the first video clip) or 24, 25, 30, 50, 60…</summary>
        public string FrameRate { get; set; } = "Auto";

        /// <summary>v2.0 — a soft fade from and to black at the start and the end.</summary>
        public bool FadeInOut { get; set; } = true;

        /// <summary>v2.0 — the warm colour look of the AI music videos; null = only for AI creator projects.</summary>
        public bool? WarmLook { get; set; } = null;
    }

    public class AISubtitle
    {
        public string Text { get; set; } = "";
        public double Start { get; set; } = 0;
        public double End { get; set; } = 0;
        public double Confidence { get; set; } = 0;
    }

    // TranscriptionResult i TranscriptionSegment definisani su u AITranscription.cs kao ugnjezdene klase.
    // Ovdje su bili duplirana definicija — uzrokovala CS0101 build error. Obrisano.

    public class AILayoutResponse
    {
        public List<AILayoutItem> raspored { get; set; }
    }

    public class AILayoutItem
    {
        public int animacija_index { get; set; }
        public double pocetak { get; set; }
        public double kraj { get; set; }
        public string razlog { get; set; }
    }

    public static class AnimationEffects
    {
        public static string GetFFmpegFilter(TimelineItem item, double fps = 30)
        {
            if (item.Keyframes == null || item.Keyframes.Count < 2) return "";

            var sorted = item.Keyframes.OrderBy(k => k.Time).ToList();
            string filter = "";

            if (sorted.Any(k => Math.Abs(k.Zoom - 1.0) > 0.01) || sorted.Any(k => k.X != 0 || k.Y != 0))
            {
                filter += $"zoompan=z='if(eq(on,0),{sorted[0].Zoom}',";
                for (int i = 1; i < sorted.Count; i++)
                {
                    double startTime = sorted[i - 1].Time;
                    double endTime = sorted[i].Time;
                    double startZoom = sorted[i - 1].Zoom;
                    double endZoom = sorted[i].Zoom;
                    double startX = sorted[i - 1].X;
                    double endX = sorted[i].X;
                    double startY = sorted[i - 1].Y;
                    double endY = sorted[i].Y;

                    filter += $"if(between(t,{startTime},{endTime}),{startZoom}+({endZoom}-{startZoom})*(t-{startTime})/({endTime}-{startTime}),";
                }
                filter += $"{sorted.Last().Zoom}" + new string(')', sorted.Count) + $"'):d=1:fps={fps}:s=1920x1080,";
            }

            if (sorted.Any(k => Math.Abs(k.Rotation) > 0.01))
            {
                filter += $"rotate=a='{sorted[0].Rotation}*PI/180'";
                for (int i = 1; i < sorted.Count; i++)
                {
                    filter += $"+({sorted[i].Rotation}-{sorted[i - 1].Rotation})*PI/180*(t-{sorted[i - 1].Time})/({sorted[i].Time}-{sorted[i - 1].Time})";
                }
                filter += "':ow=hypot(iw,ih):oh=ow,";
            }

            if (sorted.Any(k => Math.Abs(k.Opacity - 1.0) > 0.01))
            {
                filter += $"fade=type=in:start_time=0:duration={sorted.Last().Time},fade=type=out:start_time={sorted.Last().Time - 1}:duration=1,";
            }

            return filter.TrimEnd(',');
        }
    }
}