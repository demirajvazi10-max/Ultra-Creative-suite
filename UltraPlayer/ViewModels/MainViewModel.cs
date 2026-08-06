using System.Collections.ObjectModel;
using UltraAccessibleKit.Mvvm;
using UltraPlayer.Models;

namespace UltraPlayer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public ObservableCollection<PlayerTrack> Playlist { get; } = new();

        private PlayerTrack? _selectedTrack;
        public PlayerTrack? SelectedTrack
        {
            get => _selectedTrack;
            set => SetField(ref _selectedTrack, value);
        }

        private string _statusMessage = "Add files to build a playlist.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        private double _currentSpeed = 1.0;
        public double CurrentSpeed
        {
            get => _currentSpeed;
            set => SetField(ref _currentSpeed, value);
        }

        private string _sleepTimerLabel = "Sleep timer: off";
        public string SleepTimerLabel
        {
            get => _sleepTimerLabel;
            set => SetField(ref _sleepTimerLabel, value);
        }

        public RelayCommand RemoveTrackCommand { get; }

        public MainViewModel()
        {
            RemoveTrackCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedTrack != null);
        }

        public void AddTrack(string filePath)
        {
            var track = new PlayerTrack(filePath);
            Playlist.Add(track);
            if (Playlist.Count == 1)
                SelectedTrack = track;
        }

        private void RemoveSelected()
        {
            if (SelectedTrack == null) return;
            int index = Playlist.IndexOf(SelectedTrack);
            Playlist.Remove(SelectedTrack);
            SelectedTrack = Playlist.Count == 0 ? null
                : Playlist[System.Math.Min(index, Playlist.Count - 1)];
        }
    }
}
