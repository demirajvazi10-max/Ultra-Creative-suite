using System.IO;
using UltraAccessibleKit.Mvvm;

namespace UltraPlayer.Models
{
    public class PlayerTrack : ViewModelBase
    {
        public string FilePath { get; }
        public string Title { get; }

        public PlayerTrack(string filePath)
        {
            FilePath = filePath;
            Title = Path.GetFileNameWithoutExtension(filePath);
        }

        private bool _isCurrent;
        /// <summary>True while this track is the one loaded in the player (playing or paused).</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetField(ref _isCurrent, value);
        }
    }
}
