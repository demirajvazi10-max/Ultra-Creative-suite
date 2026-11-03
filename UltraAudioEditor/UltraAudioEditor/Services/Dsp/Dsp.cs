using NAudio.Dsp;

namespace UltraAudioEditor.Services.Dsp
{
    // ════════════════════════════════════════════════════════════════════════
    // Obrada zvuka nad baferom u memoriji (Sound Forge stil: izabereš deo, primeniš
    // obradu, rezultat se upiše u novi WAV). Svi bafere su STEREO, preplitani
    // (L, D, L, D ...), float -1..1, na frekvenciji projekta. Nijedna funkcija ovde
    // ne zna za WPF — sve se može testirati i van Windows-a.
    // ════════════════════════════════════════════════════════════════════════

    public enum FadeCurve { Linear, Fast, Slow, SCurve }

    public sealed class AudioStats
    {
        public double Seconds;
        public double PeakDb;
        public double RmsDb;
        public double Lufs;            // integrisana glasnoća (EBU R128 / ITU BS.1770)
        public double LoudestMomentLufs;
        public int ClippedSamples;
        public double FirstClipSeconds = -1;
        public double DcOffsetPercent;
        public double SilentPercent;   // udeo vremena ispod -50 dB
    }

    public static class Fft
    {
        /// <summary>Kompleksni FFT u mestu (radix-2). n mora biti stepen dvojke. inverse = true za IFFT (sa deljenjem sa n).</summary>
        public static void Transform(float[] re, float[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    public static class Loudness
    {
        // K-weighting filter (ITU-R BS.1770): visoka polica +4 dB oko 1.5 kHz + high-pass ~38 Hz.
        private static BiQuadFilter[] KFilters(int sr) => new[]
        {
            BiQuadFilter.HighShelf(sr, 1681.97f, 0.7071f, 4.0f),
            BiQuadFilter.HighPassFilter(sr, 38.13f, 0.5003f)
        };

        /// <summary>Integrisana glasnoća u LUFS (sa apsolutnim -70 i relativnim -10 LU gejtom) i najglasniji trenutak (400 ms).</summary>
        public static (double integrated, double loudestMoment) Measure(float[] st, int sr)
        {
            int frames = st.Length / 2;
            if (frames < sr / 10) return (-70, -70);
            var fl = KFilters(sr); var fr = KFilters(sr);
            int block = (int)(sr * 0.4), hop = (int)(sr * 0.1);
            // kvadrati K-filtriranog signala po 100 ms koraku
            int steps = frames / hop;
            var hopEnergy = new double[steps + 1];
            for (int f = 0; f < frames; f++)
            {
                float l = st[2 * f], r = st[2 * f + 1];
                foreach (var b in fl) l = b.Transform(l);
                foreach (var b in fr) r = b.Transform(r);
                hopEnergy[f / hop] += l * (double)l + r * (double)r;
            }
            var blocks = new List<double>();
            int perBlock = block / hop;   // 4
            for (int s = 0; s + perBlock <= steps; s++)
            {
                double e = 0; for (int k = 0; k < perBlock; k++) e += hopEnergy[s + k];
                blocks.Add(e / (perBlock * hop));
            }
            if (blocks.Count == 0) return (-70, -70);
            static double ToLufs(double ms) => ms <= 1e-12 ? -120 : -0.691 + 10 * Math.Log10(ms);
            double loudest = blocks.Max(ToLufs);
            var abs = blocks.Where(b => ToLufs(b) > -70).ToList();
            if (abs.Count == 0) return (-70, loudest);
            double relGate = ToLufs(abs.Average()) - 10;
            var rel = abs.Where(b => ToLufs(b) > relGate).ToList();
            return (rel.Count == 0 ? -70 : ToLufs(rel.Average()), loudest);
        }
    }

    public static class Processing
    {
        public static double Db(double lin) => lin <= 1e-9 ? -120 : 20 * Math.Log10(lin);
        public static float FromDb(double db) => (float)Math.Pow(10, db / 20);

        public static float Peak(float[] b) { float p = 0; foreach (var s in b) { float a = Math.Abs(s); if (a > p) p = a; } return p; }

        public static void Gain(float[] b, double db) { float g = FromDb(db); for (int i = 0; i < b.Length; i++) b[i] *= g; }

        /// <summary>Normalizacija po vrhu na zadat nivo (npr. -1 dB). Vraća primenjeno pojačanje u dB.</summary>
        public static double NormalizePeak(float[] b, double targetDb)
        {
            float p = Peak(b); if (p < 1e-6f) return 0;
            double g = targetDb - Db(p); Gain(b, g); return g;
        }

        /// <summary>
        /// Normalizacija po glasnoći (LUFS). Pojačanje se ograničava tako da vrh ne pređe
        /// maxPeakDb — tada se cilj ne dostiže do kraja, i to se vraća kao limited = true.
        /// </summary>
        public static (double gainDb, double resultLufs, bool limited) NormalizeLufs(float[] b, int sr, double target, double maxPeakDb = -1)
        {
            var (lufs, _) = Loudness.Measure(b, sr);
            if (lufs <= -69) return (0, lufs, false);
            double g = target - lufs;
            double peakAfter = Db(Peak(b)) + g;
            bool limited = false;
            if (peakAfter > maxPeakDb) { g -= peakAfter - maxPeakDb; limited = true; }
            Gain(b, g);
            return (g, lufs + g, limited);
        }

        private static double Curve(double x, FadeCurve c) => c switch
        {
            FadeCurve.Fast   => Math.Sqrt(x),                      // brzo raste (logaritamski osećaj)
            FadeCurve.Slow   => x * x,                             // sporo pa naglo (eksponencijalno)
            FadeCurve.SCurve => 0.5 - 0.5 * Math.Cos(Math.PI * x), // meko na oba kraja
            _                => x
        };

        public static void FadeIn(float[] b, int sr, double seconds, FadeCurve c)
        {
            int frames = b.Length / 2, n = Math.Min(frames, (int)(seconds * sr));
            for (int f = 0; f < n; f++) { float g = (float)Curve(f / (double)n, c); b[2 * f] *= g; b[2 * f + 1] *= g; }
        }

        public static void FadeOut(float[] b, int sr, double seconds, FadeCurve c)
        {
            int frames = b.Length / 2, n = Math.Min(frames, (int)(seconds * sr));
            for (int k = 0; k < n; k++) { int f = frames - 1 - k; float g = (float)Curve(k / (double)n, c); b[2 * f] *= g; b[2 * f + 1] *= g; }
        }

        public static void Reverse(float[] b)
        {
            int frames = b.Length / 2;
            for (int i = 0, j = frames - 1; i < j; i++, j--)
            {
                (b[2 * i], b[2 * j]) = (b[2 * j], b[2 * i]);
                (b[2 * i + 1], b[2 * j + 1]) = (b[2 * j + 1], b[2 * i + 1]);
            }
        }

        public static void Silence(float[] b) => Array.Clear(b);

        public static void ToMono(float[] b) { for (int i = 0; i + 1 < b.Length; i += 2) { float m = (b[i] + b[i + 1]) * 0.5f; b[i] = b[i + 1] = m; } }

        public static void SwapChannels(float[] b) { for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]); }

        public static void RemoveDc(float[] b)
        {
            // 1-polni high-pass ~5 Hz po kanalu (bez promene zvuka, samo skida pomeraj nule)
            for (int c = 0; c < 2; c++)
            {
                float x1 = 0, y1 = 0; const float R = 0.9993f;
                for (int i = c; i < b.Length; i += 2) { float x = b[i]; float y = x - x1 + R * y1; x1 = x; y1 = y; b[i] = y; }
            }
        }

        public static void Filter(float[] b, Func<BiQuadFilter> make)
        {
            for (int c = 0; c < 2; c++)
            {
                var f = make();
                for (int i = c; i < b.Length; i += 2) b[i] = f.Transform(b[i]);
            }
        }

        public static void HighPass(float[] b, int sr, float hz) { Filter(b, () => BiQuadFilter.HighPassFilter(sr, hz, 0.7071f)); Filter(b, () => BiQuadFilter.HighPassFilter(sr, hz, 0.7071f)); }
        public static void LowPass(float[] b, int sr, float hz)  { Filter(b, () => BiQuadFilter.LowPassFilter(sr, hz, 0.7071f));  Filter(b, () => BiQuadFilter.LowPassFilter(sr, hz, 0.7071f)); }

        /// <summary>Uklanjanje brujanja struje: uski notch filteri na osnovnoj (50 ili 60 Hz) i 5 harmonika.</summary>
        public static void RemoveHum(float[] b, int sr, float baseHz)
        {
            for (int h = 1; h <= 6; h++)
            {
                float f = baseHz * h; if (f >= sr / 2f - 100) break;
                Filter(b, () => BiQuadFilter.NotchFilter(sr, f, 30f));
            }
        }

        /// <summary>
        /// Uklanjanje klikova i pucketanja: uzorak koji iskače daleko iznad lokalne
        /// energije (nagli skok koji nije deo talasa) zamenjuje se glatkom interpolacijom.
        /// sensitivity 1 (blago) .. 3 (jako). Vraća broj popravljenih klikova.
        /// </summary>
        public static int RemoveClicks(float[] b, int sr, double sensitivity)
        {
            int fixedCount = 0;
            int win = Math.Max(16, sr / 200);            // 5 ms okolina
            int maxLen = Math.Max(4, sr / 1000 * 2);      // klik najviše ~2 ms
            double k = 12.0 / Math.Clamp(sensitivity, 0.5, 4);
            for (int c = 0; c < 2; c++)
            {
                int frames = b.Length / 2;
                var x = new float[frames];
                for (int f = 0; f < frames; f++) x[f] = b[2 * f + c];
                // druga razlika ističe naglu promenu
                var d = new float[frames];
                for (int f = 1; f < frames - 1; f++) d[f] = Math.Abs(x[f + 1] - 2 * x[f] + x[f - 1]);
                double run = 0; var avg = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    run += d[f]; if (f >= win) run -= d[f - win];
                    avg[f] = (float)(run / Math.Min(f + 1, win));
                }
                int fpos = win;
                while (fpos < frames - win)
                {
                    double local = avg[Math.Max(0, fpos - win / 2)] + 1e-5;
                    if (d[fpos] > k * local && d[fpos] > 0.02)
                    {
                        int s = Math.Max(1, fpos - 2), e = fpos + 1;
                        while (e < frames - 1 && e - s < maxLen && d[e] > k * 0.5 * local) e++;
                        e = Math.Min(frames - 2, e + 2);
                        float a = x[s - 1], z = x[e + 1];
                        for (int f = s; f <= e; f++)
                        {
                            double t = (f - s + 1) / (double)(e - s + 2);
                            double sm = 0.5 - 0.5 * Math.Cos(Math.PI * t);
                            x[f] = (float)(a + (z - a) * sm);
                        }
                        fixedCount++;
                        fpos = e + win / 4;
                    }
                    else fpos++;
                }
                for (int f = 0; f < frames; f++) b[2 * f + c] = x[f];
            }
            return fixedCount;
        }

