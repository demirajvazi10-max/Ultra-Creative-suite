using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UltraAccessibleKit.Mvvm;
using UltraCaptions.Models;
using UltraCaptions.Services;

namespace UltraCaptions.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public ObservableCollection<CaptionEntry> Captions { get; } = new();

        private CaptionEntry? _selectedCaption;
        public CaptionEntry? SelectedCaption
        {
            get => _selectedCaption;
            set => SetField(ref _selectedCaption, value);
        }

        private string _mediaPath = "";
        public string MediaPath
        {
            get => _mediaPath;
            set => SetField(ref _mediaPath, value);
        }

        private string _statusMessage = "Ready.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetField(ref _isBusy, value);
        }

        public RelayCommand TranscribeCommand { get; }
        public RelayCommand NewCaptionCommand { get; }
        public RelayCommand DeleteCaptionCommand { get; }

        public MainViewModel()
        {
            TranscribeCommand = new RelayCommand(async _ => await TranscribeAsync(), _ => !IsBusy && !string.IsNullOrEmpty(MediaPath));
            NewCaptionCommand = new RelayCommand(_ => AddNewCaption());
            DeleteCaptionCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedCaption != null);
        }

        public void AddNewCaption(TimeSpan? start = null, TimeSpan? end = null)
        {
            var entry = new CaptionEntry
            {
                Start = start ?? TimeSpan.Zero,
                End = end ?? TimeSpan.Zero,
                Text = ""
            };
            Captions.Add(entry);
            SelectedCaption = entry;
        }

        private void DeleteSelected()
        {
            if (SelectedCaption == null) return;
            Captions.Remove(SelectedCaption);
            SelectedCaption = null;
        }

        private async Task TranscribeAsync()
        {
            if (string.IsNullOrEmpty(MediaPath)) return;

            IsBusy = true;
            StatusMessage = "Transcribing with Whisper — this can take a while for longer files...";
            try
            {
                var lines = await AITranscription.TranscribeAsync(MediaPath);
                Captions.Clear();
                foreach (var line in lines)
                    Captions.Add(line);

                StatusMessage = $"Transcription complete: {Captions.Count} lines. Review and adjust timing as needed.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Transcription failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void ImportSrt(string path)
        {
            Captions.Clear();
            foreach (var line in SrtService.Import(path))
                Captions.Add(line);
            StatusMessage = $"Imported {Captions.Count} lines from {System.IO.Path.GetFileName(path)}.";
        }

        public void ExportSrt(string path)
        {
            SrtService.Export(path, Captions);
            StatusMessage = $"Exported {Captions.Count} lines to {System.IO.Path.GetFileName(path)}.";
        }
    }
}
