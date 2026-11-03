using System.IO;
using NAudio.Wave;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// Snimanje sa mikrofona direktno u WAV fajl (44.1 kHz, 16-bit, stereo).
    /// Koristi Windows podrazumevani ulazni uređaj (WAVE_MAPPER, DeviceNumber = -1),
    /// isto ono što je izabrano u Windows podešavanjima zvuka — bez posebnog
    /// dijaloga za izbor uređaja.
    ///
    /// Usput meri najglasniji vrh (peak) snimka, da bi editor posle snimanja mogao
    /// da upozori ako je snimak skoro nečujan (mikrofon isključen / pogrešan uređaj)
    /// ili ako je preglasan (izobličen). Slepom korisniku je to jedini način da sazna
    /// da nešto nije u redu, jer ne vidi VU metar.
    /// </summary>
    public sealed class AudioRecorder : IDisposable
    {
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private readonly object _lock = new();
        private TaskCompletionSource<bool>? _stoppedTcs;

        public string? FilePath { get; private set; }
        public bool IsRecording { get; private set; }
        public float PeakLevel { get; private set; }   // 0..1 (1 = 0 dBFS)
        public double RecordedSeconds { get; private set; }
        public string? LastError { get; private set; }

        public static bool HasInputDevice()
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch { return false; }
        }

        public static string DefaultRecordingsFolder()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Ultra Audio Editor", "Recordings");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Start(string filePath, int sampleRate = 44100)
        {
            if (IsRecording) return;
            FilePath = filePath;
            PeakLevel = 0;
            RecordedSeconds = 0;
            LastError = null;

            var format = new WaveFormat(sampleRate, 16, 2);
            _waveIn = new WaveInEvent
            {
                DeviceNumber = -1,          // WAVE_MAPPER = Windows podrazumevani mikrofon
                WaveFormat = format,
                BufferMilliseconds = 50
            };
            _writer = new WaveFileWriter(filePath, format);
            _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _waveIn.DataAvailable += OnData;
            _waveIn.RecordingStopped += (_, e) =>
            {
                lock (_lock)
                {
                    try { _writer?.Dispose(); } catch { }
                    _writer = null;
                }
                if (e.Exception != null) LastError = e.Exception.Message;
                _stoppedTcs?.TrySetResult(true);
            };

            _waveIn.StartRecording();
            IsRecording = true;
            DebugLog.Write($"Recorder: started -> {filePath}");
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                RecordedSeconds = _writer.TotalTime.TotalSeconds;
            }
            // 16-bit little-endian PCM -> peak
            float peak = PeakLevel;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                float a = Math.Abs(s / 32768f);
                if (a > peak) peak = a;
            }
            PeakLevel = peak;
        }

        /// <summary>Zaustavlja snimanje i čeka da se WAV fajl potpuno zatvori.</summary>
        public async Task StopAsync()
        {
            if (!IsRecording || _waveIn == null) return;
            IsRecording = false;
            try { _waveIn.StopRecording(); } catch (Exception ex) { LastError = ex.Message; _stoppedTcs?.TrySetResult(true); }
            if (_stoppedTcs != null)
                await Task.WhenAny(_stoppedTcs.Task, Task.Delay(3000));
            try { _waveIn.Dispose(); } catch { }
            _waveIn = null;
            lock (_lock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
            DebugLog.Write($"Recorder: stopped, {RecordedSeconds:F2}s, peak {PeakLevel:F3}, error: {LastError ?? "none"}");
        }

        public void Dispose()
        {
            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            lock (_lock) { try { _writer?.Dispose(); } catch { } _writer = null; }
        }
    }
}