        // ── Uklanjanje šuma po profilu (spektralno oduzimanje) ──────────────
        public const int NoiseFft = 2048;

        /// <summary>Profil šuma = prosečan spektar (magnituda po frekvenciji) dela gde je samo šum.</summary>
        public static float[] LearnNoiseProfile(float[] st)
        {
            int n = NoiseFft, hop = n / 4, frames = st.Length / 2;
            var prof = new double[n / 2 + 1]; int count = 0;
            var re = new float[n]; var im = new float[n]; var win = Hann(n);
            for (int pos = 0; pos + n <= frames; pos += hop)
            {
                for (int i = 0; i < n; i++) { re[i] = (st[2 * (pos + i)] + st[2 * (pos + i) + 1]) * 0.5f * win[i]; im[i] = 0; }
                Fft.Transform(re, im, false);
                for (int k = 0; k <= n / 2; k++) prof[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                count++;
            }
            if (count == 0) return Array.Empty<float>();
            return prof.Select(v => (float)(v / count)).ToArray();
        }

        /// <summary>
        /// Uklanja šum naučen u profilu. reductionDb = koliko se šum stiša (npr. 12 dB),
        /// sensitivity 1..3 = koliko iznad šuma se signal još smatra šumom.
        /// STFT sa 75% preklapanjem i glatkim pojačanjem po frekvenciji, da ne ostane
        /// "metalni" zvuk.
        /// </summary>
        public static void ReduceNoise(float[] st, float[] profile, double reductionDb, double sensitivity)
        {
            int n = NoiseFft, hop = n / 4, frames = st.Length / 2;
            if (profile.Length != n / 2 + 1 || frames < n) return;
            float floor = FromDb(-Math.Abs(reductionDb));
            double over = 1.0 + Math.Clamp(sensitivity, 0.5, 4) * 0.75;
            var win = Hann(n);
            for (int c = 0; c < 2; c++)
            {
                var input = new float[frames + 2 * n];
                for (int f = 0; f < frames; f++) input[f + n] = st[2 * f + c];
                var output = new float[input.Length];
                var norm = new float[input.Length];
                var re = new float[n]; var im = new float[n];
                var prevGain = new float[n / 2 + 1]; Array.Fill(prevGain, 1f);
                for (int pos = 0; pos + n <= input.Length; pos += hop)
                {
                    for (int i = 0; i < n; i++) { re[i] = input[pos + i] * win[i]; im[i] = 0; }
                    Fft.Transform(re, im, false);
                    for (int k = 0; k <= n / 2; k++)
                    {
                        double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) + 1e-12;
                        double g = 1.0 - over * profile[k] / mag;
                        float gain = (float)Math.Max(floor, Math.Max(0, g));
                        gain = 0.6f * gain + 0.4f * prevGain[k];   // glatko kroz vreme
                        prevGain[k] = gain;
                        re[k] *= gain; im[k] *= gain;
                        if (k > 0 && k < n / 2) { re[n - k] = re[k]; im[n - k] = -im[k]; }
                    }
                    Fft.Transform(re, im, true);
                    for (int i = 0; i < n; i++) { output[pos + i] += re[i] * win[i]; norm[pos + i] += win[i] * win[i]; }
                }
                for (int f = 0; f < frames; f++)
                {
                    float w = norm[f + n];
                    st[2 * f + c] = w > 1e-6f ? output[f + n] / w : 0;
                }
            }
        }

