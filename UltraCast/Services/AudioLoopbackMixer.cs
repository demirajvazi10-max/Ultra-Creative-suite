using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi; // WasapiCapture lives here in the NAudio version this project references (WasapiLoopbackCapture stayed resolvable via NAudio.Wave, which is why only this one type errored)
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UltraCast.Services
{
    /// <summary>
    /// Records two sources at once and mixes them into one WAV:
    ///   1) System output via WASAPI loopback ("what you hear") - this is
    ///      the important one for Ultra Cast, because JAWS/NVDA speech is
    ///      just audio going to the default output device like anything
    ///      else. No screen-reader-specific API or hook is needed - loopback
    ///      capture hears the screen reader the same way it hears a video
    ///      or a beep.
    ///   2) The default microphone, for spoken narration layered on top.
    /// Either source can be switched off independently (see RecordingOptions).
    /// </summary>
    public class AudioLoopbackMixer : IDisposable
    {
        private WasapiLoopbackCapture? _loopback;
        private WasapiCapture? _mic;
        private BufferedWaveProvider? _loopbackBuffer;
        private BufferedWaveProvider? _micBuffer;
        private MixingSampleProvider? _mixer;
        private IWaveProvider? _mixedWaveProvider;
        private WaveFileWriter? _writer;
        private CancellationTokenSource? _pumpCts;
        private Task? _pumpTask;

        public event Action<string>? CaptureError;

        /// <summary>Mirrors ScreenCaptureService.IsPaused - while true, the disk-pump loop stops writing (silence gap instead of a frozen frame's audio equivalent).</summary>
        public bool IsPaused { get; set; }

        public void Start(string outputWavPath, bool captureSystemAudio, bool captureMicrophone)
        {
            var sampleProviders = new System.Collections.Generic.List<ISampleProvider>();

            // WaveFormat that everything gets converted to before mixing.
            // 44.1kHz stereo IEEE float is what WasapiLoopbackCapture almost
            // always reports natively, so using it as the common target
            // avoids resampling the (usually more important) system-audio
            // track and only resamples the mic if needed.
            WaveFormat mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

            if (captureSystemAudio)
            {
                try
                {
                    _loopback = new WasapiLoopbackCapture();
                    mixFormat = _loopback.WaveFormat; // prefer the device's real format
                    _loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(5)
                    };
                    _loopback.DataAvailable += (_, e) =>
                        _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _loopback.RecordingStopped += (_, e) =>
                    {
                        if (e.Exception != null) CaptureError?.Invoke("System audio capture stopped: " + e.Exception.Message);
                    };
                }
                catch (Exception ex)
                {
                    AppLogger.Log("AudioLoopbackMixer: could not open system audio loopback - " + ex.Message);
                    _loopback = null;
                    _loopbackBuffer = null;
                }
            }

            if (captureMicrophone)
            {
                try
                {
                    _mic = new WasapiCapture(); // default microphone
                    _micBuffer = new BufferedWaveProvider(_mic.WaveFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(5)
                    };
                    _mic.DataAvailable += (_, e) =>
                        _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _mic.RecordingStopped += (_, e) =>
                    {
                        if (e.Exception != null) CaptureError?.Invoke("Microphone capture stopped: " + e.Exception.Message);
                    };
                }
                catch (Exception ex)
                {
                    // No default microphone, or it's in use by another app -
                    // log it and carry on recording system audio only rather
                    // than failing the whole session.
                    AppLogger.Log("AudioLoopbackMixer: could not open microphone - " + ex.Message);
                    _mic = null;
                    _micBuffer = null;
                }
            }

            if (_loopbackBuffer != null)
                sampleProviders.Add(ToMixFormat(_loopbackBuffer.ToSampleProvider(), mixFormat));
            if (_micBuffer != null)
                sampleProviders.Add(ToMixFormat(_micBuffer.ToSampleProvider(), mixFormat));

            if (sampleProviders.Count == 0)
            {
                // Nothing selected to record - write silence rather than crash,
                // so the muxing step downstream always has an audio track to work with.
                sampleProviders.Add(new SilenceProvider(mixFormat).ToSampleProvider());
            }

            _mixer = new MixingSampleProvider(sampleProviders)
            {
                ReadFully = true // keep producing silence between bursts instead of stalling the writer loop
            };
            _mixedWaveProvider = _mixer.ToWaveProvider16();

            _writer = new WaveFileWriter(outputWavPath, _mixedWaveProvider.WaveFormat);

            AppLogger.Log($"AudioLoopbackMixer: starting - systemAudio={captureSystemAudio}, microphone={captureMicrophone}, format={_mixedWaveProvider.WaveFormat}");

            _loopback?.StartRecording();
            _mic?.StartRecording();

            _pumpCts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpToDisk(_pumpCts.Token));
        }

        /// <summary>
        /// Continuously pulls mixed audio out of the sample-provider chain and
        /// writes it to the WAV file. Runs on its own thread for the whole
        /// recording, independent of the screen-capture loop.
        ///
        /// Paced against a Stopwatch rather than just looping as fast as
        /// possible: with ReadFully=true, MixingSampleProvider.Read() always
        /// returns a full buffer instantly - padding with silence for any
        /// source that doesn't have enough real samples yet rather than
        /// waiting for them. An unthrottled loop therefore races far ahead
        /// of real time, writing mostly silence and diluting whatever real
        /// audio does arrive into a WAV file that's both longer than the
        /// recording and nearly silent. Throttling writes to match elapsed
        /// wall-clock time (like the video capture loop already does) keeps
        /// the file's duration correct and lets real audio bursts actually
        /// show up instead of being drowned in padding.
        /// </summary>
        private void PumpToDisk(CancellationToken token)
        {
            var format = _mixedWaveProvider!.WaveFormat;
            int bytesPerSecond = format.AverageBytesPerSecond;
            var buffer = new byte[Math.Max(bytesPerSecond / 20, 256)]; // ~50ms chunks
            var sw = Stopwatch.StartNew();
            long bytesWritten = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    long targetBytes = (long)(sw.Elapsed.TotalSeconds * bytesPerSecond);
                    long dueBytes = targetBytes - bytesWritten;

                    if (dueBytes < buffer.Length)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int toRead = (int)Math.Min(dueBytes, buffer.Length);
                    int read = _mixedWaveProvider.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (!IsPaused)
                        _writer!.Write(buffer, 0, read);
                    // While paused we still drain the mixer (so buffered
                    // real audio doesn't pile up and burst out later) but
                    // don't write it to disk, and we still advance
                    // bytesWritten so pacing stays correct once resumed.
                    bytesWritten += read;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log("AudioLoopbackMixer: pump failed - " + ex);
                CaptureError?.Invoke("Audio mixing failed: " + ex.Message);
            }
        }

        private static ISampleProvider ToMixFormat(ISampleProvider source, WaveFormat target)
        {
            ISampleProvider result = source;

            if (result.WaveFormat.SampleRate != target.SampleRate)
                result = new WdlResamplingSampleProvider(result, target.SampleRate);

            if (result.WaveFormat.Channels != target.Channels)
            {
                if (result.WaveFormat.Channels == 1 && target.Channels == 2)
                    result = new MonoToStereoSampleProvider(result);
                else if (result.WaveFormat.Channels == 2 && target.Channels == 1)
                    result = new StereoToMonoSampleProvider(result);
            }

            return result;
        }

        public async Task StopAsync()
        {
            _loopback?.StopRecording();
            _mic?.StopRecording();

            _pumpCts?.Cancel();
            if (_pumpTask != null)
            {
                try { await _pumpTask; } catch { /* cancellation is expected */ }
            }

            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            _loopback?.Dispose();
            _loopback = null;
            _mic?.Dispose();
            _mic = null;
        }

        public void Dispose()
        {
            _pumpCts?.Cancel();
            _writer?.Dispose();
            _loopback?.Dispose();
            _mic?.Dispose();
        }
    }
}
