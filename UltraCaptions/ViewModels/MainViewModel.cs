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

        private string _statusMessage = L.T("Ready.");
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
            StatusMessage = L.T("Transcribing with Whisper — this can take a while for longer files...");
            try
            {
                var lines = await AITranscription.TranscribeAsync(MediaPath);
                Captions.Clear();
                foreach (var line in lines)
                    Captions.Add(line);

                StatusMessage = L.F("Transcription complete: {0} lines. Review and adjust timing as needed.", Captions.Count);
            }
            catch (Exception ex)
            {
                StatusMessage = L.F("Transcription failed: {0}", ex.Message);
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
            StatusMessage = L.F("Imported {0} lines from {1}.", Captions.Count, System.IO.Path.GetFileName(path));
        }

        public void ExportSrt(string path)
        {
            SrtService.Export(path, Captions);
            StatusMessage = L.F("Exported {0} lines to {1}.", Captions.Count, System.IO.Path.GetFileName(path));
        }
    }
}