        private static float[] Hann(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            return w;
        }

        // ── Statistika ──────────────────────────────────────────────────────
        public static AudioStats Analyze(float[] st, int sr)
        {
            var s = new AudioStats { Seconds = st.Length / 2.0 / sr };
            float peak = 0; double sum2 = 0, sumL = 0, sumR = 0; int clipped = 0;
            for (int i = 0; i < st.Length; i++)
            {
                float a = Math.Abs(st[i]);
                if (a > peak) peak = a;
                sum2 += st[i] * (double)st[i];
                if ((i & 1) == 0) sumL += st[i]; else sumR += st[i];
                if (a >= 0.999f) { clipped++; if (s.FirstClipSeconds < 0) s.FirstClipSeconds = (i / 2) / (double)sr; }
            }
            s.PeakDb = Db(peak);
            s.RmsDb = st.Length == 0 ? -120 : Db(Math.Sqrt(sum2 / st.Length));
            s.ClippedSamples = clipped;
            int frames = st.Length / 2;
            s.DcOffsetPercent = frames == 0 ? 0 : Math.Max(Math.Abs(sumL), Math.Abs(sumR)) / frames * 100;
            (s.Lufs, s.LoudestMomentLufs) = Loudness.Measure(st, sr);
            // tišina: 50 ms prozori ispod -50 dB
            int w = Math.Max(1, sr / 20), silent = 0, total = 0;
            for (int pos = 0; pos + w <= frames; pos += w)
            {
                double e = 0; for (int f = pos; f < pos + w; f++) e += st[2 * f] * st[2 * f] + st[2 * f + 1] * st[2 * f + 1];
                if (Db(Math.Sqrt(e / (2 * w))) < -50) silent++;
                total++;
            }
            s.SilentPercent = total == 0 ? 0 : silent * 100.0 / total;
            return s;
        }

