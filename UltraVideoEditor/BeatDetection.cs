using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // BEAT DETECTION ENGINE
    // ═══════════════════════════════════════════════════════════════

    public class BeatInfo
    {
        public double BPM { get; set; }
        public double BeatInterval { get; set; }
        public List<double> BeatTimes { get; set; }
        public List<double> DownBeats { get; set; }
        public string TimeSignature { get; set; }
        public double Confidence { get; set; }
        public bool IsValid => BPM > 30 && BPM < 300 && BeatTimes?.Count > 4;

        /// <summary>
        /// True ako je detektovana klavirska/melodijska muzika bez peroksuzivnih onsets.
        /// U Piano modu, rezovi prate melodijske fraze, ne beat-to-beat udarce.
        /// </summary>
        public bool PianoMode { get; set; } = false;

        /// <summary>
        /// Phrase boundary timestampovi za piano mode.
        /// Ovo su tačke promene melodijske fraze (spectral flux promene), 
        /// idealna mesta za rez u klavirskoj muzici.
        /// </summary>
        public List<double> PhraseBeats { get; set; } = new List<double>();

        /// <summary>
        /// Prosječna gustoća nota (0-1). Visoka gustoća = brzi pasaž → kraći kadrovi.
        /// Niska gustoća = tihe/spore fraze → duži kadrovi.
        /// </summary>
        public double NoteDensity { get; set; } = 0.5;

        /// <summary>
        /// Sekunde tišine na početku audio fajla prije prve note.
        /// Video timeline pomjera se za ovaj iznos da nema "mrtvog hoda".
        /// </summary>
        public double AudioStartSeconds { get; set; } = 0.0;

        /// <summary>
        /// Preemptivni offset za rezove: koliko ms RANIJE treba napraviti rez
        /// u odnosu na audio peak, da oko i uho dožive promjenu istovremeno.
        /// Vrijednost: 100–150ms za tipičnu dječiju pjesmu (vokal napad dolazi
        /// prije RMS peaka jer bas/perkusija dolaze nakon vokala).
        /// </summary>
        public double CutAdvanceMs { get; set; } = 120.0;
    }

    public class BeatSyncPlan
    {
        public double ClipDuration { get; set; }
        public int BeatsPerClip { get; set; }
        public string SceneType { get; set; }
        public string Reason { get; set; }
    }

    public static class BeatDetection
    {
        public static async Task<BeatInfo> AnalyzeAudio(
            string audioPath,
            string ffmpegPath,
            CancellationToken ct = default)
        {
            if (!File.Exists(audioPath) || !File.Exists(ffmpegPath))
                return FallbackBeatInfo(120);

            try
            {
                string tempWav = Path.Combine(Path.GetTempPath(), $"beat_{Guid.NewGuid().ToString().Substring(0, 8)}.wav");
                bool extracted = await ExtractMonoAudio(audioPath, tempWav, ffmpegPath, ct);
                if (!extracted) return FallbackBeatInfo(120);

                var energyProfile = await GetEnergyProfile(tempWav, ffmpegPath, ct);
                var beatTimes = DetectBeatsFromEnergy(energyProfile);
                double bpm = CalculateBPM(beatTimes);
                var downBeats = GetDownBeats(beatTimes, bpm);

                // Detekcija početka audija: koliko tišine ima na početku
                double audioStart = await AITranscription.DetectAudioStartAsync(audioPath, ffmpegPath, -40.0, ct);

                // Cut-advance: brže pjesme imaju manji offset (vokal ataci su bliži peaku)
                // Sporije i vokalne pjesme imaju veći offset (vokal dolazi dosta ranije od basa)
                double cutAdvanceMs = bpm > 140 ? 80.0 : bpm > 100 ? 120.0 : 150.0;

                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }

                var beatInfo = new BeatInfo
                {
                    BPM              = Math.Round(bpm, 1),
                    BeatInterval     = bpm > 0 ? Math.Round(60.0 / bpm, 4) : 0.5,
                    BeatTimes        = beatTimes,
                    DownBeats        = downBeats,
                    TimeSignature    = EstimateTimeSignature(bpm),
                    Confidence       = CalculateConfidence(beatTimes, bpm),
                    AudioStartSeconds = audioStart,
                    CutAdvanceMs     = cutAdvanceMs
                };

                // ── PIANO MODE DETECTION ─────────────────────────────────────
                // Klavirska muzika nema perkusivne spikeve → standardni beat detection nije pouzdan.
                // Detekcija: niska confidence ILI neravnomjerni beati → piano/melodijska muzika.
                try
                {
                    bool lowConf = beatInfo.Confidence < 0.45;
                    bool irregular = false;
                    if (beatInfo.BeatTimes?.Count > 4)
                    {
                        var gaps = new List<double>();
                        for (int i = 1; i < beatInfo.BeatTimes.Count; i++)
                            gaps.Add(beatInfo.BeatTimes[i] - beatInfo.BeatTimes[i - 1]);
                        double avgGap = gaps.Average();
                        double stdGap = Math.Sqrt(gaps.Average(g => Math.Pow(g - avgGap, 2)));
                        irregular = avgGap > 0 && stdGap / avgGap > 0.30;
                    }

                    if (lowConf || irregular || !beatInfo.IsValid)
                    {
                        // Učitaj energy profile ponovo za phrase detection
                        var epForPiano = await GetEnergyProfile(
                            File.Exists(tempWav) ? tempWav : audioPath, ffmpegPath, ct);
                        if (epForPiano.Count > 20)
                        {
                            var phraseBeats = DetectPhraseBeats(epForPiano);
                            if (phraseBeats.Count >= 2)
                            {
                                beatInfo.PianoMode = true;
                                beatInfo.PhraseBeats = phraseBeats;

                                double totalDur = epForPiano.Last().time - epForPiano.First().time;
                                int spikes = epForPiano.Count(p =>
                                    p.energy > epForPiano.Average(e => e.energy) * 1.2);
                                beatInfo.NoteDensity = totalDur > 0
                                    ? Math.Min(1.0, spikes / totalDur / 5.0) : 0.5;

                                // Ako beat detection nije bio validan, koristi phrase beats
                                if (!beatInfo.IsValid)
                                {
                                    beatInfo.BeatTimes = phraseBeats;
                                    beatInfo.DownBeats = phraseBeats
                                        .Where((_, idx) => idx % 2 == 0).ToList();
                                    if (phraseBeats.Count > 1)
                                    {
                                        double avgPhr = (phraseBeats.Last() - phraseBeats.First())
                                                        / (phraseBeats.Count - 1);
                                        beatInfo.BPM = Math.Round(60.0 / avgPhr, 1);
                                        beatInfo.BeatInterval = avgPhr;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { /* Piano mode je bonus — ne smije srušiti render */ }

                return beatInfo;
            }
            catch (OperationCanceledException) { throw; }
            catch { return FallbackBeatInfo(120); }
        }

        public static BeatSyncPlan GetSyncPlan(BeatInfo beats, string sceneType, int vibeScore)
        {
            if (!beats.IsValid)
            {
                double fallbackDur = sceneType switch
                {
                    "lullaby" => 6.0,
                    "chorus" => 2.0,
                    "intro" => 5.0,
                    "outro" => 6.0,
                    _ => 4.0
                };
                return new BeatSyncPlan
                {
                    ClipDuration = fallbackDur,
                    BeatsPerClip = 0,
                    SceneType = sceneType,
                    Reason = "Beat sync nije dostupan - koristim fiksno trajanje"
                };
            }

            double interval = beats.BeatInterval;
            int beatsPerClip = (sceneType, vibeScore) switch
            {
                ("lullaby", _) => 8,
                ("outro", _) => 8,
                ("intro", _) => 6,
                ("chorus", >= 8) => 2,
                ("chorus", >= 5) => 4,
                ("chorus", _) => 4,
                (_, >= 8) => 2,
                (_, >= 6) => 4,
                (_, >= 4) => 4,
                _ => 8
            };

            double raw = interval * beatsPerClip;
            while (raw < 0.8 && beatsPerClip < 16)
            {
                beatsPerClip *= 2;
                raw = interval * beatsPerClip;
            }
            while (raw > 8.0 && beatsPerClip > 1)
            {
                beatsPerClip = Math.Max(1, beatsPerClip / 2);
                raw = interval * beatsPerClip;
            }

            double clipDuration = Math.Round(interval * beatsPerClip, 3);
            string reason = $"{beats.BPM:F0} BPM, {beatsPerClip} beata po klipu = {clipDuration:F1}s po klipu ({sceneType}, vibe {vibeScore})";

            return new BeatSyncPlan
            {
                ClipDuration = clipDuration,
                BeatsPerClip = beatsPerClip,
                SceneType = sceneType,
                Reason = reason
            };
        }

        public static void ApplyBeatSync(List<LyricShot> shots, BeatInfo beats, double totalDuration)
        {
            if (!beats.IsValid || shots == null || shots.Count == 0) return;

            // Zajednički offseti za piano i standardni blok — deklarisani jednom ovdje
            double advanceSec = beats.CutAdvanceMs / 1000.0;
            double audioOffset = beats.AudioStartSeconds;

            // ── PIANO MODE: Dynamic pacing baziran na melodijskim frazama ────
            // Gemini preporuka: klavir tih/spor → duži kadrovi, brz/intenzivan → kraći kadrovi.
            // PianoMode=true znači da nemamo perkusivne beatove, koristimo phrase boundaries.
            if (beats.PianoMode && beats.PhraseBeats?.Count >= 2)
            {

                // NoteDensity: 0=tiho/sporo, 1=brzo/gusto
                // Mapiramo na clip duration: tiho→4.5s, prosječno→3.0s, brzo→1.8s
                double baseDuration = 4.5 - beats.NoteDensity * 2.7; // range: 1.8-4.5s

                for (int i = 0; i < shots.Count; i++)
                {
                    var shot = shots[i];

                    // Nearest phrase boundary za ovaj shot
                    double idealStart;
                    if (i < beats.PhraseBeats.Count)
                        idealStart = beats.PhraseBeats[i] + audioOffset;
                    else
                    {
                        // Ekstrapoluj iz zadnje fraze
                        double lastPhrase = beats.PhraseBeats.Last() + audioOffset;
                        idealStart = lastPhrase + (i - beats.PhraseBeats.Count + 1) * baseDuration;
                    }

                    double startTime = Math.Max(0, idealStart - advanceSec);

                    // End: sljedeći phrase boundary ili baseDuration
                    double endTime;
                    if (i + 1 < beats.PhraseBeats.Count)
                        endTime = beats.PhraseBeats[i + 1] + audioOffset - advanceSec;
                    else if (i == shots.Count - 1)
                        endTime = Math.Round(totalDuration, 3);
                    else
                        endTime = startTime + baseDuration;

                    // Dinamički pacing: ako je stih high-energy, skrati kadar
                    double energyMultiplier = 1.0;
                    if (shot.Data?.VibeScore >= 7) energyMultiplier = 0.75; // brz pasaž
                    if (shot.Data?.VibeScore <= 2) energyMultiplier = 1.30; // tiha fraza

                    double duration = Math.Min(8.0, Math.Max(1.0,
                        (endTime - startTime) * energyMultiplier));
                    endTime = startTime + duration;

                    shot.StartSeconds = Math.Round(startTime, 3);
                    shot.EndSeconds   = Math.Round(endTime, 3);
                    shot.Timestamp    = $"{FmtTs(shot.StartSeconds)} - {FmtTs(shot.EndSeconds)}";

                    if (shot.Data != null)
                        shot.Data.MotionIntent = $"{shot.Data.MotionIntent} " +
                            $"[piano phrase {i+1}/{beats.PhraseBeats.Count} " +
                            $"density={beats.NoteDensity:F2} dur={duration:F1}s]";
                }
                return; // Piano mode završen — ne ulazi u standardni beat sync ispod
            }

            // ── STANDARDNI BEAT SYNC (perkusivna muzika) ─────────────────────
            // FIX-BEATSYNC: Koristimo stvarne beat timestamps umesto izračunatog interval × n
            // DownBeats su preferirani snap-tačke (downbeat = 1. udarac u taktu)
            // Ako nema dovoljno downbeats-a, fallback na sve BeatTimes
            var snapPoints = (beats.DownBeats?.Count >= shots.Count)
                ? beats.DownBeats
                : beats.BeatTimes;


            // advanceSec i audioOffset deklarisani gore (zajednički za oba bloka)

            for (int i = 0; i < shots.Count; i++)
            {
                var shot = shots[i];

                string sceneType = shot.IsChorus ? "chorus" :
                    shot.Data?.VibeScore <= 2 ? "lullaby" :
                    i == 0 ? "intro" :
                    i == shots.Count - 1 ? "outro" : "verse";

                // Nađi idealan snap point: najbliži downbeat za ovaj shot index
                double idealStart;
                if (i < snapPoints.Count)
                {
                    idealStart = snapPoints[i] + audioOffset;
                }
                else
                {
                    // Ekstrapoluj iz zadnjeg poznatog snap pointa + beat interval
                    double lastSnap = snapPoints[snapPoints.Count - 1] + audioOffset;
                    int extra = i - snapPoints.Count + 1;
                    idealStart = lastSnap + extra * beats.BeatInterval;
                }

                // Preemptivni offset: rez 100-150ms RANIJE od audio peaka
                double startTime = Math.Max(0, idealStart - advanceSec);

                // End = snap point sledećeg shot-a (minus advance) ili kraj pesme
                double endTime;
                if (i + 1 < snapPoints.Count)
                {
                    double nextIdeal = snapPoints[i + 1] + audioOffset;
                    endTime = Math.Max(startTime + 0.5, nextIdeal - advanceSec);
                }
                else if (i == shots.Count - 1)
                {
                    endTime = Math.Round(totalDuration, 3);
                }
                else
                {
                    // Ekstrapoluj
                    endTime = startTime + beats.BeatInterval * (shot.Data?.VibeScore >= 7 ? 2 : 4);
                }

                // Zaštita: minimalno 0.5s, maksimalno 10s po klipcu
                double duration = Math.Min(10.0, Math.Max(0.5, endTime - startTime));
                endTime = startTime + duration;

                shot.StartSeconds = Math.Round(startTime, 3);
                shot.EndSeconds   = Math.Round(endTime, 3);
                shot.Timestamp    = $"{FmtTs(shot.StartSeconds)} - {FmtTs(shot.EndSeconds)}";

                var plan = GetSyncPlan(beats, sceneType, shot.Data?.VibeScore ?? 5);
                if (shot.Data != null && plan.BeatsPerClip > 0)
                    shot.Data.MotionIntent = $"{shot.Data.MotionIntent} [beat snap: {plan.BeatsPerClip}b @ {shot.StartSeconds:F2}s]";
            }
        }

        private static async Task<bool> ExtractMonoAudio(string input, string output, string ffmpegPath, CancellationToken ct)
        {
            string args = $"-nostdin -i \"{input}\" -ac 1 -ar 22050 -vn -y \"{output}\"";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode == 0 && File.Exists(output);
            }
            catch { return false; }
        }

        private static async Task<List<(double time, double energy)>> GetEnergyProfile(string wavPath, string ffmpegPath, CancellationToken ct)
        {
            string tempLog = Path.Combine(Path.GetTempPath(), $"energy_{Guid.NewGuid().ToString().Substring(0, 8)}.txt");
            string args = $"-nostdin -i \"{wavPath}\" -af \"astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file={tempLog}\" -f null -";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync(ct);
            }
            catch { }

            var profile = new List<(double time, double energy)>();
            if (!File.Exists(tempLog)) return profile;

            string logContent = await File.ReadAllTextAsync(tempLog, ct);
            try { File.Delete(tempLog); } catch { }

            var lines = logContent.Split('\n');
            double currentTime = 0;
            foreach (var line in lines)
            {
                if (line.Contains("pts_time:"))
                {
                    var m = Regex.Match(line, @"pts_time:([\d.]+)");
                    if (m.Success)
                        double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out currentTime);
                }
                else if (line.Contains("RMS_level="))
                {
                    var m = Regex.Match(line, @"RMS_level=([-\d.]+|inf|-inf)");
                    if (m.Success)
                    {
                        string valStr = m.Groups[1].Value;
                        double rms = valStr == "-inf" || valStr == "inf" ? -100.0 : double.Parse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture);
                        double linear = rms > -100 ? Math.Pow(10, rms / 20.0) : 0;
                        profile.Add((currentTime, linear));
                    }
                }
            }
            return profile;
        }

        // ── PHRASE BEAT DETECTION — za klavirsku/melodijsku muziku ───────────
        // Umjesto peroksuzivnih spikeva, tražimo granice melodijskih fraza:
        // tačke gdje se energija nagli mijenja (spectral flux / energy flux).
        // Ovo daje prirodne točke reza za klavir, violinu, melodiju bez bubnja.
        private static List<double> DetectPhraseBeats(List<(double time, double energy)> profile)
        {
            if (profile.Count < 20) return new List<double>();

            var phrases = new List<double>();

            // Smooth profil da smanjimo sitne fluktacije
            int smoothWin = Math.Max(3, profile.Count / 30);
            var smoothed = new double[profile.Count];
            for (int i = 0; i < profile.Count; i++)
            {
                int start = Math.Max(0, i - smoothWin);
                int end   = Math.Min(profile.Count - 1, i + smoothWin);
                smoothed[i] = profile.Skip(start).Take(end - start + 1).Average(p => p.energy);
            }

            // Računaj energy flux = promjena energije između susjednih prozora
            var flux = new double[profile.Count];
            for (int i = 1; i < profile.Count; i++)
                flux[i] = Math.Abs(smoothed[i] - smoothed[i - 1]);

            // Pragovi za detekciju granica fraza
            double avgFlux = flux.Average();
            double stdFlux = Math.Sqrt(flux.Average(f => Math.Pow(f - avgFlux, 2)));
            double threshold = avgFlux + stdFlux * 1.2;

            // Minimalni razmak između fraza: 1.5s (kratka fraza) do 8s (duga fraza)
            double minPhraseGap = 1.5;
            double lastPhrase = -10.0;

            for (int i = smoothWin; i < profile.Count - smoothWin; i++)
            {
                if (flux[i] > threshold && profile[i].time - lastPhrase >= minPhraseGap)
                {
                    // Provjeri da je ovo lokalni maksimum flux-a
                    bool isLocalMax = true;
                    for (int j = Math.Max(0, i - 3); j <= Math.Min(profile.Count - 1, i + 3); j++)
                        if (j != i && flux[j] >= flux[i]) { isLocalMax = false; break; }

                    if (isLocalMax)
                    {
                        phrases.Add(Math.Round(profile[i].time, 3));
                        lastPhrase = profile[i].time;
                    }
                }
            }

            // Ako nema dovoljno phrase boundariesa, generiši ravnomjerne rezove
            // Koristimo 4s kao tipičnu muzičku frazu (1 takt @ 60 BPM = 4s)
            if (phrases.Count < 3 && profile.Count > 0)
            {
                double start = profile.First().time;
                double end   = profile.Last().time;
                double interval = 4.0; // 4 sekunde ≈ 1 muzički takt
                for (double t = start + interval; t < end - 1.0; t += interval)
                    phrases.Add(Math.Round(t, 3));
            }

            return phrases.OrderBy(t => t).ToList();
        }

        private static List<double> DetectBeatsFromEnergy(List<(double time, double energy)> profile)
        {
            if (profile.Count < 10) return new List<double>();

            int windowSize = Math.Max(10, profile.Count / 20);
            var beats = new List<double>();
            double minBeatGap = 0.2;
            double lastBeat = -1.0;

            for (int i = windowSize; i < profile.Count - windowSize; i++)
            {
                double localAvg = profile.Skip(i - windowSize).Take(windowSize * 2).Average(p => p.energy);
                double current = profile[i].energy;
                bool isLocalMax = current > profile[i - 1].energy && current > profile[i + 1].energy;
                bool aboveThreshold = current > localAvg * 1.3;

                if (isLocalMax && aboveThreshold)
                {
                    double t = profile[i].time;
                    if (t - lastBeat >= minBeatGap)
                    {
                        beats.Add(t);
                        lastBeat = t;
                    }
                }
            }
            return beats;
        }

        private static double CalculateBPM(List<double> beatTimes)
        {
            if (beatTimes.Count < 4) return 120;

            var intervals = new List<double>();
            for (int i = 1; i < beatTimes.Count; i++)
                intervals.Add(beatTimes[i] - beatTimes[i - 1]);

            intervals.Sort();
            double median = intervals[intervals.Count / 2];
            var filtered = intervals.Where(x => x > median * 0.5 && x < median * 2.0).ToList();

            if (filtered.Count == 0) return 120;

            double avgInterval = filtered.Average();
            double bpm = 60.0 / avgInterval;

            while (bpm < 60) bpm *= 2;
            while (bpm > 200) bpm /= 2;

            return bpm;
        }

        private static List<double> GetDownBeats(List<double> beats, double bpm)
        {
            if (beats.Count == 0) return new List<double>();
            int step = bpm < 80 ? 3 : 4;
            return beats.Where((_, idx) => idx % step == 0).ToList();
        }

        private static string EstimateTimeSignature(double bpm)
        {
            if (bpm >= 55 && bpm <= 85) return "3/4 (valcer)";
            return "4/4";
        }

        private static double CalculateConfidence(List<double> beats, double bpm)
        {
            if (beats.Count < 4) return 0;
            double interval = 60.0 / bpm;
            int consistent = 0;
            for (int i = 1; i < beats.Count; i++)
            {
                double diff = Math.Abs((beats[i] - beats[i - 1]) - interval);
                if (diff < interval * 0.15) consistent++;
            }
            return Math.Round((double)consistent / (beats.Count - 1), 2);
        }

        private static BeatInfo FallbackBeatInfo(double bpm)
        {
            double interval = 60.0 / bpm;
            return new BeatInfo
            {
                BPM = bpm,
                BeatInterval = interval,
                BeatTimes = new List<double>(),
                DownBeats = new List<double>(),
                TimeSignature = "4/4",
                Confidence = 0
            };
        }

        private static string FmtTs(double s) => TimeSpan.FromSeconds(s).ToString(@"m\:ss\.ff");

        // ══════════════════════════════════════════════════════════════════════
        //  GetClipSpeedFactor — izračunaj faktor ubrzanja/usporavanja klipa
        //  da vizuelni pokret prati BPM (rješava problem tramboline).
        //
        //  Logika:
        //  - Uzmemo prosječan tempo vizuelnog pokreta u klipu (MotionMagnitude)
        //  - Poredimo sa audio BPM konvertovanim u "pokrete po sekundi"
        //  - Vraćamo setpts faktor za FFmpeg: < 1.0 = ubrzaj, > 1.0 = uspori
        //
        //  Primjer: BPM=120, klip 25fps, prosječna magnituda 15px/frame
        //  → 2 udarca/s, klip ima ~15 pokreta/s → faktor ≈ 1.0 (OK)
        //  → BPM=80 → 1.33 udarca/s, klip je brz → faktor = 1.5 (uspori)
        //
        //  VAŽNO: faktor je ograničen na [0.5, 2.0] — drastičnije promjene
        //  izgledaju artificijelno na djeci 3-7g.
        // ══════════════════════════════════════════════════════════════════════
        public static double GetClipSpeedFactor(BeatInfo beats, double clipMotionMagnitude, string sceneType)
        {
            // Sporim scenama (lullaby, outro, intro) ne mijenjamo brzinu
            if (sceneType is "lullaby" or "outro" or "intro") return 1.0;
            if (!beats.IsValid || clipMotionMagnitude <= 0) return 1.0;

            // Konvertuj BPM u "udaraca po sekundi"
            double beatsPerSec = beats.BPM / 60.0;

            // Prosječni vizuelni pokret koji "prati" jedan udarac
            // Pretpostavka: klip je sniman na 25fps, "normalan" pokret = 8px/frame
            const double NORMAL_MOTION_PER_SEC = 8.0 * 25.0; // = 200 px/s
            double clipMotionPerSec = clipMotionMagnitude * 25.0;

            // Željeni pokret po sekundi = beatsPerSec × NORMAL_MOTION_PER_SEC
            double targetMotion = beatsPerSec * NORMAL_MOTION_PER_SEC / 2.0;

            if (targetMotion <= 0 || clipMotionPerSec <= 0) return 1.0;

            // setpts faktor: > 1.0 = sporiji video (pts se množi = duže)
            //                < 1.0 = brži video
            double factor = clipMotionPerSec / targetMotion;

            // Ograniči na razumne granice
            factor = Math.Max(0.6, Math.Min(1.8, factor));

            // Zaokruži na 2 decimale
            return Math.Round(factor, 2);
        }
    }
}