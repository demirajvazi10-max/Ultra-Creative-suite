using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Services
{
    // ═══════════════════════════════════════════════════════════════════════════
    // DSP EFEKTI — svaki čuva referencu na TrackEffects i čita LIVE vrijednosti
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parametarski ekvilajzer: bas polica, srednji opseg (frekvencija + širina), visoka
    /// polica, i filteri koji seku krajeve (high-pass za brujanje/bas, low-pass za šuštanje).
    /// Sve se čita uživo; filteri se prave iznova samo kad se neka vrednost promeni.
    /// </summary>
    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private BiQuadFilter[][] _chain = Array.Empty<BiQuadFilter[]>();
        private string _key = "";
        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, TrackEffects fx) { _source = source; _fx = fx; Rebuild(); }

        private void Rebuild()
        {
            string key = $"{_fx.EqLow}|{_fx.EqMid}|{_fx.EqHigh}|{_fx.EqLowFreq}|{_fx.EqMidFreq}|{_fx.EqMidQ}|{_fx.EqHighFreq}|{_fx.HighPassFreq}|{_fx.LowPassFreq}";
            if (key == _key) return;
            _key = key;
            int ch = WaveFormat.Channels, sr = WaveFormat.SampleRate;
            float nyq = sr / 2f - 100;
            _chain = Enumerable.Range(0, ch).Select(_ =>
            {
                var list = new List<BiQuadFilter>();
                if (_fx.HighPassFreq >= 10) { var f = Math.Min(_fx.HighPassFreq, nyq); list.Add(BiQuadFilter.HighPassFilter(sr, f, 0.7071f)); list.Add(BiQuadFilter.HighPassFilter(sr, f, 0.7071f)); }
                if (Math.Abs(_fx.EqLow) > 0.05f)  list.Add(BiQuadFilter.LowShelf(sr, Math.Clamp(_fx.EqLowFreq, 20, nyq), 0.7f, _fx.EqLow));
                if (Math.Abs(_fx.EqMid) > 0.05f)  list.Add(BiQuadFilter.PeakingEQ(sr, Math.Clamp(_fx.EqMidFreq, 20, nyq), Math.Clamp(_fx.EqMidQ, 0.1f, 10f), _fx.EqMid));
                if (Math.Abs(_fx.EqHigh) > 0.05f) list.Add(BiQuadFilter.HighShelf(sr, Math.Clamp(_fx.EqHighFreq, 20, nyq), 0.7f, _fx.EqHigh));
                if (_fx.LowPassFreq >= 100 && _fx.LowPassFreq < nyq) { list.Add(BiQuadFilter.LowPassFilter(sr, _fx.LowPassFreq, 0.7071f)); list.Add(BiQuadFilter.LowPassFilter(sr, _fx.LowPassFreq, 0.7071f)); }
                return list.ToArray();
            }).ToArray();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.EqEnabled) return read;
            Rebuild();
            int ch = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
            {
                var filters = _chain[i % ch];
                float smp = buffer[offset + i];
                for (int k = 0; k < filters.Length; k++) smp = filters[k].Transform(smp);
                buffer[offset + i] = smp;
            }
            return read;
        }
    }

    /// <summary>
    /// Kratko utišavanje na početku i kraju svakog klipa (5 ms) — rez nikad ne "pukne",
    /// bez obzira gde je talas presečen. Kraći od 5 ms se ne čuje, a uklanja klik.
    /// </summary>
    public class EdgeFadeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly long _total;           // ukupno uzoraka (svi kanali) koji će se pročitati
        private readonly int _fade;             // uzoraka u fade-u (svi kanali)
        private readonly bool _fadeStart;
        private long _pos;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public EdgeFadeSampleProvider(ISampleProvider src, double seconds, bool fadeStart)
        {
            _src = src; _fadeStart = fadeStart;
            int ch = src.WaveFormat.Channels;
            _total = (long)(seconds * src.WaveFormat.SampleRate) * ch;
            _fade = (int)(0.005 * src.WaveFormat.SampleRate) * ch;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            for (int i = 0; i < n; i++, _pos++)
            {
                float g = 1f;
                if (_fadeStart && _pos < _fade) g = _pos / (float)_fade;
                long left = _total - _pos;
                if (left < _fade) g = Math.Min(g, Math.Max(0, left) / (float)_fade);
                if (g < 1f) buffer[offset + i] *= g;
            }
            return n;
        }
    }

    /// <summary>
    /// Reverb po modelu "Freeverb" (Jezar, javni domen) — 8 paralelnih comb filtera sa
    /// prigušenjem visokih + 4 serijska allpass filtera, POSEBNO za levi i desni kanal
    /// (desni ima malo duže linije = prirodna stereo širina), plus pre-delay.
    /// Ranije su 4 kratka comb filtera radila nad preplitanim stereo uzorcima, pa ni
    /// najveća "soba" nije mogla da zazvuči kao sala.
    ///   ReverbRoom     0..1 -> dužina repa (od kupatila do katedrale)
    ///   ReverbDamping  0..1 -> 0 svetli/tvrdi zidovi, 1 tamni/meki prostor
    ///   ReverbPreDelay ms   -> vreme do prvog odjeka (veći prostor = duže)
    ///   ReverbMix      0..1 -> koliko odjeka u odnosu na originalni zvuk
    /// </summary>
    public class ReverbSampleProvider : ISampleProvider
    {
        private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        private const int StereoSpread = 23;
        private const float FixedGain = 0.015f, ScaleWet = 3f;

        private sealed class Comb
        {
            private readonly float[] _buf; private int _i; private float _store;
            public Comb(int n) { _buf = new float[Math.Max(1, n)]; }
            public float Process(float input, float feedback, float damp1, float damp2)
            {
                float output = _buf[_i];
                _store = output * damp2 + _store * damp1;
                _buf[_i] = input + _store * feedback;
                if (++_i >= _buf.Length) _i = 0;
                return output;
            }
            public void Clear() { Array.Clear(_buf); _store = 0; }
        }
        private sealed class Allpass
        {
            private readonly float[] _buf; private int _i;
            public Allpass(int n) { _buf = new float[Math.Max(1, n)]; }
            public float Process(float input)
            {
                float bufout = _buf[_i];
                float output = -input + bufout;
                _buf[_i] = input + bufout * 0.5f;
                if (++_i >= _buf.Length) _i = 0;
                return output;
            }
            public void Clear() => Array.Clear(_buf);
        }

        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private readonly Comb[] _combL, _combR;
        private readonly Allpass[] _apL, _apR;
        private readonly float[] _preL, _preR;
        private int _preIdx;
        private bool _wasOn;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public ReverbSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            double k = source.WaveFormat.SampleRate / 44100.0;
            _combL = CombTuning.Select(n => new Comb((int)(n * k))).ToArray();
            _combR = CombTuning.Select(n => new Comb((int)((n + StereoSpread) * k))).ToArray();
            _apL = AllpassTuning.Select(n => new Allpass((int)(n * k))).ToArray();
            _apR = AllpassTuning.Select(n => new Allpass((int)((n + StereoSpread) * k))).ToArray();
            int preMax = (int)(source.WaveFormat.SampleRate * 0.25) + 1;   // do 250 ms
            _preL = new float[preMax]; _preR = new float[preMax];
        }

        private void ClearAll()
        {
            foreach (var c in _combL) c.Clear(); foreach (var c in _combR) c.Clear();
            foreach (var a in _apL) a.Clear(); foreach (var a in _apR) a.Clear();
            Array.Clear(_preL); Array.Clear(_preR);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.ReverbEnabled) { _wasOn = false; return read; }
            if (!_wasOn) { ClearAll(); _wasOn = true; }   // bez ostatka starog repa pri uključivanju

            int ch = WaveFormat.Channels;
            float room = Math.Clamp(_fx.ReverbRoom, 0f, 1f);
            float feedback = room * 0.28f + 0.7f;                       // 0.70 .. 0.98
            float damp1 = Math.Clamp(_fx.ReverbDamping, 0f, 1f) * 0.4f;
            float damp2 = 1f - damp1;
            float mix = Math.Clamp(_fx.ReverbMix, 0f, 1f);
            float dry = 1f - mix * 0.5f;
            int pre = Math.Clamp((int)(_fx.ReverbPreDelay / 1000f * WaveFormat.SampleRate), 0, _preL.Length - 1);

            for (int i = 0; i + ch - 1 < read; i += ch)
            {
                float l = buffer[offset + i];
                float r = ch > 1 ? buffer[offset + i + 1] : l;
                float input = (l + r) * FixedGain;

                // pre-delay
                _preL[_preIdx] = input;
                int rd = _preIdx - pre; if (rd < 0) rd += _preL.Length;
                float delayed = _preL[rd];
                if (++_preIdx >= _preL.Length) _preIdx = 0;

                float outL = 0, outR = 0;
                for (int c = 0; c < _combL.Length; c++)
                {
                    outL += _combL[c].Process(delayed, feedback, damp1, damp2);
                    outR += _combR[c].Process(delayed, feedback, damp1, damp2);
                }
                for (int a = 0; a < _apL.Length; a++) { outL = _apL[a].Process(outL); outR = _apR[a].Process(outR); }

                buffer[offset + i] = l * dry + outL * mix * ScaleWet;
                if (ch > 1) buffer[offset + i + 1] = r * dry + outR * mix * ScaleWet;
            }
            return read;
        }
    }

    public class DelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float[] _buffer;
        private int _pos, _delaySamples;
        private float _lastDelayTime;
        private bool _wasOn;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public DelaySampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            _lastDelayTime = fx.DelayTime;
            _delaySamples = (int)(fx.DelayTime * source.WaveFormat.SampleRate) * source.WaveFormat.Channels;
            _buffer = new float[Math.Max(_delaySamples * 2, source.WaveFormat.SampleRate * 2)];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.DelayEnabled) { _wasOn = false; return read; }
            if (!_wasOn) { Array.Clear(_buffer); _wasOn = true; }
            // Ažuriraj delay time ako se promijenio
            if (Math.Abs(_lastDelayTime - _fx.DelayTime) > 0.001f)
            {
                _delaySamples = (int)(_fx.DelayTime * WaveFormat.SampleRate) * WaveFormat.Channels;
                _delaySamples = Math.Clamp(_delaySamples, 1, _buffer.Length - 1);
                _lastDelayTime = _fx.DelayTime;
            }
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[offset + i];
                int readPos = (_pos - _delaySamples + _buffer.Length) % _buffer.Length;
                float delayed = _buffer[readPos];
                _buffer[_pos] = dry + delayed * _fx.DelayFeedback;
                _pos = (_pos + 1) % _buffer.Length;
                buffer[offset + i] = dry + delayed * Math.Clamp(_fx.DelayMix, 0f, 1f) * 1.5f;
            }
            return read;
        }
    }

    public class CompressorSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float _envelope;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public CompressorSampleProvider(ISampleProvider source, TrackEffects fx)
        { _source = source; _fx = fx; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.CompressorEnabled) return read;
            float threshold = (float)Math.Pow(10.0, _fx.CompThreshold / 20.0);
            float sr = WaveFormat.SampleRate;
            float attackCoeff  = (float)Math.Exp(-1.0 / (sr * _fx.CompAttack  / 1000.0));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sr * _fx.CompRelease / 1000.0));
            float makeup = (float)Math.Pow(10.0, _fx.CompMakeup / 20.0);   // pojačanje posle kompresije
            for (int i = 0; i < read; i++)
            {
                float input = Math.Abs(buffer[offset + i]);
                float coeff = input > _envelope ? attackCoeff : releaseCoeff;
                _envelope = input + coeff * (_envelope - input);
                float gain = 1f;
                if (_envelope > threshold && _fx.CompRatio > 1f)
                {
                    float overDb = 20f * (float)Math.Log10(_envelope / threshold);
                    gain = (float)Math.Pow(10.0, -overDb * (1f - 1f / _fx.CompRatio) / 20.0);
                }
                buffer[offset + i] *= gain * makeup;
            }
            return read;
        }
    }

    public class NoiseGateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float _envelope;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public NoiseGateSampleProvider(ISampleProvider source, TrackEffects fx)
        { _source = source; _fx = fx; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.NoiseGateEnabled) return read;
            float threshold = (float)Math.Pow(10.0, _fx.GateThreshold / 20.0);
            float sr = WaveFormat.SampleRate;
            float attackCoeff  = (float)Math.Exp(-1.0 / (sr * 0.001));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sr * 0.100));
            for (int i = 0; i < read; i++)
            {
                float input = Math.Abs(buffer[offset + i]);
                float coeff = input > _envelope ? attackCoeff : releaseCoeff;
                _envelope = input + coeff * (_envelope - input);
                if (_envelope < threshold) buffer[offset + i] = 0f;
            }
            return read;
        }
    }

    public class BassBoostSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private BiQuadFilter[] _filters;
        private float _lastGain;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public BassBoostSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int ch = source.WaveFormat.Channels, sr = source.WaveFormat.SampleRate;
            _filters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,fx.BassGain)).ToArray();
            _lastGain = fx.BassGain;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.BassBostEnabled) return read;
            if (Math.Abs(_lastGain - _fx.BassGain) > 0.01f)
            {
                int ch = WaveFormat.Channels, sr = WaveFormat.SampleRate;
                _filters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,_fx.BassGain)).ToArray();
                _lastGain = _fx.BassGain;
            }
            int channels = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
                buffer[offset + i] = _filters[i % channels].Transform(buffer[offset + i]);
            return read;
        }
    }

    /// <summary>
    /// Chorus: po kanalu, kašnjenje 7–27 ms koje se njiše LFO-om (levi i desni kanal
    /// u suprotnoj fazi = širok stereo). Ranije je kašnjenje bilo svega par uzoraka
    /// nad preplitanim stereo signalom, pa se efekat skoro nije čuo.
    ///   ChorusDepth 0..1 — koliko se kašnjenje njiše
    ///   ChorusRate  Hz   — brzina njihanja
    ///   ChorusMix   0..1 — koliko "drugog glasa" u odnosu na original
    /// </summary>
    public class ChorusSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private readonly float[][] _buf;
        private int _pos;
        private double _phase;
        private bool _wasOn;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public ChorusSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int len = (int)(source.WaveFormat.SampleRate * 0.05) + 2;   // 50 ms
            _buf = Enumerable.Range(0, Math.Max(1, source.WaveFormat.Channels)).Select(_ => new float[len]).ToArray();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.ChorusEnabled) { _wasOn = false; return read; }
            if (!_wasOn) { foreach (var b in _buf) Array.Clear(b); _wasOn = true; }

            int ch = WaveFormat.Channels, len = _buf[0].Length;
            double sr = WaveFormat.SampleRate;
            double baseMs = 7 + 10 * Math.Clamp(_fx.ChorusDepth, 0f, 1f);
            double swingMs = 10 * Math.Clamp(_fx.ChorusDepth, 0f, 1f);
            double inc = 2 * Math.PI * Math.Clamp(_fx.ChorusRate, 0.05f, 10f) / sr;
            float mix = Math.Clamp(_fx.ChorusMix, 0f, 1f);

            for (int i = 0; i + ch - 1 < read; i += ch)
            {
                for (int c = 0; c < ch; c++)
                {
                    float dry = buffer[offset + i + c];
                    _buf[c][_pos] = dry;
                    double lfo = Math.Sin(_phase + (c == 1 ? Math.PI : 0));
                    double d = (baseMs + swingMs * lfo) / 1000.0 * sr;
                    double rp = _pos - d; while (rp < 0) rp += len;
                    int i0 = (int)rp; int i1 = (i0 + 1) % len; float frac = (float)(rp - i0);
                    float wet = _buf[c][i0 % len] * (1 - frac) + _buf[c][i1] * frac;   // linearna interpolacija = bez krckanja
                    buffer[offset + i + c] = dry * (1f - mix * 0.5f) + wet * mix;
                }
                if (++_pos >= len) _pos = 0;
                _phase += inc; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }
            return read;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // AUDIO ENGINE
    // ════════════════════════════════════════════════════════════════════════════

    public class AudioEngine : IDisposable
    {
        private WaveOutEvent? _waveOut;
        private MixingSampleProvider? _mixer;
        private readonly List<AudioFileReader> _readers = new();
        private bool _disposed;

        /// <summary>Kašnjenje izlaza zvuka; koristi se i za poravnanje snimka sa podlogom.</summary>
        public const int OutputLatencyMs = 100;

        public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
        public float MasterVolume { get; set; } = 0.8f;

        public event EventHandler? PlaybackStopped;
        public event EventHandler<double>? PositionChanged;

        private System.Timers.Timer? _posTimer;
        private double _playStartOffset;
        private DateTime _playStartTime;

        public void Play(AudioProject project, double fromPosition = 0)
        {
            Stop();
            _playStartOffset = fromPosition;
            _playStartTime   = DateTime.UtcNow;

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
            _mixer = new MixingSampleProvider(waveFormat) { ReadFully = true };

            // Mute / Solo / Master se NE primenjuju ovde jednom pri pokretanju, nego
            // uživo u LiveVolumeSampleProvider-u — klik na Mute ili Solo dok svira
            // čuje se odmah, bez ponovnog puštanja.
            foreach (var track in project.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (string.IsNullOrEmpty(clip.FilePath) || !File.Exists(clip.FilePath)) continue;
                    if (clip.StartTime + clip.Duration < fromPosition) continue;

                    try
                    {
                        var reader = new AudioFileReader(clip.FilePath);
                        _readers.Add(reader);

                        double skip = Math.Max(0, fromPosition - clip.StartTime) + clip.TrimStart;
                        if (skip > 0)
                            reader.CurrentTime = TimeSpan.FromSeconds(
                                Math.Min(skip, reader.TotalTime.TotalSeconds - 0.01));

                        // Stereo + Resample
                        ISampleProvider src = reader.WaveFormat.Channels == 1
                            ? (ISampleProvider)new MonoToStereoSampleProvider(reader)
                            : reader;
                        if (reader.WaveFormat.SampleRate != project.SampleRate)
                            src = new WdlResamplingSampleProvider(src, project.SampleRate);

                        // ── LIVE DSP LANAC ── redosled iz track.Effects.Order, vrednosti se čitaju uživo
                        double fromInClip = Math.Max(0, fromPosition - clip.StartTime);
                        src = new EdgeFadeSampleProvider(src, clip.Duration - fromInClip, fadeStart: fromInClip < 0.005);
                        src = BuildEffectChain(src, track.Effects);

                        // Jačina, panorama, mute/solo i kriva jačine — sve uživo
                        var vol = new LiveVolumeSampleProvider(src, track, project, () => MasterVolume,
                            Math.Max(fromPosition, clip.StartTime));

                        // Offset (početak klipa) + dužina klipa u JEDNOM OffsetSampleProvider-u.
                        // Ranije je TakeSamples bio na spoljnom provider-u koji je već sadržao
                        // tišinu kašnjenja, pa je klip koji počinje posle playhead-a bio odsečen
                        // na kraju tačno za dužinu tog kašnjenja (vidljivo posle sečenja klipa).
                        double delay = Math.Max(0, clip.StartTime - fromPosition);
                        double remaining = clip.Duration - Math.Max(0, fromPosition - clip.StartTime);
                        var trimmed = new OffsetSampleProvider(vol)
                        {
                            DelayBySamples = (int)(delay * project.SampleRate) * 2,      // paran broj = levi/desni kanal ostaju na mestu
                            TakeSamples    = (int)(remaining * project.SampleRate) * 2
                        };

                        _mixer.AddMixerInput(trimmed);
                    }
                    catch (Exception ex) { DebugLog.Write($"Play: clip '{clip.Name}' skipped: {ex.Message}"); }
                }
            }

            _waveOut = new WaveOutEvent { DesiredLatency = OutputLatencyMs };
            _waveOut.Init(_mixer);
            // PlaybackStopped se prosleđuje SAMO za ovaj, trenutni WaveOut. Ranije je i
            // ručno zaustavljanje prethodnog (koje Play() radi na početku) javljalo "stalo",
            // pa je stanje "svira" odmah padalo na false i Space je ponovo puštao umesto
            // da pauzira.
            int gen = ++_generation;
            var wo = _waveOut;
            wo.PlaybackStopped += (s, e) => { if (gen == _generation && ReferenceEquals(wo, _waveOut)) PlaybackStopped?.Invoke(this, EventArgs.Empty); };
            _waveOut.Play();

            _posTimer = new System.Timers.Timer(50);
            _posTimer.Elapsed += (s, e) =>
            {
                // Zakasneli otkucaj starog tajmera (posle skoka na novu poziciju) se ignoriše,
                // inače bi vratio poziciju na staro mesto.
                if (gen != _generation) return;
                PositionChanged?.Invoke(this, _playStartOffset + (DateTime.UtcNow - _playStartTime).TotalSeconds);
            };
            _posTimer.Start();
        }

        private volatile int _generation;

        public void Stop()
        {
            _generation++;
            _posTimer?.Stop(); _posTimer?.Dispose(); _posTimer = null;
            _waveOut?.Stop(); _waveOut?.Dispose(); _waveOut = null;
            foreach (var r in _readers) try { r.Dispose(); } catch { }
            _readers.Clear();
        }

        public void Pause() { _generation++; _posTimer?.Stop(); _waveOut?.Pause(); }
        public void Resume() { _waveOut?.Play(); _posTimer?.Start(); }

        /// <summary>Lanac efekata trake, redom iz fx.Order (nedostajući efekti se dodaju na kraj).</summary>
        public static ISampleProvider BuildEffectChain(ISampleProvider src, TrackEffects fx)
        {
            var order = (fx.Order ?? TrackEffects.DefaultOrder()).Distinct().ToList();
            foreach (var t in TrackEffects.DefaultOrder()) if (!order.Contains(t)) order.Add(t);
            foreach (var t in order)
            {
                src = t switch
                {
                    EffectType.NoiseGate  => new NoiseGateSampleProvider(src, fx),
                    EffectType.Compressor => new CompressorSampleProvider(src, fx),
                    EffectType.PitchShift => new PitchShiftSampleProvider(src, fx),
                    EffectType.Equalizer  => new EqualizerSampleProvider(src, fx),
                    EffectType.BassBoost  => new BassBoostSampleProvider(src, fx),
                    EffectType.Chorus     => new ChorusSampleProvider(src, fx),
                    EffectType.Delay      => new DelaySampleProvider(src, fx),
                    EffectType.Reverb     => new ReverbSampleProvider(src, fx),
                    _ => src
                };
            }
            return src;
        }

        // ── Statičke operacije ────────────────────────────────────────────────

        public static float[] LoadWaveformData(string filePath, int points = 2000)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                var result = new float[points];
                int ch = reader.WaveFormat.Channels;
                int step = Math.Max(1, (int)(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate) / points);
                var buf = new float[step * ch];
                int idx = 0; int n;
                while (idx < points && (n = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    float max = 0;
                    for (int i = 0; i < n; i++) max = Math.Max(max, Math.Abs(buf[i]));
                    result[idx++] = max;
                }
                return result;
            }
            catch { return new float[points]; }
        }

        public static void NormalizeFile(string inputPath, string outputPath, float target = 0.95f)
        {
            using var r = new AudioFileReader(inputPath);
            float peak = 0;
            var buf = new float[r.WaveFormat.SampleRate * r.WaveFormat.Channels];
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
            if (peak < 0.0001f) return;
            float factor = target / peak;
            r.Position = 0;
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) buf[i] = Math.Clamp(buf[i] * factor, -1f, 1f);
                w.WriteSamples(buf, 0, n);
            }
        }

        public static void ApplyFadeIn(string inputPath, string outputPath, double fadeSec)
        {
            using var r = new AudioFileReader(inputPath);
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            int fadeSamples = (int)(fadeSec * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
            var buf = new float[4096]; int total = 0, n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                    if (total + i < fadeSamples) buf[i] *= (float)(total + i) / fadeSamples;
                total += n; w.WriteSamples(buf, 0, n);
            }
        }

        public static void ApplyFadeOut(string inputPath, string outputPath, double fadeSec)
        {
            using var r = new AudioFileReader(inputPath);
            long total = r.Length / sizeof(float);
            long fadeSamples = (long)(fadeSec * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
            long fadeStart = total - fadeSamples;
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            var buf = new float[4096]; long pos = 0; int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    long sp = pos + i;
                    if (sp >= fadeStart && fadeSamples > 0)
                        buf[i] *= Math.Max(0f, (float)(total - sp) / fadeSamples);
                }
                pos += n; w.WriteSamples(buf, 0, n);
            }
        }

        // ── IZVOZ ─────────────────────────────────────────────────────────────
        // Dva koraka:
        //  1) RenderMix: miks svih traka u privremeni 32-bit float WAV + merenje
        //     najglasnijeg vrha. Float NE seče signal iznad 0 dBFS, pa tačno znamo
        //     koliko je miks preglasan pre nego što išta izgubimo.
        //  2) EncodeMix: iz tog fajla pravi konačni format (WAV/MP3/M4A/AIFF), uz
        //     opcionu korekciju glasnoće (gain) da miks ne bi bio izobličen.
        // Između ta dva koraka editor korisnika pita šta da radi ako je miks preglasan.

        public sealed class MixRenderResult
        {
            public string TempPath { get; init; } = "";
            public float Peak { get; init; }          // 1.0 = 0 dBFS
            public double Seconds { get; init; }
            public int TracksMixed { get; init; }
            public int TracksSkipped { get; init; }  // mute / van solo-a
            public int ClipsMixed { get; init; }
            public int MissingFiles { get; init; }
            public double PeakDb => Peak <= 0.000001f ? -120 : 20 * Math.Log10(Peak);
            public bool IsClipping => Peak > 1.0f;
        }

        private sealed class PeakMeterSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _src;
            public float Peak { get; private set; }
            public PeakMeterSampleProvider(ISampleProvider src) { _src = src; }
            public WaveFormat WaveFormat => _src.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            {
                int n = _src.Read(buffer, offset, count);
                float p = Peak;
                for (int i = 0; i < n; i++) { float a = Math.Abs(buffer[offset + i]); if (a > p) p = a; }
                Peak = p;
                return n;
            }
        }

        public static MixRenderResult RenderMix(AudioProject project, Action<int>? progress = null)
        {
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
            // ReadFully = false: mikser MORA da javi kraj (Read vrati 0) čim sve trake završe.
            // Sa ReadFully = true, mikser zauvijek vraća tišinu i WaveFileWriter piše u nedogled
            // dok ne probije limit veličine WAV chunk-a -> "WAV file too large".
            var mixer = new MixingSampleProvider(waveFormat) { ReadFully = false };
            bool anySolo = project.Tracks.Any(t => t.IsSolo);
            var readers = new List<AudioFileReader>();
            int tracksMixed = 0, tracksSkipped = 0, clipsMixed = 0, missing = 0;

            foreach (var track in project.Tracks)
            {
                if (track.IsMuted || (anySolo && !track.IsSolo)) { if (track.Clips.Count > 0) tracksSkipped++; continue; }
                int before = clipsMixed;
                foreach (var clip in track.Clips)
                {
                    if (!File.Exists(clip.FilePath)) { missing++; continue; }
                    try
                    {
                        var reader = new AudioFileReader(clip.FilePath);
                        readers.Add(reader);
                        if (clip.TrimStart > 0)
                            reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(clip.TrimStart, Math.Max(0, reader.TotalTime.TotalSeconds - 0.01)));
                        ISampleProvider src = reader.WaveFormat.Channels == 1
                            ? (ISampleProvider)new MonoToStereoSampleProvider(reader) : reader;
                        if (reader.WaveFormat.SampleRate != project.SampleRate)
                            src = new WdlResamplingSampleProvider(src, project.SampleRate);
                        src = new EdgeFadeSampleProvider(src, clip.Duration, fadeStart: true);
                        src = BuildEffectChain(src, track.Effects);
                        // Isto kao reprodukcija: jačina, panorama i kriva jačine (master = 100%)
                        var vol = new LiveVolumeSampleProvider(src, track, project, () => 1f, clip.StartTime);
                        var offset = new OffsetSampleProvider(vol)
                        {
                            DelayBySamples = (int)(clip.StartTime * project.SampleRate) * 2,
                            TakeSamples    = (int)(clip.Duration   * project.SampleRate) * 2
                        };
                        mixer.AddMixerInput(offset);
                        clipsMixed++;
                    }
                    catch (Exception ex) { DebugLog.Write($"RenderMix: clip '{clip.Name}' skipped: {ex.Message}"); }
                }
                if (clipsMixed > before) tracksMixed++;
            }

            progress?.Invoke(10);
            var meter = new PeakMeterSampleProvider(mixer);
            string tmp = Path.Combine(Path.GetTempPath(), $"ultraaudio_mix_{Guid.NewGuid()}.wav");
            try
            {
                WaveFileWriter.CreateWaveFile(tmp, new SampleToWaveProvider(meter));
            }
            finally
            {
                foreach (var rr in readers) try { rr.Dispose(); } catch { }
            }
            double secs = 0;
            try { using var r = new WaveFileReader(tmp); secs = r.TotalTime.TotalSeconds; } catch { }
            progress?.Invoke(60);
            return new MixRenderResult
            {
                TempPath = tmp, Peak = meter.Peak, Seconds = secs,
                TracksMixed = tracksMixed, TracksSkipped = tracksSkipped, ClipsMixed = clipsMixed, MissingFiles = missing
            };
        }

        /// <summary>
        /// Pravi konačni fajl iz privremenog float miksa. gain = 1 ostavlja miks kakav jeste
        /// (vrhovi iznad 0 dBFS se tada odsecaju pri prelasku u 16-bit).
        /// </summary>
        public static void EncodeMix(string floatWavPath, string outputPath, ExportFormat format,
            float gain = 1f, int bitRate = 192, Action<int>? progress = null)
        {
            using var reader = new AudioFileReader(floatWavPath);
            ISampleProvider sp = reader;
            if (Math.Abs(gain - 1f) > 0.0001f) sp = new VolumeSampleProvider(sp) { Volume = gain };
            var pcm16 = new SampleToWaveProvider16(sp);
            progress?.Invoke(70);
            switch (format)
            {
                case ExportFormat.MP3:
                    using (var mp3 = new NAudio.Lame.LameMP3FileWriter(outputPath, pcm16.WaveFormat, bitRate))
                        CopyProvider(pcm16, mp3.Write);
                    break;
                case ExportFormat.M4A:
                    // AAC preko Windows Media Foundation (ugrađeno u Windows 10/11).
                    NAudio.MediaFoundation.MediaFoundationApi.Startup();
                    MediaFoundationEncoder.EncodeToAac(pcm16, outputPath, Math.Max(96, bitRate) * 1000);
                    break;
                case ExportFormat.AIFF:
                    using (var aiff = new AiffFileWriter(outputPath, pcm16.WaveFormat))
                        CopyProvider(pcm16, aiff.Write);
                    break;
                default:
                    WaveFileWriter.CreateWaveFile(outputPath, pcm16);
                    break;
            }
            progress?.Invoke(100);
        }

        private static void CopyProvider(IWaveProvider src, Action<byte[], int, int> write)
        {
            var buf = new byte[src.WaveFormat.AverageBytesPerSecond];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0) write(buf, 0, n);
        }

        /// <summary>Stari jednostavni izvoz (bez pitanja) — koristi ga "Kombinuj trake".</summary>
        public static void ExportMixdown(AudioProject project, string outputPath,
            ExportFormat format, int bitRate = 192, Action<int>? progress = null)
        {
            var mix = RenderMix(project, progress);
            try
            {
                float gain = mix.IsClipping ? 0.98f / mix.Peak : 1f;
                EncodeMix(mix.TempPath, outputPath, format, gain, bitRate, progress);
            }
            finally
            {
                try { File.Delete(mix.TempPath); } catch { }
            }
        }

        public void Dispose() { if (!_disposed) { Stop(); _disposed = true; } }
    }

    // Live volume — čita Volume i MasterVolume u realnom vremenu
    // ═══════════════════════════════════════════════════════════════════════════
    // PITCH SHIFT — nije postojao u lancu efekata ranije (samo UI dugme koje nije
    // radilo ništa). NAudio nema ugrađen pitch shifter, pa je ovo samostalna
    // implementacija klasičnog FFT phase vocoder algoritma (Stephan M. Bernsee,
    // "smbPitchShift" — javno dostupan algoritam, korišćen u bezbroj audio alata
    // poslednjih 20+ godina). Radi po kanalu (stereo = dve nezavisne instance),
    // strimuje po Read() pozivu, bez potrebe za spoljnom FFT bibliotekom.
    // ═══════════════════════════════════════════════════════════════════════════
    internal class SmbPitchShifter
    {
        private const int MAX_FRAME_LENGTH = 8192;
        private readonly float[] _inFifo = new float[MAX_FRAME_LENGTH];
        private readonly float[] _outFifo = new float[MAX_FRAME_LENGTH];
        private readonly float[] _fftWorksp = new float[2 * MAX_FRAME_LENGTH];
        private readonly float[] _lastPhase = new float[MAX_FRAME_LENGTH / 2 + 1];
        private readonly float[] _sumPhase = new float[MAX_FRAME_LENGTH / 2 + 1];
        private readonly float[] _outputAccum = new float[2 * MAX_FRAME_LENGTH];
        private readonly float[] _anaFreq = new float[MAX_FRAME_LENGTH];
        private readonly float[] _anaMagn = new float[MAX_FRAME_LENGTH];
        private readonly float[] _synFreq = new float[MAX_FRAME_LENGTH];
        private readonly float[] _synMagn = new float[MAX_FRAME_LENGTH];
        private long _rover = -1;

        public void PitchShift(float pitchShift, int numSampsToProcess, int fftFrameSize, int osamp,
            float sampleRate, float[] indata, float[] outdata)
        {
            int fftFrameSize2 = fftFrameSize / 2;
            int stepSize = fftFrameSize / osamp;
            double freqPerBin = sampleRate / (double)fftFrameSize;
            double expct = 2.0 * Math.PI * stepSize / fftFrameSize;
            int inFifoLatency = fftFrameSize - stepSize;
            if (_rover < 0) _rover = inFifoLatency;

            for (int i = 0; i < numSampsToProcess; i++)
            {
                _inFifo[_rover] = indata[i];
                outdata[i] = _outFifo[_rover - inFifoLatency];
                _rover++;

                if (_rover >= fftFrameSize)
                {
                    _rover = inFifoLatency;

                    for (int k = 0; k < fftFrameSize; k++)
                    {
                        double window = -0.5 * Math.Cos(2.0 * Math.PI * k / fftFrameSize) + 0.5;
                        _fftWorksp[2 * k] = (float)(_inFifo[k] * window);
                        _fftWorksp[2 * k + 1] = 0f;
                    }

                    Fft(_fftWorksp, fftFrameSize, -1);

                    for (int k = 0; k <= fftFrameSize2; k++)
                    {
                        double real = _fftWorksp[2 * k], imag = _fftWorksp[2 * k + 1];
                        double magn = 2.0 * Math.Sqrt(real * real + imag * imag);
                        double phase = Math.Atan2(imag, real);
                        double tmp = phase - _lastPhase[k];
                        _lastPhase[k] = (float)phase;
                        tmp -= k * expct;
                        long qpd = (long)(tmp / Math.PI);
                        if (qpd >= 0) qpd += qpd & 1; else qpd -= qpd & 1;
                        tmp -= Math.PI * qpd;
                        tmp = osamp * tmp / (2.0 * Math.PI);
                        tmp = k * freqPerBin + tmp * freqPerBin;
                        _anaMagn[k] = (float)magn;
                        _anaFreq[k] = (float)tmp;
                    }

                    Array.Clear(_synMagn, 0, fftFrameSize);
                    Array.Clear(_synFreq, 0, fftFrameSize);
                    for (int k = 0; k <= fftFrameSize2; k++)
                    {
                        int index = (int)(k * pitchShift);
                        if (index <= fftFrameSize2)
                        {
                            _synMagn[index] += _anaMagn[k];
                            _synFreq[index] = _anaFreq[k] * pitchShift;
                        }
                    }

                    for (int k = 0; k <= fftFrameSize2; k++)
                    {
                        double magn = _synMagn[k];
                        double tmp = _synFreq[k];
                        tmp -= k * freqPerBin;
                        tmp /= freqPerBin;
                        tmp = 2.0 * Math.PI * tmp / osamp;
                        tmp += k * expct;
                        _sumPhase[k] += (float)tmp;
                        double phase = _sumPhase[k];
                        _fftWorksp[2 * k] = (float)(magn * Math.Cos(phase));
                        _fftWorksp[2 * k + 1] = (float)(magn * Math.Sin(phase));
                    }

                    for (int k = fftFrameSize + 2; k < 2 * fftFrameSize; k++) _fftWorksp[k] = 0f;

                    Fft(_fftWorksp, fftFrameSize, 1);

                    for (int k = 0; k < fftFrameSize; k++)
                    {
                        double window = -0.5 * Math.Cos(2.0 * Math.PI * k / fftFrameSize) + 0.5;
                        _outputAccum[k] += (float)(2.0 * window * _fftWorksp[2 * k] / (fftFrameSize2 * osamp));
                    }
                    for (int k = 0; k < stepSize; k++) _outFifo[k] = _outputAccum[k];
                    Array.Copy(_outputAccum, stepSize, _outputAccum, 0, fftFrameSize);
                    for (int k = 0; k < inFifoLatency; k++) _inFifo[k] = _inFifo[k + stepSize];
                }
            }
        }

        private static void Fft(float[] fftBuffer, int fftFrameSize, int sign)
        {
            for (int i = 2; i < 2 * fftFrameSize - 2; i += 2)
            {
                int j = 0;
                for (int bitm = 2; bitm < 2 * fftFrameSize; bitm <<= 1)
                {
                    if ((i & bitm) != 0) j++;
                    j <<= 1;
                }
                if (i < j)
                {
                    (fftBuffer[j], fftBuffer[i]) = (fftBuffer[i], fftBuffer[j]);
                    (fftBuffer[j + 1], fftBuffer[i + 1]) = (fftBuffer[i + 1], fftBuffer[j + 1]);
                }
            }
            int logN = (int)(Math.Log(fftFrameSize) / Math.Log(2.0) + 0.5);
            int le = 2;
            for (int k = 0; k < logN; k++)
            {
                le <<= 1;
                int le2 = le >> 1;
                double ur = 1.0, ui = 0.0;
                double arg = Math.PI / (le2 >> 1);
                double wr = Math.Cos(arg), wi = sign * Math.Sin(arg);
                for (int j = 0; j < le2; j += 2)
                {
                    int p1r = j, p1i = j + 1, p2r = j + le2, p2i = j + le2 + 1;
                    for (int i = j; i < 2 * fftFrameSize; i += le)
                    {
                        double tr = fftBuffer[p2r] * ur - fftBuffer[p2i] * ui;
                        double ti = fftBuffer[p2r] * ui + fftBuffer[p2i] * ur;
                        fftBuffer[p2r] = (float)(fftBuffer[p1r] - tr);
                        fftBuffer[p2i] = (float)(fftBuffer[p1i] - ti);
                        fftBuffer[p1r] += (float)tr;
                        fftBuffer[p1i] += (float)ti;
                        p1r += le; p1i += le; p2r += le; p2i += le;
                    }
                    double tr2 = ur * wr - ui * wi;
                    ui = ur * wi + ui * wr;
                    ur = tr2;
                }
            }
        }
    }

    public class PitchShiftSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private readonly SmbPitchShifter[] _shifters;
        private readonly float[] _channelIn;
        private readonly float[] _channelOut;
        private const int FFT_SIZE = 2048, OVERSAMPLE = 8;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public PitchShiftSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int ch = source.WaveFormat.Channels;
            _shifters = new SmbPitchShifter[ch];
            for (int i = 0; i < ch; i++) _shifters[i] = new SmbPitchShifter();
            _channelIn = new float[16384];
            _channelOut = new float[16384];
        }

        private bool _errored;
        public static string? LastError { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.PitchEnabled || _fx.PitchSemitones == 0 || _errored) return read;

            try
            {
                int ch = WaveFormat.Channels;
                int framesRead = read / ch;
                if (framesRead > _channelIn.Length)
                {
                    // Neočekivano velik blok (retko) — obradi u komadima od najviše
                    // kapaciteta bafera umesto da pretpostavljamo fiksnu gornju granicu.
                    int done = 0;
                    while (done < framesRead)
                    {
                        int chunk = Math.Min(_channelIn.Length, framesRead - done);
                        ProcessChunk(buffer, offset + done * ch, chunk, ch);
                        done += chunk;
                    }
                }
                else
                {
                    ProcessChunk(buffer, offset, framesRead, ch);
                }
            }
            catch (Exception ex)
            {
                // NE rušimo plejbek zbog bug-a u pitch shift-u — nastavljamo sa
                // neizmenjenim zvukom i zapisujemo tačan uzrok u log fajl da se
                // sledeći put popravi precizno, umesto nagađanja.
                _errored = true;
                LastError = ex.ToString();
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraAudioEditor");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "pitch_shift_error.log"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
                }
                catch { }
            }
            return read;
        }

        private void ProcessChunk(float[] buffer, int offset, int frames, int ch)
        {
            float ratio = (float)Math.Pow(2.0, _fx.PitchSemitones / 12.0);
            for (int c = 0; c < ch; c++)
            {
                for (int f = 0; f < frames; f++) _channelIn[f] = buffer[offset + f * ch + c];
                _shifters[c].PitchShift(ratio, frames, FFT_SIZE, OVERSAMPLE, WaveFormat.SampleRate, _channelIn, _channelOut);
                for (int f = 0; f < frames; f++) buffer[offset + f * ch + c] = _channelOut[f];
            }
        }
    }
    public class LiveVolumeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly AudioProject _project;
        private readonly Func<float> _master;
        private float _current = -1f;   // poslednja primenjena jačina (za glatku promenu)
        private readonly double _startTime;   // vreme na vremenskoj liniji prvog uzorka koji ovaj provider propušta
        private long _frames;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public LiveVolumeSampleProvider(ISampleProvider source, AudioTrack track, AudioProject project, Func<float> master, double startTime = 0)
        { _source = source; _track = track; _project = project; _master = master; _startTime = startTime; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            bool anySolo = false;
            try { anySolo = _project.Tracks.Any(t => t.IsSolo); } catch { }
            bool audible = !_track.IsMuted && (!anySolo || _track.IsSolo);
            int chn = Math.Max(1, WaveFormat.Channels);
            // kriva jačine na KRAJU ovog bloka (unutar bloka se glatko prelazi do nje)
            double tEnd = _startTime + (_frames + read / chn) / (double)WaveFormat.SampleRate;
            _frames += read / chn;
            float env = 1f;
            try { env = _track.EnvelopeGainAt(tEnd); } catch { }
            float target = audible ? _track.Volume * _master() * env : 0f;
            if (_current < 0) _current = target;
            // Linearni prelaz preko bloka umesto skoka — bez "klika" pri mute/solo/jačini.
            float step = read > 0 ? (target - _current) / read : 0;
            // Panorama (ranije se Pan čuvao, ali se nigde nije primenjivao): signal je
            // ovde uvek stereo, parni uzorci = levo, neparni = desno.
            float pan = Math.Clamp(_track.Pan, -1f, 1f);
            float gl = pan > 0 ? 1f - pan : 1f, gr = pan < 0 ? 1f + pan : 1f;
            bool stereo = WaveFormat.Channels == 2;
            for (int i = 0; i < read; i++)
            {
                _current += step;
                float g = stereo ? ((i & 1) == 0 ? gl : gr) : 1f;
                buffer[offset + i] *= _current * g;
            }
            _current = target;
            return read;
        }
    }

    // OGG i FLAC su izbačeni: NAudio nema enkoder za njih, pa je stari kod samo
    // kopirao WAV pod tim imenom (fajl sa pogrešnom ekstenzijom).
    public enum ExportFormat { WAV, MP3, M4A, AIFF }
}