        /// <summary>
        /// Pronalazi tišine: vraća listu (početak, kraj) u sekundama za svaki deo duži od
        /// minSilence gde je zvuk stalno ispod thresholdDb (mereno u 20 ms prozorima).
        /// </summary>
        public static List<(double start, double end)> FindSilences(float[] st, int sr, double thresholdDb, double minSilence)
        {
            var list = new List<(double, double)>();
            int frames = st.Length / 2, w = Math.Max(1, sr / 50);
            int runStart = -1;
            for (int pos = 0; pos + w <= frames; pos += w)
            {
                double e = 0; for (int f = pos; f < pos + w; f++) e += st[2 * f] * st[2 * f] + st[2 * f + 1] * st[2 * f + 1];
                bool quiet = Db(Math.Sqrt(e / (2 * w))) < thresholdDb;
                if (quiet && runStart < 0) runStart = pos;
                if (!quiet && runStart >= 0)
                {
                    double a = runStart / (double)sr, b = pos / (double)sr;
                    if (b - a >= minSilence) list.Add((a, b));
                    runStart = -1;
                }
            }
            if (runStart >= 0)
            {
                double a = runStart / (double)sr, b = frames / (double)sr;
                if (b - a >= minSilence) list.Add((a, b));
            }
            return list;
        }
    }
}
