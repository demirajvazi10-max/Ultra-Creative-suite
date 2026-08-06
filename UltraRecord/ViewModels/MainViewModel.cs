using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UltraAccessibleKit.Mvvm;
using UltraRecord.Models;
using UltraRecord.Services;

namespace UltraRecord.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public ObservableCollection<RecordingTrack> Tracks { get; } = new();

        public List<DeviceOption> InputDevices { get; }

        private RecordingTrack? _selectedTrack;
        public RecordingTrack? SelectedTrack
        {
            get => _selectedTrack;
            set => SetField(ref _selectedTrack, value);
        }

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            set => SetField(ref _isRecording, value);
        }

        private string _outputFolder = "";
        public string OutputFolder
        {
            get => _outputFolder;
            set => SetField(ref _outputFolder, value);
        }

        private string _statusMessage = "Ready. Add at least one track and choose an output folder to begin.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public RelayCommand AddTrackCommand { get; }
        public RelayCommand RemoveTrackCommand { get; }
        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }

        public MainViewModel()
        {
            InputDevices = AudioDeviceService.GetInputDevices();

            AddTrackCommand = new RelayCommand(_ => AddTrack());
            RemoveTrackCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedTrack != null && !IsRecording);
            StartCommand = new RelayCommand(_ => StartAll(), _ => CanStart());
            StopCommand = new RelayCommand(_ => StopAll(), _ => IsRecording);

            AddTrack();
        }

        private bool CanStart() =>
            !IsRecording && Tracks.Any(t => t.IsArmed) && !string.IsNullOrWhiteSpace(OutputFolder);

        public void AddTrack()
        {
            var track = new RecordingTrack { Name = $"Track {Tracks.Count + 1}" };
            Tracks.Add(track);
            SelectedTrack = track;
        }

        private void RemoveSelected()
        {
            if (SelectedTrack == null) return;
            Tracks.Remove(SelectedTrack);
            SelectedTrack = Tracks.FirstOrDefault();
        }

        private void StartAll()
        {
            foreach (var track in Tracks.Where(t => t.IsArmed))
                track.StartRecording(OutputFolder);

            IsRecording = true;
            StatusMessage = "Recording all armed tracks...";
        }

        private void StopAll()
        {
            foreach (var track in Tracks)
                track.StopRecording();

            IsRecording = false;
            StatusMessage = $"Stopped. Files saved to: {OutputFolder}";
        }
    }
}
