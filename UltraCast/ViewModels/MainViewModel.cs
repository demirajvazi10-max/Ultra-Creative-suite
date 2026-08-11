using System;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using UltraAccessibleKit.Mvvm;
using UltraCast.Models;
using UltraCast.Services;

namespace UltraCast.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly RecordingCoordinator _coordinator = new();

        // Captured at construction time, which always happens on the UI
        // thread - lets background-thread events (coordinator.Error, the
        // audio/video capture services) safely update bound properties
        // without throwing "the calling thread cannot access this object"
        // cross-thread exceptions.
        private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            set => SetField(ref _isRecording, value);
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set => SetField(ref _isPaused, value);
        }

        private string _outputFolder = "";
        public string OutputFolder
        {
            get => _outputFolder;
            set => SetField(ref _outputFolder, value);
        }

        private bool _captureSystemAudio = true;
        public bool CaptureSystemAudio
        {
            get => _captureSystemAudio;
            set => SetField(ref _captureSystemAudio, value);
        }

        private bool _captureMicrophone = true;
        public bool CaptureMicrophone
        {
            get => _captureMicrophone;
            set => SetField(ref _captureMicrophone, value);
        }

        private string _statusMessage =
            "Ready. Choose an output folder, then start recording (Ctrl+Alt+R works from any window).";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        private string _lastSavedFile = "";
        public string LastSavedFile
        {
            get => _lastSavedFile;
            set => SetField(ref _lastSavedFile, value);
        }

        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand PauseCommand { get; }

        public MainViewModel()
        {
            StartCommand = new RelayCommand(async _ => await StartAsync(), _ => CanStart());
            StopCommand = new RelayCommand(async _ => await StopAsync(), _ => IsRecording);
            PauseCommand = new RelayCommand(_ => TogglePause(), _ => IsRecording);

            // ScreenCaptureService/AudioLoopbackMixer raise this from
            // whatever background thread hit the problem - always hop back
            // to the UI thread before touching bound properties.
            _coordinator.Error += msg => RunOnUi(() =>
            {
                AppLogger.Log("MainViewModel: coordinator reported error - " + msg);
                StatusMessage = "Problem: " + msg;
                SystemSounds.Hand.Play();
            });
        }

        /// <summary>Marshals an action to the UI thread that constructed this ViewModel, or runs it inline if already there / no context was captured.</summary>
        private void RunOnUi(Action action)
        {
            if (_uiContext != null && SynchronizationContext.Current != _uiContext)
                _uiContext.Post(_ => action(), null);
            else
                action();
        }

        private bool CanStart() => !IsRecording && !string.IsNullOrWhiteSpace(OutputFolder);

        private async Task StartAsync()
        {
            try
            {
                var options = new RecordingOptions
                {
                    OutputFolder = OutputFolder,
                    CaptureSystemAudio = CaptureSystemAudio,
                    CaptureMicrophone = CaptureMicrophone
                };

                _coordinator.Start(options);
                IsRecording = true;
                IsPaused = false;
                StatusMessage = "Recording. Press Ctrl+Alt+R from anywhere to stop, Ctrl+Alt+P to pause.";

                // Two short beeps mean "recording started" - a non-visual
                // confirmation, since the whole point is that the user may
                // not be looking at (or have) this window in focus.
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                AppLogger.Log("MainViewModel: StartAsync failed - " + ex);
                StatusMessage = "Could not start recording: " + ex.Message + $"  (see log: {AppLogger.LogFilePath})";
                SystemSounds.Hand.Play();
            }
        }

        private async Task StopAsync()
        {
            try
            {
                StatusMessage = "Finishing up - combining video and audio...";
                var finalPath = await _coordinator.StopAsync();
                IsRecording = false;
                IsPaused = false;
                LastSavedFile = finalPath;
                StatusMessage = $"Saved: {finalPath}";
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                AppLogger.Log("MainViewModel: StopAsync failed - " + ex);
                IsRecording = false;
                StatusMessage = "Recording stopped, but there was a problem saving it: " + ex.Message + $"  (see log: {AppLogger.LogFilePath})";
                SystemSounds.Hand.Play();
            }
        }

        private void TogglePause()
        {
            _coordinator.TogglePause();
            IsPaused = _coordinator.IsPaused;
            StatusMessage = IsPaused ? "Paused. Press Ctrl+Alt+P to resume." : "Recording resumed.";
            SystemSounds.Beep.Play();
        }

        /// <summary>Called by MainWindow's global-hotkey handlers.</summary>
        public async void HandleToggleHotkey()
        {
            if (IsRecording)
                await StopAsync();
            else if (StartCommand.CanExecute(null))
                await StartAsync();
        }

        public void HandlePauseHotkey()
        {
            if (PauseCommand.CanExecute(null))
                TogglePause();
        }
    }
}
