using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// Lokalna Whisper AI transkripcija — bez interneta, bez API kljuca.
    /// Koristi whisper.exe (Python whisper CLI) ili faster-whisper-xxl.exe.
    /// Vraca stihove sa timestamp-ovima za sinhronizaciju kadrova.
    /// </summary>
    public static class AITranscription
    {
        // ── Rezultat transkripcije ────────────────────────────────────────────
        public class TranscriptionResult
        {
            public string FullText { get; set; } = "";
            public List<TimedLine> Lines { get; set; } = new();
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
            /// <summary>
            /// Word-level timestamps iz Whisper --word_timestamps True.
            /// Key = word (lowercase, bez interpunkcije), Value = apsolutna sekunda.
            /// Za stih "Leti kupite sladoled" → {"leti":1.2, "kupite":1.6, "sladoled":2.1}
            /// </summary>
            public List<WordTiming> WordTimings { get; set; } = new();
        }

        public class TimedLine
        {
            public double StartSeconds { get; set; }
            public double EndSeconds   { get; set; }
            public string Text         { get; set; } = "";
        }

        /// <summary>
        /// Jedna izgovorena riječ sa tačnom sekundom početka.
        /// Ovo je osnova za word-level video sync:
        /// kad pjevačica kaže "sladoled" → API query "ice cream" startuje tačno tada.
        /// </summary>
        public class WordTiming
        {
            public string Word        { get; set; } = "";
            public double StartSecond { get; set; }
            public double EndSecond   { get; set; }
            /// <summary>Whisper confidence 0.0–1.0 za ovu riječ</summary>
            public double Probability { get; set; } = 1.0;
        }

        // ── Pretraga Whisper izvrsne datoteke ─────────────────────────────────
        private static string FindWhisperExecutable()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "whisper.exe"),
                Path.Combine(appDir, "whisper-cli.exe"),
                Path.Combine(appDir, "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Whisper", "whisper.exe"),
                Path.Combine(appDir, "Whisper", "faster-whisper-xxl.exe"),
                Path.Combine(appDir, "Tools", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python311", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python312", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "Local", "Programs", "Python", "Python310", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "miniconda3", "Scripts", "whisper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "anaconda3", "Scripts", "whisper.exe"),
            };

            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo("where", "whisper")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    string first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            }
            catch { }

            return null;
        }

        
        // Language helper
        private static string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public static bool IsWhisperAvailable() => FindWhisperExecutable() != null;

        private static async Task<string> ExtractAudioAsync(string mediaPath, string tempDir, string ffmpegPath)
        {
            string outPath = Path.Combine(tempDir, $"whisper_audio_{Guid.NewGuid():N}.wav");
            string args = $"-nostdin -i \"{mediaPath}\" -ar 16000 -ac 1 -c:a pcm_s16le -y \"{outPath}\"";

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            await Task.Run(() => proc.WaitForExit());
            return File.Exists(outPath) ? outPath : null;
        }

        private static List<TimedLine> ParseWhisperSrt(string srtPath)
        {
            var lines = new List<TimedLine>();
            if (!File.Exists(srtPath)) return lines;

            string content = File.ReadAllText(srtPath, Encoding.UTF8);
            var blocks = Regex.Split(content.Trim(), @"\r?\n\r?\n");

            foreach (var block in blocks)
            {
                var blockLines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (blockLines.Length < 3) continue;

                var timeMatch = Regex.Match(blockLines[1],
                    @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
                if (!timeMatch.Success) continue;

                double start = int.Parse(timeMatch.Groups[1].Value) * 3600
                             + int.Parse(timeMatch.Groups[2].Value) * 60
                             + int.Parse(timeMatch.Groups[3].Value)
                             + int.Parse(timeMatch.Groups[4].Value.Trim()) / 1000.0;

                double end = int.Parse(timeMatch.Groups[5].Value) * 3600
                           + int.Parse(timeMatch.Groups[6].Value) * 60
                           + int.Parse(timeMatch.Groups[7].Value)
                           + int.Parse(timeMatch.Groups[8].Value) / 1000.0;

                string text = string.Join(" ", blockLines.Skip(2)).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new TimedLine { StartSeconds = start, EndSeconds = end, Text = text });
            }

            return lines;
        }

        public static async Task<TranscriptionResult> TranscribeAsync(
            string mediaPath,
            string language = "sr",
            string ffmpegPath = null,
            string modelSize = "large-v3",
            IProgress<string> progress = null,
            CancellationToken ct = default)
        {
            var result = new TranscriptionResult();

            string whisperExe = FindWhisperExecutable();
            if (whisperExe == null)
            {
                result.ErrorMessage =
                    "Whisper not found on this computer.\n\n" +
                    "Install it in one of these ways:\n\n" +
                    "OPTION A — Python (recommended):\n" +
                    "  pip install openai-whisper\n\n" +
                    "OPTION B — Standalone (no Python):\n" +
                    "  Download faster-whisper-xxl.exe from GitHub\n" +
                    "  and place it next to UltraVideoEditor.exe\n\n" +
                    "After installation, restart the application.";
                return result;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
                ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                result.ErrorMessage = L("re_ffmpeg_missing");
                return result;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"UVE_Whisper_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                progress?.Report("Ekstrahujem audio...");
                string audioPath = mediaPath;

                string ext = Path.GetExtension(mediaPath).ToLower();
                if (ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm")
                {
                    audioPath = await ExtractAudioAsync(mediaPath, tempDir, ffmpegPath);
                    if (audioPath == null)
                    {
                        result.ErrorMessage = L("at_extract_error");
                        return result;
                    }
                }

                progress?.Report($"Whisper analizira audio (model: {modelSize})...");

                bool isFasterWhisper = whisperExe.ToLower().Contains("faster-whisper");
                string whisperArgs = isFasterWhisper
                    ? $"\"{audioPath}\" --model {modelSize} --language {language} " +
                      $"--output_format srt --output_dir \"{tempDir}\" " +
                      $"--compute_type float16 " +
                      $"--beam_size 5 " +
                      $"--best_of 5 " +
                      $"--temperature 0 " +
                      // VAD filter: preskace segmente bez ljudskog glasa (instrument, tišina).
                      // threshold=0.6 znaci da segment mora biti 60%+ siguran da ima glas.
                      // min_silence_duration_ms=500 = pauza kraca od 0.5s se ne tretira kao tišina.
                      // BEZ OVOGA: Whisper halucinira tekst tokom 10s instrumentalnog uvoda
                      // i gura prvi titl na 00:06 umjesto na 00:12 kada glas stvarno pocinje.
                      $"--vad_filter True " +
                      $"--vad_parameters \"threshold=0.6,min_silence_duration_ms=500\" " +
                      // condition_on_previous_text=False: svaki segment se dekodira nezavisno.
                      // Bez ovoga model "prepisuje" kontekst prethodnog segmenta u tišinu
                      // i izmišlja tekst koji jos nije izgovoren — early-start bug.
                      $"--condition_on_previous_text False"
                    : $"\"{audioPath}\" --model {modelSize} --language {language} " +
                      $"--output_format srt --output_dir \"{tempDir}\" " +
                      // no_speech_threshold=0.6: openai-whisper ekvivalent VAD-a.
                      // Segmenti ispod praga se oznacavaju kao tišina i preskacu.
                      $"--no_speech_threshold 0.6 " +
                      // condition_on_previous_text=False: ista logika kao kod faster-whisper.
                      $"--condition_on_previous_text False " +
                      $"--temperature 0 --verbose False";

                var whisperProc = new Process
                {
                    StartInfo = new ProcessStartInfo(whisperExe, whisperArgs)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                var stdErr = new StringBuilder();
                whisperProc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                        if (e.Data.Contains("%") || e.Data.Contains("Detecting"))
                            progress?.Report($"Whisper: {e.Data.Trim()}");
                    }
                };

                whisperProc.Start();
                whisperProc.BeginErrorReadLine();

                // WaitForExitAsync(ct) direktno reaguje na otkazivanje — nema polling latency
                try
                {
                    await whisperProc.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { whisperProc.Kill(); } catch { }
                    throw;
                }

                ct.ThrowIfCancellationRequested();

                progress?.Report("Parsiranje rezultata...");

                string audioName = Path.GetFileNameWithoutExtension(audioPath);
                string srtPath = Path.Combine(tempDir, audioName + ".srt");

                if (!File.Exists(srtPath))
                {
                    var srtFiles = Directory.GetFiles(tempDir, "*.srt");
                    srtPath = srtFiles.FirstOrDefault();
                }

                if (srtPath == null || !File.Exists(srtPath))
                {
                    result.ErrorMessage = LF("at_no_srt", stdErr);
                    return result;
                }

                var timedLines = ParseWhisperSrt(srtPath);

                if (timedLines.Count == 0)
                {
                    result.ErrorMessage = "Whisper nije prepoznao nikakav tekst u audio fajlu.";
                    return result;
                }

                // ── POST-PROCESSING VAD KOREKCIJA ─────────────────────────────────────
                // Problem: Whisper ponekad i pored --vad_filter halu­cinira timestamp
                // prvog segmenta — smijesti ga u instrumentalni uvod (npr. 00:06)
                // umjesto kad glas stvarno počinje (npr. 00:12).
                //
                // Detekcija halucinacije: ako je razmak između prvog i drugog segmenta
                // neuobičajeno velik (> 4s), to znači da Whisper nije detektovao pravi
                // početak glasa i gura tekst prerano.
                //
                // Korekcija: pomijeri prvi segment na poziciju drugog minus prosječno
                // trajanje segmenta. Ovo je konzervativna korekcija — bolje da tekst
                // kasni 0.5s nego da istrči 6s ispred glasa pred djetetom.
                //
                // Garancija bez padding-a: StartSeconds se NE zaokružava —
                // koristi se tačna vrijednost do milisekunde iz Whisper izlaza.
                // ─────────────────────────────────────────────────────────────────────
                if (timedLines.Count >= 2)
                {
                    double firstStart  = timedLines[0].StartSeconds;
                    double secondStart = timedLines[1].StartSeconds;
                    double gap = secondStart - firstStart;

                    // Sumnjiv gap: prvi segment je ≥ 4s ranije od drugog,
                    // a ukupna duracija prvog segmenta je kratka (< 3s).
                    // To je klasičan znak halucinacije na instrumentalu.
                    bool isHallucination = gap >= 4.0
                        && (timedLines[0].EndSeconds - timedLines[0].StartSeconds) < 3.0;

                    if (isHallucination)
                    {
                        double avgDur = timedLines
                            .Take(Math.Min(5, timedLines.Count))
                            .Average(l => l.EndSeconds - l.StartSeconds);
                        // Pomijeri prvi segment tik ispred drugog
                        double correctedStart = Math.Max(0, secondStart - avgDur - 0.1);
                        double correctedEnd   = secondStart - 0.05;
                        progress?.Report($"VAD korekcija: prvi titl pomjeren {firstStart:F2}s → {correctedStart:F2}s (halucinacija na instrumentalu)");
                        timedLines[0] = new TimedLine
                        {
                            Text         = timedLines[0].Text,
                            StartSeconds = correctedStart,
                            EndSeconds   = correctedEnd > correctedStart ? correctedEnd : correctedStart + avgDur
                        };
                    }
                }
                // ─────────────────────────────────────────────────────────────────────

                result.Lines = timedLines;
                result.FullText = string.Join("\n", timedLines.Select(l => l.Text));
                result.Success = true;
                progress?.Report($"Gotovo — {timedLines.Count} linija prepoznato.");
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Transkripcija otkazana.";
            }
            catch (Exception ex)
            {
                result.ErrorMessage = LF("generic_error", ex.Message);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            return result;
        }

        public static string FormatLyricsForTextBox(List<TimedLine> lines)
            => string.Join("\n", lines.Select(l => l.Text));

        public static Dictionary<string, double> BuildTimestampMap(List<TimedLine> lines)
        {
            var map = new Dictionary<string, double>();
            foreach (var line in lines)
                if (!map.ContainsKey(line.Text))
                    map[line.Text] = line.StartSeconds;
            return map;
        }

        // ── Forced Alignment ─────────────────────────────────────────────────
        // Korisnik daje tekst, Whisper samo mjeri GDJE u audio-u je svaka linija.
        // Ne transkribuje — samo poravnava. Rezultat: milisekunda-tačni timestamps
        // bez obzira na dijalekt, akcenat ili kvalitet snimka.
        //
        // Metoda: Whisper se pokreće sa --initial_prompt koji sadrži cijeli tekst
        // pjesme. Time Whisper "zna" šta treba čuti i fokusira se na alignment
        // umjesto na pogađanje teksta. Vraćeni segmenti se mapiraju na korisnikove
        // linije po redoslijedu (ne po tekstu — tekst može varirati u izgovoru).
        public static async Task<TranscriptionResult> ForcedAlignAsync(
            string audioPath,
            List<string> userLines,
            string language = "sr",
            string ffmpegPath = null,
            string modelSize = "small",
            IProgress<string> progress = null,
            CancellationToken ct = default)
        {
            var result = new TranscriptionResult();

            if (userLines == null || userLines.Count == 0)
            {
                result.ErrorMessage = "Nema teksta za poravnavanje.";
                return result;
            }

            string whisperExe = FindWhisperExecutable();
            if (whisperExe == null)
            {
                result.ErrorMessage = "Whisper nije pronađen.";
                return result;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
                ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            string tempDir = Path.Combine(Path.GetTempPath(), $"UVE_Align_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Ekstrakt WAV ako je mp3/mp4
                progress?.Report("Priprema audia za alignment...");
                string wavPath = audioPath;
                string ext = Path.GetExtension(audioPath).ToLower();
                if (ext != ".wav")
                {
                    wavPath = await ExtractAudioAsync(audioPath, tempDir, ffmpegPath);
                    if (wavPath == null) { result.ErrorMessage = "Greška pri ekstrakciji audia."; return result; }
                }

                // initial_prompt = cijeli tekst pjesme (max 224 tokena = ~900 znakova)
                // Ako je tekst duži, uzimamo prvih N linija koje stanu
                string fullText = string.Join(" / ", userLines);
                if (fullText.Length > 880)
                    fullText = fullText.Substring(0, 880);

                // Escapujemo navodnike za command line
                string promptEscaped = fullText.Replace("\"", "'");

                bool isFasterWhisper = whisperExe.ToLower().Contains("faster-whisper");

                string whisperArgs;
                if (isFasterWhisper)
                {
                    // faster-whisper: tražimo i SRT i JSON da dobijemo word-level timestamps
                    whisperArgs = $"\"{wavPath}\" --model {modelSize} --language {language} " +
                                  $"--output_format json --output_dir \"{tempDir}\" " +
                                  $"--initial_prompt \"{promptEscaped}\" " +
                                  $"--word_timestamps True " +
                                  $"--compute_type float16 --beam_size 5 --temperature 0";
                }
                else
                {
                    // openai-whisper: --initial_prompt i --word_timestamps
                    whisperArgs = $"\"{wavPath}\" --model {modelSize} --language {language} " +
                                  $"--output_format srt --output_dir \"{tempDir}\" " +
                                  $"--initial_prompt \"{promptEscaped}\" " +
                                  $"--word_timestamps True --verbose False";
                }

                progress?.Report($"Whisper alignment ({modelSize} model, slušam glas...)");

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo(whisperExe, whisperArgs)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                var stderr = new StringBuilder();
                proc.ErrorDataReceived += (s, e) => {
                    if (!string.IsNullOrEmpty(e.Data)) {
                        stderr.AppendLine(e.Data);
                        if (e.Data.Contains("%")) progress?.Report($"Alignment: {e.Data.Trim()}");
                    }
                };

                proc.Start();
                proc.BeginErrorReadLine();
                try { await proc.WaitForExitAsync(ct); }
                catch (OperationCanceledException) { try { proc.Kill(); } catch { } throw; }

                ct.ThrowIfCancellationRequested();
                progress?.Report("Mapiranje teksta na timestamps...");

                // Pokušaj JSON (word-level) prvo, SRT kao fallback
                string jsonPath = Directory.GetFiles(tempDir, "*.json").FirstOrDefault();
                string srtPath  = Directory.GetFiles(tempDir, "*.srt").FirstOrDefault();

                // Ako JSON nije dostupan (starija verzija Whispera), pokušaj SRT fallback
                if (jsonPath == null && srtPath == null)
                {
                    // Pokušaj ponovo sa SRT formatom
                    whisperArgs = whisperArgs.Replace("--output_format json", "--output_format srt");
                    using var proc2 = new Process
                    {
                        StartInfo = new ProcessStartInfo(whisperExe, whisperArgs)
                        {
                            UseShellExecute = false, RedirectStandardOutput = true,
                            RedirectStandardError = true, CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                        }
                    };
                    proc2.Start();
                    await proc2.WaitForExitAsync(ct);
                    srtPath = Directory.GetFiles(tempDir, "*.srt").FirstOrDefault();
                }

                // Parsiraj word-level timestamps iz JSON ako postoji
                if (jsonPath != null && File.Exists(jsonPath))
                {
                    var wordTimings = ParseWhisperJsonWords(jsonPath);
                    if (wordTimings.Count > 0)
                    {
                        result.WordTimings = wordTimings;
                        progress?.Report($"Word-level sync: {wordTimings.Count} riječi sa timestamps.");
                    }

                    // Izvuci segmente iz JSON-a za liniju-po-liniju mapiranje
                    var whisperSegments = ParseWhisperJsonSegments(jsonPath);
                    if (whisperSegments.Count == 0 && srtPath != null)
                        whisperSegments = ParseWhisperSrt(srtPath);

                    if (whisperSegments.Count == 0)
                    {
                        result.ErrorMessage = "Whisper JSON/SRT ne sadrži segmente.";
                        return result;
                    }

                    BuildAlignedLines(userLines, whisperSegments, result);
                }
                else if (srtPath != null)
                {
                    var whisperSegments = ParseWhisperSrt(srtPath);
                    if (whisperSegments.Count == 0)
                    {
                        result.ErrorMessage = "Whisper nije pronašao nijedno vremensko poravnanje u audio fajlu.";
                        return result;
                    }
                    BuildAlignedLines(userLines, whisperSegments, result);
                }
                else
                {
                    result.ErrorMessage = "Whisper alignment nije generisao ni JSON ni SRT fajl. " + stderr;
                    return result;
                }

                result.FullText = string.Join("\n", userLines);
                result.Success  = true;
                progress?.Report($"Alignment gotov — {result.Lines.Count} linija, {result.WordTimings.Count} word timestamps.");
            }
            catch (OperationCanceledException) { result.ErrorMessage = "Alignment otkazan."; }
            catch (Exception ex) { result.ErrorMessage = $"Alignment greška: {ex.Message}"; }
            finally { try { Directory.Delete(tempDir, true); } catch { } }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BuildAlignedLines — mapira Whisper segmente na korisnikove linije
        // ══════════════════════════════════════════════════════════════════════
        private static void BuildAlignedLines(List<string> userLines, List<TimedLine> whisperSegments, TranscriptionResult result)
        {
            var alignedLines = new List<TimedLine>();

            if (whisperSegments.Count >= userLines.Count)
            {
                double ratio = (double)whisperSegments.Count / userLines.Count;
                for (int i = 0; i < userLines.Count; i++)
                {
                    int segStart = (int)(i * ratio);
                    int segEnd   = Math.Min((int)((i + 1) * ratio) - 1, whisperSegments.Count - 1);
                    segStart = Math.Min(segStart, segEnd);
                    alignedLines.Add(new TimedLine
                    {
                        Text         = userLines[i],
                        StartSeconds = whisperSegments[segStart].StartSeconds,
                        EndSeconds   = whisperSegments[segEnd].EndSeconds
                    });
                }
            }
            else
            {
                for (int i = 0; i < userLines.Count; i++)
                {
                    if (i < whisperSegments.Count)
                    {
                        alignedLines.Add(new TimedLine
                        {
                            Text         = userLines[i],
                            StartSeconds = whisperSegments[i].StartSeconds,
                            EndSeconds   = whisperSegments[i].EndSeconds
                        });
                    }
                    else
                    {
                        double lastEnd = alignedLines.Last().EndSeconds;
                        double avgDur  = alignedLines.Count > 0 ? alignedLines.Average(l => l.EndSeconds - l.StartSeconds) : 3.0;
                        alignedLines.Add(new TimedLine
                        {
                            Text         = userLines[i],
                            StartSeconds = lastEnd,
                            EndSeconds   = lastEnd + avgDur
                        });
                    }
                }
            }

            // FREEZE FIX: max 8s po liniji
            const double MAX_LINE_DUR = 8.0;
            for (int i = 0; i < alignedLines.Count; i++)
            {
                double maxEnd = alignedLines[i].StartSeconds + MAX_LINE_DUR;
                if (i < alignedLines.Count - 1)
                    maxEnd = Math.Min(maxEnd, alignedLines[i + 1].StartSeconds - 0.050);
                if (alignedLines[i].EndSeconds > maxEnd)
                    alignedLines[i].EndSeconds = maxEnd;
            }

            // Seamless flow: zatvori praznine do 100ms
            for (int i = 0; i < alignedLines.Count - 1; i++)
            {
                double gap = alignedLines[i + 1].StartSeconds - alignedLines[i].EndSeconds;
                if (gap > 0 && gap <= 0.100)
                    alignedLines[i].EndSeconds = alignedLines[i + 1].StartSeconds - 0.010;
            }

            result.Lines = alignedLines;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ParseWhisperJsonWords — izvlači word-level timestamps iz Whisper JSON
        //
        //  Whisper JSON struktura:
        //  { "segments": [ { "words": [ {"word":"sladoled","start":2.1,"end":2.6,"probability":0.99} ] } ] }
        // ══════════════════════════════════════════════════════════════════════
        private static List<WordTiming> ParseWhisperJsonWords(string jsonPath)
        {
            var result = new List<WordTiming>();
            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);

                // Parsiraj bez System.Text.Json da ne zavisimo od verzije
                // Tražimo sve "words" array-e i vadimo word/start/end/probability
                var wordMatches = Regex.Matches(json,
                    @"\{[^}]*""word""\s*:\s*""([^""]+)""\s*,\s*""start""\s*:\s*([\d.]+)\s*,\s*""end""\s*:\s*([\d.]+)(?:\s*,\s*""probability""\s*:\s*([\d.]+))?[^}]*\}");

                foreach (Match m in wordMatches)
                {
                    string word = m.Groups[1].Value.Trim().ToLowerInvariant();
                    // Ukloni interpunkciju
                    word = Regex.Replace(word, @"[^\p{L}\p{N}]", "");
                    if (string.IsNullOrEmpty(word)) continue;

                    double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double start);
                    double.TryParse(m.Groups[3].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double end);
                    double prob = 1.0;
                    if (m.Groups[4].Success)
                        double.TryParse(m.Groups[4].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out prob);

                    result.Add(new WordTiming { Word = word, StartSecond = start, EndSecond = end, Probability = prob });
                }

                // Fallback: ako gornji regex ne pali, pokušaj alternativni format
                // (faster-whisper može imati drugačiji redoslijed ključeva)
                if (result.Count == 0)
                {
                    var altMatches = Regex.Matches(json,
                        @"""start""\s*:\s*([\d.]+)[^}]*""end""\s*:\s*([\d.]+)[^}]*""word""\s*:\s*""([^""]+)""");
                    foreach (Match m in altMatches)
                    {
                        double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double start);
                        double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double end);
                        string word = Regex.Replace(m.Groups[3].Value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
                        if (!string.IsNullOrEmpty(word))
                            result.Add(new WordTiming { Word = word, StartSecond = start, EndSecond = end });
                    }
                }
            }
            catch { }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ParseWhisperJsonSegments — izvlači segment-level timestamps iz JSON
        //  (za liniju-po-liniju mapiranje, kao zamjena za SRT)
        // ══════════════════════════════════════════════════════════════════════
        private static List<TimedLine> ParseWhisperJsonSegments(string jsonPath)
        {
            var result = new List<TimedLine>();
            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var segMatches = Regex.Matches(json,
                    @"""start""\s*:\s*([\d.]+)\s*,\s*""end""\s*:\s*([\d.]+)\s*,\s*""text""\s*:\s*""([^""]+)""");

                foreach (Match m in segMatches)
                {
                    double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double start);
                    double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double end);
                    string text = m.Groups[3].Value.Trim();
                    if (!string.IsNullOrEmpty(text))
                        result.Add(new TimedLine { StartSeconds = start, EndSeconds = end, Text = text });
                }
            }
            catch { }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DetectAudioStart — pronađi prvu sekundu gdje muzika/glas počinje
        //  Rješava "mrtav hod": video počinje tačno kad krene prva nota.
        //
        //  Koristi FFmpeg silencedetect filter.
        //  Vraća broj sekundi tišine na početku audio fajla.
        // ══════════════════════════════════════════════════════════════════════
        public static async Task<double> DetectAudioStartAsync(
            string audioPath,
            string ffmpegPath,
            double silenceThresholdDb = -40.0,
            CancellationToken ct = default)
        {
            if (!File.Exists(audioPath) || !File.Exists(ffmpegPath)) return 0.0;
            try
            {
                string threshStr = silenceThresholdDb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                // silencedetect: noise = threshold u dB, duration = minimalno trajanje tišine
                string args = $"-nostdin -i \"{audioPath}\" " +
                              $"-af \"silencedetect=noise={threshStr}dB:duration=0.1\" " +
                              $"-f null -";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = args,
                    UseShellExecute = false, RedirectStandardError = true,
                    CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8
                };
                using var proc = Process.Start(psi);
                if (proc == null) return 0.0;

                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                // FFmpeg ispisuje: "silence_end: 1.234 | silence_duration: 1.234"
                // Prva silence_end = kraj početne tišine = početak muzike
                var m = Regex.Match(stderr, @"silence_end:\s*([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double silenceEnd))
                {
                    // Oduzmi 50ms buffer da ne odsječemo napad note
                    return Math.Max(0.0, silenceEnd - 0.05);
                }
            }
            catch { }
            return 0.0;
        }
    }
}
