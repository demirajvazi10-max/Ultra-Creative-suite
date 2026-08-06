using System;
using System.IO;
using System.Media;
using NAudio.Wave;
using UltraAccessibleKit.Mvvm;

namespace UltraRecord.Models
{
    /// <summary>
    /// One recording track: its own input device, its own output WAV file,
    /// and its own clipping feedback. Multiple tracks record simultaneously,
    /// each independently - e.g. one per podcast participant.
    ///
    /// Clipping feedback is audio, not a visual meter: visual VU meters are
    /// inherently inaccessible. Instead, a short system beep plays the
    /// moment a track clips (rate-limited so it doesn't spam), and the
    /// track's status text updates with AutomationProperties.LiveSetting so
    /// a screen reader announces it too, without needing focus on the track.
    /// </summary>
    public class RecordingTrack : ViewModelBase
    {
        private const double ClipThreshold = 0.95;      // ~-0.4 dBFS
        private static readonly TimeSpan ClipBeepCooldown = TimeSpan.FromMilliseconds(1500);

        private string _name = "Track 1";
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private int _deviceIndex;
        public int DeviceIndex
        {
            get => _deviceIndex;
            set => SetField(ref _deviceIndex, value);
        }

        private bool _isArmed = true;
        public bool IsArmed
        {
            get => _isArmed;
            set => SetField(ref _isArmed, value);
        }

        private bool _isClipping;
        public bool IsClipping
        {
            get => _isClipping;
            set => SetField(ref _isClipping, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public string? OutputFilePath { get; private set; }

        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private DateTime _lastClipBeep = DateTime.MinValue;

        public void StartRecording(string outputFolder)
        {
            OutputFilePath = Path.Combine(outputFolder, SanitizeFileName(Name) + ".wav");

            _waveIn = new WaveInEvent
            {
                DeviceNumber = DeviceIndex,
                WaveFormat = new WaveFormat(44100, 16, 1)
            };

            _writer = new WaveFileWriter(OutputFilePath, _waveIn.WaveFormat);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            _waveIn.StartRecording();
            StatusText = "Recording";
            IsClipping = false;
        }

        public void StopRecording()
        {
            _waveIn?.StopRecording();
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _writer?.Dispose();
            _writer = null;
            _waveIn?.Dispose();
            _waveIn = null;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusText = e.Exception != null ? $"Error: {e.Exception.Message}" : "Stopped";
                IsClipping = false;
            });
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);

            short peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                short abs = sample < 0 ? (short)-Math.Max(sample, (short)(short.MinValue + 1)) : sample;
                if (abs > peak) peak = abs;
            }

            bool clipping = (peak / 32768.0) > ClipThreshold;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (clipping != IsClipping)
                {
                    IsClipping = clipping;
                    StatusText = clipping ? "Clipping!" : "Recording";
                }
            });

            if (clipping && DateTime.Now - _lastClipBeep > ClipBeepCooldown)
            {
                _lastClipBeep = DateTime.Now;
                SystemSounds.Exclamation.Play();
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Track" : name.Trim();
        }
    }
}
