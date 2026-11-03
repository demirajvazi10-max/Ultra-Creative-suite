using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace UltraAudioEditor.Models
{
    public class AudioClip : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = Localization.Lang.T("default_clip_name");
        public string FilePath { get; set; } = "";
        public double StartTime { get; set; }      // offset u projektu (sekunde)
        public double Duration { get; set; }        // trajanje klipa (sekunde)
        public double TrimStart { get; set; }       // od kojeg trenutka u fajlu
        public double SourceDuration { get; set; }  // ukupna dužina izvornog fajla (sekunde), 0 = nepoznato
        public float[]? WaveformData { get; set; }  // za crtanje (cela dužina izvornog fajla)

        public double EndTime => StartTime + Duration;

        /// <summary>Kopija klipa sa novim Id-jem (isti izvorni fajl, ista pozicija/trim).</summary>
        public AudioClip CloneWithNewId() => new AudioClip
        {
            Name = Name, FilePath = FilePath, StartTime = StartTime, Duration = Duration,
            TrimStart = TrimStart, SourceDuration = SourceDuration, WaveformData = WaveformData
        };

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AudioTrack : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _name = Localization.Lang.T("default_track_name");
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        private Color _color = Color.FromRgb(55, 138, 221);
        public Color Color
        {
            get => _color;
            set { _color = value; OnPropertyChanged(); OnPropertyChanged(nameof(ColorBrush)); }
        }
        public SolidColorBrush ColorBrush => new SolidColorBrush(_color);

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set { _isMuted = value; OnPropertyChanged(); }
        }

        private bool _isSolo;
        public bool IsSolo
        {
            get => _isSolo;
            set { _isSolo = value; OnPropertyChanged(); }
        }

        private float _volume = 0.8f;
        public float Volume
        {
            get => _volume;
            set { _volume = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); }
        }

        private float _pan = 0f;
        public float Pan
        {
            get => _pan;
            set { _pan = Math.Clamp(value, -1f, 1f); OnPropertyChanged(); }
        }

        private double _height = 80;
        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public TrackType Type { get; set; } = TrackType.Audio;

        private ObservableCollection<AudioClip> _clips = new();
        public ObservableCollection<AudioClip> Clips
        {
            get => _clips;
            set { _clips = value; OnPropertyChanged(); }
        }

        // Efekti
        public TrackEffects Effects { get; set; } = new();

        /// <summary>Kriva jačine kroz vreme (prazno = bez krive), sortirano po vremenu.</summary>
        public List<EnvelopePoint> Envelope { get; set; } = new();

        /// <summary>Pojačanje (linearno) krive jačine u trenutku t (sekunde na vremenskoj liniji).</summary>
        public float EnvelopeGainAt(double t)
        {
            var env = Envelope;
            if (env.Count == 0) return 1f;
            if (t <= env[0].Time) return Db2(env[0].Db);
            for (int i = 1; i < env.Count; i++)
            {
                if (t <= env[i].Time)
                {
                    var a = env[i - 1]; var b = env[i];
                    double span = b.Time - a.Time;
                    double db = span <= 0 ? b.Db : a.Db + (b.Db - a.Db) * (t - a.Time) / span;
                    return Db2(db);
                }
            }
            return Db2(env[^1].Db);
        }
        private static float Db2(double db) => (float)Math.Pow(10, db / 20);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public enum TrackType { Audio, Vocal, Instrumental, Effects, Bus }

    public enum EffectType
    {
        Equalizer, Reverb, Delay, Compressor, NoiseGate, BassBoost, PitchShift, Chorus
    }

    /// <summary>Tačka krive jačine (envelope): u trenutku Time traka svira Db decibela glasnije/tiše.</summary>
    public class EnvelopePoint
    {
        public double Time { get; set; }
        public double Db { get; set; }
    }

    /// <summary>Oznake pesme za izvoz (ID3 / metapodaci).</summary>
    public class AudioTags
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Year { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Comment { get; set; } = "";
        public string CoverPath { get; set; } = "";
        public bool IsEmpty => string.IsNullOrWhiteSpace(Title + Artist + Album + Year + Genre + Comment + CoverPath);
    }

    public class TrackEffects : INotifyPropertyChanged
    {
        private bool _reverbEnabled; public bool ReverbEnabled { get => _reverbEnabled; set { _reverbEnabled = value; OnPropertyChanged(); } }
        private float _reverbMix = 0.3f; public float ReverbMix { get => _reverbMix; set { _reverbMix = value; OnPropertyChanged(); } }
        private float _reverbRoom = 0.5f; public float ReverbRoom { get => _reverbRoom; set { _reverbRoom = value; OnPropertyChanged(); } }
        private float _reverbDamping = 0.5f; public float ReverbDamping { get => _reverbDamping; set { _reverbDamping = value; OnPropertyChanged(); } }   // 0 = svetli zidovi (pločice), 1 = tamni (zavese, tepih)
        private float _reverbPreDelay = 15f; public float ReverbPreDelay { get => _reverbPreDelay; set { _reverbPreDelay = value; OnPropertyChanged(); } } // ms do prvog odjeka (veći prostor = duže)

        private bool _delayEnabled; public bool DelayEnabled { get => _delayEnabled; set { _delayEnabled = value; OnPropertyChanged(); } }
        private float _delayTime = 0.25f; public float DelayTime { get => _delayTime; set { _delayTime = value; OnPropertyChanged(); } }
        private float _delayFeedback = 0.3f; public float DelayFeedback { get => _delayFeedback; set { _delayFeedback = value; OnPropertyChanged(); } }
        private float _delayMix = 0.4f; public float DelayMix { get => _delayMix; set { _delayMix = value; OnPropertyChanged(); } }

        private bool _compressorEnabled; public bool CompressorEnabled { get => _compressorEnabled; set { _compressorEnabled = value; OnPropertyChanged(); } }
        private float _compThreshold = -20f; public float CompThreshold { get => _compThreshold; set { _compThreshold = value; OnPropertyChanged(); } }
        private float _compRatio = 4f; public float CompRatio { get => _compRatio; set { _compRatio = value; OnPropertyChanged(); } }
        private float _compAttack = 10f; public float CompAttack { get => _compAttack; set { _compAttack = value; OnPropertyChanged(); } }
        private float _compRelease = 100f; public float CompRelease { get => _compRelease; set { _compRelease = value; OnPropertyChanged(); } }
        private float _compMakeup = 0f; public float CompMakeup { get => _compMakeup; set { _compMakeup = value; OnPropertyChanged(); } }   // dB pojačanja posle kompresije

        private bool _eqEnabled; public bool EqEnabled { get => _eqEnabled; set { _eqEnabled = value; OnPropertyChanged(); } }
        // Parametarski EQ: frekvencije opsega, širina srednjeg opsega i filteri koji seku krajeve (0 = isključen)
        private float _eqLowFreq = 200f; public float EqLowFreq { get => _eqLowFreq; set { _eqLowFreq = value; OnPropertyChanged(); } }
        private float _eqMidFreq = 1000f; public float EqMidFreq { get => _eqMidFreq; set { _eqMidFreq = value; OnPropertyChanged(); } }
        private float _eqMidQ = 0.7f; public float EqMidQ { get => _eqMidQ; set { _eqMidQ = value; OnPropertyChanged(); } }
        private float _eqHighFreq = 8000f; public float EqHighFreq { get => _eqHighFreq; set { _eqHighFreq = value; OnPropertyChanged(); } }
        private float _highPassFreq = 0f; public float HighPassFreq { get => _highPassFreq; set { _highPassFreq = value; OnPropertyChanged(); } }
        private float _lowPassFreq = 0f; public float LowPassFreq { get => _lowPassFreq; set { _lowPassFreq = value; OnPropertyChanged(); } }

        /// <summary>Redosled efekata u lancu (prvi u listi obrađuje zvuk prvi).</summary>
        public List<EffectType> Order { get; set; } = DefaultOrder();
        public static List<EffectType> DefaultOrder() => new()
        {
            EffectType.NoiseGate, EffectType.Compressor, EffectType.PitchShift, EffectType.Equalizer,
            EffectType.BassBoost, EffectType.Chorus, EffectType.Delay, EffectType.Reverb
        };

        public TrackEffects CloneSettings()
        {
            var c = (TrackEffects)MemberwiseClone();
            c.Order = new List<EffectType>(Order);
            c.PropertyChanged = null;
            return c;
        }
        private float _eqLow = 0f; public float EqLow { get => _eqLow; set { _eqLow = value; OnPropertyChanged(); } }
        private float _eqMid = 0f; public float EqMid { get => _eqMid; set { _eqMid = value; OnPropertyChanged(); } }
        private float _eqHigh = 0f; public float EqHigh { get => _eqHigh; set { _eqHigh = value; OnPropertyChanged(); } }

        private bool _pitchEnabled; public bool PitchEnabled { get => _pitchEnabled; set { _pitchEnabled = value; OnPropertyChanged(); } }
        private float _pitchSemitones = 0f; public float PitchSemitones { get => _pitchSemitones; set { _pitchSemitones = value; OnPropertyChanged(); } }

        private bool _noiseGateEnabled; public bool NoiseGateEnabled { get => _noiseGateEnabled; set { _noiseGateEnabled = value; OnPropertyChanged(); } }
        private float _gateThreshold = -40f; public float GateThreshold { get => _gateThreshold; set { _gateThreshold = value; OnPropertyChanged(); } }

        private bool _bassBostEnabled; public bool BassBostEnabled { get => _bassBostEnabled; set { _bassBostEnabled = value; OnPropertyChanged(); } }
        private float _bassGain = 6f; public float BassGain { get => _bassGain; set { _bassGain = value; OnPropertyChanged(); } }

        private bool _chorusEnabled; public bool ChorusEnabled { get => _chorusEnabled; set { _chorusEnabled = value; OnPropertyChanged(); } }
        private float _chorusDepth = 0.5f; public float ChorusDepth { get => _chorusDepth; set { _chorusDepth = value; OnPropertyChanged(); } }
        private float _chorusRate = 0.8f; public float ChorusRate { get => _chorusRate; set { _chorusRate = value; OnPropertyChanged(); } }   // Hz
        private float _chorusMix = 0.5f; public float ChorusMix { get => _chorusMix; set { _chorusMix = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AudioProject : INotifyPropertyChanged
    {
        public string Name { get; set; } = Localization.Lang.T("default_project_name");
        public string FilePath { get; set; } = "";
        public int SampleRate { get; set; } = 44100;
        public int BitDepth { get; set; } = 24;
        public double Bpm { get; set; } = 120;
        public ObservableCollection<AudioTrack> Tracks { get; set; } = new();

        /// <summary>Markeri na vremenskoj liniji (sekunde), uvek sortirani rastuće.</summary>
        public ObservableCollection<double> Markers { get; } = new();

        /// <summary>Oznake pesme (naslov, izvođač...) koje se upisuju pri izvozu.</summary>
        public AudioTags Tags { get; set; } = new();

        /// <summary>Kraj poslednjeg klipa u projektu (bez dodatnih 5 s rezerve).</summary>
        public double ContentEnd => Tracks.SelectMany(t => t.Clips).Select(c => c.StartTime + c.Duration).DefaultIfEmpty(0).Max();

        public void RaiseDurationChanged() { OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(ContentEnd)); }

        public double Duration => Tracks.Count == 0 ? 30 :
            Tracks.SelectMany(t => t.Clips).Select(c => c.StartTime + c.Duration).DefaultIfEmpty(30).Max() + 5;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
