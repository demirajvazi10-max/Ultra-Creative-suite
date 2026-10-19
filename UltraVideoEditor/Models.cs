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

        /// <summary>The saved picture description at a position inside the clip, or null.</summary>
        public FrameDescription DescriptionAt(double positionInClip) =>
            FrameDescriptions?.FirstOrDefault(d => positionInClip >= d.Start - 0.001 && positionInClip < d.End);

        [JsonIgnore]
        public string TrackName => TrackIndex switch
        {
            0 => "🎬 Video 1",
            1 => "🎬 Video 2",
            2 => "🎵 Audio 1",
            3 => "🎵 Audio 2",
            _ => $"Track {TrackIndex + 1}"
        };

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
        public bool IsAudio => Ext is ".mp3" or ".wav" or ".m4a" or ".flac" or ".ogg" or ".aac" or ".wma";

        [JsonIgnore]
        public bool IsImage => Ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";

        [JsonIgnore]
        public bool IsVideo => Ext is ".mp4" or ".avi" or ".mov" or ".mkv" or ".webm" or ".m4v" or ".wmv";

        [JsonIgnore]
        public bool IsVideoTrack => TrackIndex == 0 || TrackIndex == 1;

        [JsonIgnore]
        public bool IsAudioTrack => TrackIndex == 2 || TrackIndex == 3;

        public override string ToString()
        {
            string trackInfo = TrackIndex switch
            {
                0 => "🎬 Video 1 ",
                1 => "🎬 Video 2 ",
                2 => "🎵 Audio 1 ",
                3 => "🎵 Audio 2 ",
                _ => $"Track {TrackIndex + 1} "
            };

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
    }

    public class AudioSettingsData
    {
        public double BassBoost { get; set; } = 0;
        public double TrebleBoost { get; set; } = 0;
        public double ReverbAmount { get; set; } = 0;
        public bool CompressorEnabled { get; set; } = false;
    }

    public class ExportSettingsData
    {
        public string Format { get; set; } = "MP4";
        public string Quality { get; set; } = "Medium";
        public bool ExportAudioOnly { get; set; } = false;
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