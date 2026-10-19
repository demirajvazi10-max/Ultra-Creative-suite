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
        /// <summary>v1.4: faster-whisper-xxl's built-in vocal separation (MDX Kim_Vocal_2).</summary>
        private const string VocalExtractArg = "--ff_vocal_extract mdx_kim2";

        private static string LastLine(string text) =>
            (text ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

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

            /// <summary>
            /// v1.4 — per user line, the user's own words with times (from LyricAligner).
            /// Index matches Lines. Empty when word-level alignment was not possible.
            /// </summary>
            public List<List<WordTiming>> LineWords { get; set; } = new();

            /// <summary>v1.4 — share of the user's words matched to heard words (0..1), or -1 if not aligned by words.</summary>
            public double AlignmentConfidence { get; set; } = -1;

            /// <summary>v1.4 — the raw words Whisper heard (before alignment), for caching/re-alignment.</summary>
            public List<WordTiming> HeardWords { get; set; } = new();
        }

        public class TimedLine
        {
            public double StartSeconds { get; set; }
            public double EndSeconds   { get; set; }
            public string Text         { get; set; } = "";
        }

        /// <summary>
        /// A single spoken word with its exact start second.
        /// Ovo je osnova za word-level video sync:
        /// when the singer says "sladoled" → API query "ice cream" starts exactly then.
        /// </summary>
        public class WordTiming
        {
            public string Word        { get; set; } = "";
            public double StartSecond { get; set; }
            public double EndSecond   { get; set; }
            /// <summary>Whisper confidence 0.0–1.0 for this word</summary>
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
                using var proc = new Process
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
        private static string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public static bool IsWhisperAvailable() => FindWhisperExecutable() != null;

        private static async Task<string> ExtractAudioAsync(string mediaPath, string tempDir, string ffmpegPath)
        {
            string outPath = Path.Combine(tempDir, $"whisper_audio_{Guid.NewGuid():N}.wav");
            string args = $"-nostdin -i \"{mediaPath}\" -ar 16000 -ac 1 -c:a pcm_s16le -y \"{outPath}\"";

            using var proc = new Process
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
            proc.DrainAll(); // ffmpeg piše napredak u stderr — bez čitanja zastane na dužim snimcima
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
                ffmpegPath = AppPaths.FFmpeg;

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

                progress?.Report($"Whisper is analyzing audio (model: {modelSize})...");

                bool isFasterWhisper = whisperExe.ToLower().Contains("faster-whisper");
                string whisperArgs = isFasterWhisper
                    ? $"\"{audioPath}\" --model {modelSize} --language {language} " +
                      // v1.4: "all" = SRT (lines) + JSON (word times for word sync / karaoke)
                      $"--output_format all --word_timestamps True --output_dir \"{tempDir}\" " +
                      // v1.4: "auto" = float16 on an NVIDIA card, int8/float32 on the CPU. A fixed
                      // float16 fails on computers without CUDA.
                      $"--compute_type auto " +
                      $"--beam_size 5 " +
                      $"--best_of 5 " +
                      $"--temperature 0 " +
                      // VAD filter: skips segments without human voice (instruments, silence).
                      // threshold=0.6 znaci da segment mora biti 60%+ siguran da ima glas.
                      // min_silence_duration_ms=500 = pause shorter than 0.5s is not treated as silence.
                      // BEZ OVOGA: Whisper halucinira tekst tokom 10s instrumentalnog uvoda
                      // i gura prvi titl na 00:06 umjesto na 00:12 kada glas stvarno pocinje.
                      $"--vad_filter True " +
                      // v1.4: faster-whisper-xxl has no --vad_parameters option (that one is from
                      // whisperX); passing it made the program print its usage and stop, so no
                      // subtitles were ever written. The real options:
                      $"--vad_threshold 0.6 --vad_min_silence_duration_ms 500 " +
                      // condition_on_previous_text=False: svaki segment se dekodira nezavisno.
                      // Without this the model "copies" context from previous segment into silence
                      // and invents text not yet spoken — early-start bug.
                      $"--condition_on_previous_text False " +
                      // v1.4: separate the voice from the music first (MDX Kim_Vocal_2, built into
                      // faster-whisper-xxl; the model is downloaded once on first use). On a song
                      // this is the biggest single accuracy gain: Whisper no longer hears the
                      // instruments as words or misses quiet lines. Dropped automatically if it fails.
                      VocalExtractArg
                    : $"\"{audioPath}\" --model {modelSize} --language {language} " +
                      $"--output_format all --word_timestamps True --output_dir \"{tempDir}\" " +
                      // no_speech_threshold=0.6: openai-whisper ekvivalent VAD-a.
                      // Segments below threshold are marked as silence and skipped.
                      $"--no_speech_threshold 0.6 " +
                      // condition_on_previous_text=False: ista logika kao kod faster-whisper.
                      $"--condition_on_previous_text False " +
                      $"--temperature 0 --verbose False";

                // v1.4: some faster-whisper-xxl builds reject "--output_format all" (argparse prints
                // "usage: ..." and exits without writing anything). Try the richest output first
                // and fall back step by step; only an argument error triggers the next attempt.
                async Task<string> RunWhisperAsync(string args)
                {
                    using var whisperProc = new Process
                    {
                        StartInfo = new ProcessStartInfo(whisperExe, args)
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
                    whisperProc.DrainStdout();

                    // WaitForExitAsync(ct) direktno reaguje na otkazivanje — nema polling latency
                    try
                    {
                        await whisperProc.WaitForExitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        try { whisperProc.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }

                    return stdErr.ToString();
                }

                string[] formatVariants =
                {
                    "--output_format all --word_timestamps True",
                    "--output_format srt json --word_timestamps True",
                    "--output_format srt"
                };
                string stdErrText = "";
                bool WroteSomething() => Directory.GetFiles(tempDir, "*.srt").Length > 0 ||
                                         Directory.GetFiles(tempDir, "*.json").Length > 0;
                foreach (bool withVocals in new[] { true, false })
                {
                    if (!withVocals)
                    {
                        if (WroteSomething() || !whisperArgs.Contains(VocalExtractArg)) break;
                        // v1.4: vocal separation failed (no internet for the first model download,
                        // not enough memory...) — transcribe the full mix instead.
                        AppLog.Warn("Whisper: vocal separation failed, transcribing without it. " + LastLine(stdErrText));
                        progress?.Report("Whisper: retrying without vocal separation...");
                        whisperArgs = whisperArgs.Replace(VocalExtractArg, "");
                    }
                    foreach (string variant in formatVariants)
                    {
                        string attemptArgs = whisperArgs.Replace("--output_format all --word_timestamps True", variant);
                        stdErrText = await RunWhisperAsync(attemptArgs);
                        ct.ThrowIfCancellationRequested();
                        bool argumentError = stdErrText.IndexOf("usage:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             stdErrText.IndexOf("unrecognized arguments", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             stdErrText.IndexOf("invalid choice", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (WroteSomething() || !argumentError) break;
                        progress?.Report("Whisper: retrying with simpler output options...");
                    }
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
                    result.ErrorMessage = LF("at_no_srt", stdErrText);
                    return result;
                }

                var timedLines = ParseWhisperSrt(srtPath);

                if (timedLines.Count == 0)
                {
                    result.ErrorMessage = "Whisper did not recognize any text in the audio file.";
                    return result;
                }

                // ── POST-PROCESSING VAD KOREKCIJA ─────────────────────────────────────
                // Problem: Whisper ponekad i pored --vad_filter halu­cinira timestamp
                // prvog segmenta — smijesti ga u instrumentalni uvod (npr. 00:06)
                // instead of when the voice actually starts (e.g. 00:12).
                //
                // Hallucination detection: if the gap between first and second segment
                // is unusually large (> 4s), it means Whisper did not detect the real
                // start of the voice and is pushing text too early.
                //
                // Correction: shift the first segment to the position of the second minus average
                // trajanje segmenta. Ovo je konzervativna korekcija — bolje da tekst
                // 0.5s late rather than running 6s ahead of the voice for the child.
                //
                // No-padding guarantee: StartSeconds is NOT rounded —
                // the exact value to the millisecond from Whisper output is used.
                // ─────────────────────────────────────────────────────────────────────
                if (timedLines.Count >= 2)
                {
                    double firstStart  = timedLines[0].StartSeconds;
                    double secondStart = timedLines[1].StartSeconds;
                    double gap = secondStart - firstStart;

                    // Sumnjiv gap: prvi segment je ≥ 4s ranije od drugog,
                    // a ukupna duracija prvog segmenta je kratka (< 3s).
                    // That is a classic sign of hallucination on an instrumental.
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

                // v1.4: word times (if Whisper wrote a JSON file) — used to re-align the
                // lyrics later when the user corrects or re-splits lines, without re-running Whisper.
                string wordsJson = Path.Combine(tempDir, audioName + ".json");
                if (!File.Exists(wordsJson)) wordsJson = Directory.GetFiles(tempDir, "*.json").FirstOrDefault();
                if (wordsJson != null && File.Exists(wordsJson))
                    result.WordTimings = ParseWhisperJsonWords(wordsJson);

                result.Success = true;
                progress?.Report(LanguageManager.F("loc_transcr_done", timedLines.Count));
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Transcription cancelled.";
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
        // Does not transcribe — only aligns. Result: millisecond-accurate timestamps
        // bez obzira na dijalekt, akcenat ili kvalitet snimka.
        //
        // Method: Whisper is run with --initial_prompt containing the full text
        // of the song. This way Whisper "knows" what to hear and focuses on alignment
        // instead of guessing the text. Returned segments are mapped to the user's
        // lines by order (not by text — text may vary in pronunciation).
        public static async Task<TranscriptionResult> ForcedAlignAsync(
            string audioPath,
            List<string> userLines,
            string language = "sr",
            string ffmpegPath = null,
            string modelSize = "small",
            IProgress<string> progress = null,
            CancellationToken ct = default,
            bool allowRepeats = false)
        {
            var result = new TranscriptionResult();

            if (userLines == null || userLines.Count == 0)
            {
                result.ErrorMessage = "No text to align.";
                return result;
            }

            string whisperExe = FindWhisperExecutable();
            if (whisperExe == null)
            {
                result.ErrorMessage = "Whisper not found.";
                return result;
            }

            if (string.IsNullOrEmpty(ffmpegPath))
                ffmpegPath = AppPaths.FFmpeg;

            string tempDir = Path.Combine(Path.GetTempPath(), $"UVE_Align_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Ekstrakt WAV ako je mp3/mp4
                progress?.Report(LanguageManager.T("loc_transcr_prep_align"));
                string wavPath = audioPath;
                string ext = Path.GetExtension(audioPath).ToLower();
                if (ext != ".wav")
                {
                    wavPath = await ExtractAudioAsync(audioPath, tempDir, ffmpegPath);
                    if (wavPath == null) { result.ErrorMessage = "Error during audio extraction."; return result; }
                }

                // initial_prompt = cijeli tekst pjesme (max 224 tokena = ~900 znakova)
                // If text is longer, we take the first N lines that fit
                string fullText = string.Join(" / ", userLines);
                if (fullText.Length > 880)
                    fullText = fullText.Substring(0, 880);

                // Escapujemo navodnike za command line
                string promptEscaped = fullText.Replace("\"", "'");

                bool isFasterWhisper = whisperExe.ToLower().Contains("faster-whisper");

                string whisperArgs;
                if (isFasterWhisper)
                {
                    // faster-whisper: we look for both SRT and JSON to get word-level timestamps
                    whisperArgs = $"\"{wavPath}\" --model {modelSize} --language {language} " +
                                  $"--output_format json --output_dir \"{tempDir}\" " +
                                  $"--initial_prompt \"{promptEscaped}\" " +
                                  $"--word_timestamps True " +
                                  $"--compute_type auto --beam_size 5 --temperature 0 " +
                                  // v1.4: same anti-hallucination settings as TranscribeAsync —
                                  // without them Whisper "hears" words in the instrumental intro.
                                  $"--vad_filter True --condition_on_previous_text False " +
                                  VocalExtractArg;   // v1.4: voice separated from the music first
                }
                else
                {
                    // openai-whisper: --initial_prompt i --word_timestamps
                    // v1.4: JSON instead of SRT — SRT has no word times, so the
                    // word-level alignment could never run with openai-whisper.
                    whisperArgs = $"\"{wavPath}\" --model {modelSize} --language {language} " +
                                  $"--output_format json --output_dir \"{tempDir}\" " +
                                  $"--initial_prompt \"{promptEscaped}\" " +
                                  $"--word_timestamps True --condition_on_previous_text False --verbose False";
                }

                progress?.Report($"Whisper alignment ({modelSize} model, listening to voice...)");

                using var proc = new Process
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
                proc.DrainStdout();
                await proc.WaitForExitOrKillAsync(ct);

                ct.ThrowIfCancellationRequested();
                progress?.Report("Mapping text to timestamps...");

                // Try JSON (word-level) first, SRT as fallback
                string jsonPath = Directory.GetFiles(tempDir, "*.json").FirstOrDefault();
                string srtPath  = Directory.GetFiles(tempDir, "*.srt").FirstOrDefault();

                // If JSON is not available (older Whisper version), try SRT fallback
                if (jsonPath == null && srtPath == null)
                {
                    // Try again with SRT format — and without vocal separation, in case that
                    // was what failed (v1.4)
                    if (whisperArgs.Contains(VocalExtractArg))
                        AppLog.Warn("Whisper alignment: no output with vocal separation, retrying without it. " + LastLine(stderr.ToString()));
                    whisperArgs = whisperArgs.Replace("--output_format json", "--output_format srt").Replace(VocalExtractArg, "");
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
                    proc2.DrainAll();
                    await proc2.WaitForExitOrKillAsync(ct);
                    srtPath = Directory.GetFiles(tempDir, "*.srt").FirstOrDefault();
                }

                // Parsiraj word-level timestamps iz JSON ako postoji
                if (jsonPath != null && File.Exists(jsonPath))
                {
                    var wordTimings = ParseWhisperJsonWords(jsonPath);
                    if (wordTimings.Count > 0)
                    {
                        result.WordTimings = wordTimings;
                        result.HeardWords = wordTimings;
                        progress?.Report($"Word-level sync: {wordTimings.Count} words with timestamps.");
                    }

                    // Izvuci segmente iz JSON-a za liniju-po-liniju mapiranje
                    var whisperSegments = ParseWhisperJsonSegments(jsonPath);
                    if (whisperSegments.Count == 0 && srtPath != null)
                        whisperSegments = ParseWhisperSrt(srtPath);

                    if (whisperSegments.Count == 0)
                    {
                        result.ErrorMessage = "Whisper JSON/SRT contains no segments.";
                        return result;
                    }

                    // v1.4: word-level alignment of the user's exact lines (LyricAligner).
                    // With allowRepeats (subtitles for a song) every repeat of a chorus gets its
                    // own subtitle even though it was typed once. Falls back to the old
                    // segment-ratio mapping only when too few words match.
                    if (!(allowRepeats && TryApplySungAlignment(userLines, result.WordTimings, result, progress)) &&
                        !TryApplyWordAlignment(userLines, result.WordTimings, result, progress))
                        BuildAlignedLines(userLines, whisperSegments, result);
                }
                else if (srtPath != null)
                {
                    var whisperSegments = ParseWhisperSrt(srtPath);
                    if (whisperSegments.Count == 0)
                    {
                        result.ErrorMessage = "Whisper found no time alignment in the audio file.";
                        return result;
                    }
                    BuildAlignedLines(userLines, whisperSegments, result);
                }
                else
                {
                    result.ErrorMessage = "Whisper alignment did not generate a JSON or SRT file. " + stderr;
                    return result;
                }

                result.FullText = string.Join("\n", userLines);
                result.Success  = true;
                progress?.Report($"Alignment gotov — {result.Lines.Count} linija, {result.WordTimings.Count} word timestamps.");
            }
            catch (OperationCanceledException) { result.ErrorMessage = "Alignment cancelled."; }
            catch (Exception ex) { result.ErrorMessage = $"Alignment error: {ex.Message}"; }
            finally { try { Directory.Delete(tempDir, true); } catch { } }

            return result;
        }

        /// <summary>
        /// v1.4 — aligns the user's lines to heard words. On success fills result.Lines
        /// (one per user line, user's text), result.LineWords and replaces result.WordTimings
        /// with the user's own words, so later word lookups use the real lyric spelling.
        /// </summary>
        public static bool TryApplyWordAlignment(List<string> userLines, List<WordTiming> heardWords,
                                                 TranscriptionResult result, IProgress<string> progress = null)
        {
            if (userLines == null || userLines.Count == 0 || heardWords == null || heardWords.Count == 0)
                return false;

            var aligned = LyricAligner.Align(userLines, heardWords);
            result.AlignmentConfidence = aligned.Confidence;
            if (!aligned.IsUsable)
            {
                progress?.Report($"Word alignment too uncertain ({aligned.Confidence:P0} of words matched).");
                return false;
            }

            result.Lines = aligned.Lines.Select(l => new TimedLine
            {
                StartSeconds = l.Start,
                EndSeconds = l.End,
                Text = l.Text
            }).ToList();
            result.LineWords = aligned.Lines.Select(l => l.Words).ToList();
            result.WordTimings = aligned.Lines.SelectMany(l => l.Words).ToList();
            progress?.Report($"Lyrics aligned word by word: {aligned.Confidence:P0} of words matched.");
            return true;
        }

        /// <summary>
        /// v1.4 — lyrics as they are sung: typed lines matched to what was heard, repeats
        /// included (see LyricAligner.AlignSung). Used when at least half of the typed lines
        /// were found; otherwise the caller falls back to the once-per-line alignment.
        /// </summary>
        public static bool TryApplySungAlignment(List<string> userLines, List<WordTiming> heardWords,
                                                 TranscriptionResult result, IProgress<string> progress = null)
        {
            if (userLines == null || userLines.Count == 0 || heardWords == null || heardWords.Count == 0)
                return false;
            var sung = LyricAligner.AlignSung(userLines, heardWords);
            int distinct = sung.Select(l => l.LineIndex).Distinct().Count();
            int typed = userLines.Count(l => !string.IsNullOrWhiteSpace(l));
            if (sung.Count == 0 || distinct * 2 < typed) return false;

            result.Lines = sung.Select(l => new TimedLine { StartSeconds = l.Start, EndSeconds = l.End, Text = l.Text }).ToList();
            result.LineWords = sung.Select(l => l.Words).ToList();
            result.WordTimings = sung.SelectMany(l => l.Words).ToList();
            int words = sung.Sum(l => l.Words.Count), matched = sung.Sum(l => l.MatchedWords);
            result.AlignmentConfidence = words > 0 ? (double)matched / words : 0;
            progress?.Report($"Lyrics aligned as sung: {sung.Count} sung lines from {distinct} of {typed} typed lines.");
            return true;
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
        //  ParseWhisperJsonWords — extracts word-level timestamps from Whisper JSON
        //
        //  Whisper JSON struktura:
        //  { "segments": [ { "words": [ {"word":"sladoled","start":2.1,"end":2.6,"probability":0.99} ] } ] }
        // ══════════════════════════════════════════════════════════════════════
        private static List<WordTiming> ParseWhisperJsonWords(string jsonPath)
        {
            // FIX (v1.4): parsed with Newtonsoft instead of regex. The regex expected one fixed
            // key order and did not decode \uXXXX escapes — openai-whisper writes JSON with
            // ensure_ascii, so "šuma" arrived as "\u0161uma" and the regex turned it into "u0161uma".
            var result = new List<WordTiming>();
            try
            {
                var root = Newtonsoft.Json.Linq.JToken.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
                foreach (var wordsArray in root.SelectTokens("$..words").OfType<Newtonsoft.Json.Linq.JArray>())
                {
                    foreach (var w in wordsArray.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        string raw = (string)w["word"] ?? (string)w["text"] ?? "";
                        string word = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
                        if (string.IsNullOrEmpty(word)) continue;
                        double start = w["start"]?.Type is Newtonsoft.Json.Linq.JTokenType.Float or Newtonsoft.Json.Linq.JTokenType.Integer ? (double)w["start"] : -1;
                        double end = w["end"]?.Type is Newtonsoft.Json.Linq.JTokenType.Float or Newtonsoft.Json.Linq.JTokenType.Integer ? (double)w["end"] : -1;
                        if (start < 0) continue;
                        if (end < start) end = start + 0.2;
                        double prob = w["probability"]?.Type is Newtonsoft.Json.Linq.JTokenType.Float or Newtonsoft.Json.Linq.JTokenType.Integer ? (double)w["probability"] : 1.0;
                        result.Add(new WordTiming { Word = word, StartSecond = start, EndSecond = end, Probability = prob });
                    }
                }
                result.Sort((a, b) => a.StartSecond.CompareTo(b.StartSecond));
            }
            catch (Exception swallowed) { AppLog.Error("Reading Whisper word timings (JSON)", swallowed); }   // v1.4: was silent
            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ParseWhisperJsonSegments — extracts segment-level timestamps from JSON
        //  (za liniju-po-liniju mapiranje, kao zamjena za SRT)
        // ══════════════════════════════════════════════════════════════════════
        private static List<TimedLine> ParseWhisperJsonSegments(string jsonPath)
        {
            var result = new List<TimedLine>();
            try
            {
                var root = Newtonsoft.Json.Linq.JToken.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
                var segments = root["segments"] as Newtonsoft.Json.Linq.JArray;
                if (segments == null) return result;
                foreach (var seg in segments.OfType<Newtonsoft.Json.Linq.JObject>())
                {
                    string text = ((string)seg["text"] ?? "").Trim();
                    if (string.IsNullOrEmpty(text) || seg["start"] == null || seg["end"] == null) continue;
                    result.Add(new TimedLine { StartSeconds = (double)seg["start"], EndSeconds = (double)seg["end"], Text = text });
                }
            }
            catch (Exception swallowed) { AppLog.Error("Reading Whisper segments (JSON)", swallowed); }   // v1.4: was silent
            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DetectAudioStart — find the first second where music/voice starts
        //  Solves "dead start": video starts exactly when the first note begins.
        //
        //  Koristi FFmpeg silencedetect filter.
        //  Returns the number of silence seconds at the start of the audio file.
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
                // silencedetect: noise = threshold in dB, duration = minimum silence duration
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
                // First silence_end = end of initial silence = start of music
                var m = Regex.Match(stderr, @"silence_end:\s*([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double silenceEnd))
                {
                    // Subtract 50ms buffer to not cut off the note attack
                    return Math.Max(0.0, silenceEnd - 0.05);
                }
            }
            catch (Exception swallowed) { AppLog.Error("Detecting the start of the vocals (silencedetect)", swallowed); }   // v1.4: was silent
            return 0.0;
        }
    }
}
