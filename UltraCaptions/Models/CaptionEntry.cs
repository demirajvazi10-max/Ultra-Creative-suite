using System;
using UltraAccessibleKit.Mvvm;

namespace UltraCaptions.Models
{
    /// <summary>
    /// A single caption/subtitle line. Used the same way whether it came from
    /// Whisper auto-transcription or was typed and timed by hand — both paths
    /// produce the same CaptionEntry, so the rest of the app (list, editing,
    /// export) doesn't need to know or care which one made it.
    /// </summary>
    public class CaptionEntry : ViewModelBase
    {
        private TimeSpan _start;
        private TimeSpan _end;
        private string _text = "";

        public TimeSpan Start
        {
            get => _start;
            set
            {
                if (SetField(ref _start, value))
                {
                    OnPropertyChanged(nameof(DisplayLabel));
                    OnPropertyChanged(nameof(StartDisplay));
                }
            }
        }

        public TimeSpan End
        {
            get => _end;
            set
            {
                if (SetField(ref _end, value))
                {
                    OnPropertyChanged(nameof(DisplayLabel));
                    OnPropertyChanged(nameof(EndDisplay));
                }
            }
        }

        public string Text
        {
            get => _text;
            set { if (SetField(ref _text, value)) OnPropertyChanged(nameof(DisplayLabel)); }
        }

        /// <summary>
        /// What the screen reader (and the sighted list view) actually reads for
        /// this row — start, end, and text together in one announcement, so
        /// nothing requires moving focus between separate columns to get the
        /// full picture of a line.
        /// </summary>
        public string DisplayLabel => $"{Format(Start)} to {Format(End)}: {Text}";

        // Separate formatted strings for the grid columns, so XAML can bind
        // directly without relying on fragile inline TimeSpan format strings.
        public string StartDisplay => Format(Start);
        public string EndDisplay => Format(End);

        private static string Format(TimeSpan t) => t.ToString(@"mm\:ss\.ff");
    }
}
