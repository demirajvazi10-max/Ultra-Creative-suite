using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    public class VisionResult
    {
        public double Score { get; set; }
        public bool HasChildren { get; set; }
        public bool HasFaces { get; set; }
        public bool IsOutdoor { get; set; }
        public bool IsWarm { get; set; }
        public bool HasMotion { get; set; }
        public double Luminance { get; set; }
        public double Saturation { get; set; }
        public double Sharpness { get; set; }
        public string TopLabel { get; set; }
        public string[] Labels { get; set; }
        public bool OnnxUsed { get; set; }

        /// <summary>v1.4 — true when the score comes from the vision AI (Qwen), not from the fallbacks.</summary>
        public bool QwenUsed { get; set; }
        public string RejectReason { get; set; }

        /// <summary>
        /// Qwen je oznacio da scena ne odgovara kontekstu stiha —
        /// sistem treba da potrazi novi video za ovaj segment.
        /// </summary>
        public bool RetryNeeded { get; set; }

        /// <summary>
        /// Qwen je detektovao osmeh/radost na licima u kadru.
        /// Koristi se za bonus score kada je stih pozitivnog sentimenta.
        /// </summary>
        public bool HasSmile { get; set; }

        /// <summary>
        /// Short plain-language description of what the clip shows (from Qwen).
        /// Shown in the scene review so a blind user knows what is on screen.
        /// </summary>
        public string Description { get; set; }
    }

    public static class VisionAnalyzer
    {
        private static readonly string _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraVideoEditor", "Models");

        private static readonly string _modelPath = Path.Combine(_modelDir, "mobilenetv2.onnx");
        private static readonly string _labelsPath = Path.Combine(_modelDir, "imagenet_labels.txt");

        private const string MODEL_URL =
            "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx";
        private const string LABELS_URL =
            "https://raw.githubusercontent.com/anishathalye/imagenet-simple-labels/master/imagenet-simple-labels.json";

        private static object _onnxSession = null;
        private static string[] _labels = null;
        private static bool _onnxAvailable = false;
        private static bool _initDone = false;
        private static readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);

        // ── Qwen2-VL (Ollama) ─────────────────────────────────────────────────
        private static OllamaClient _ollamaClient;
        private static bool _qwenAvailable = false;
        private static readonly SemaphoreSlim _qwenCheckLock = new SemaphoreSlim(1, 1);

        private const string QWEN_MODEL      = "qwen2-vl";
        private const string QWEN_MODEL_ALT  = "qwen2.5-vl";
        private const string QWEN_MODEL_ALT2 = "qwen2.5vl"; // instaliran bez crtice

        // Cached model name — detected once in InitializeQwenAsync, not per-frame
        private static string _resolvedQwenModel = null;

        // Log helper for static context (Qwen analysis)
        private static void LogToMainWindowStatic(string msg)
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                        mw.LogMessage(msg, true);
                });
            }
            catch { }
        }

        // QWEN_PROMPT is no longer used directly — use BuildContextualQwenPrompt()
        // Remains as fallback for calls that do not pass context
        private const string QWEN_PROMPT =
            "Analyze this image for a family music video. Respond ONLY in JSON:\n" +
            "{\"score\":7,\"outdoor\":true,\"children\":false,\"faces\":true,\"animals\":false," +
            "\"warm\":true,\"motion\":false,\"luminance\":0.6,\"saturation\":0.5,\"sharpness\":70," +
            "\"label\":\"park\",\"labels\":[\"park\",\"outdoor\",\"trees\",\"people\"]," +
            "\"retry\":false,\"reject\":null}\n" +
            "REJECT if: medical masks, hockey/boxing, violence, grainy/blurry, motion blur, watery compression, " +
            "dark/gloomy/sad, watermarks, text overlays, city/urban/traffic, people posing at camera.";

        // ── Dinamicki prompt koji uzima u obzir stih i godisnje doba ─────────
        public static string BuildContextualQwenPrompt(
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null)
        {
            // v1.4: a SHORT prompt. The old one had 93 lines of partly contradicting rules (about
            // 2000 tokens): the 7B vision model needed 45-95 seconds per clip, and it copied the
            // example "score":7 from the template into almost every answer (29 of 31 clips got 7.0).
            // Now: a few clear rules, a score scale with meaning, and an example without numbers.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You check stock video clips for a children's music video (children 2-6 years old).");
            if (!string.IsNullOrWhiteSpace(lyricLine))
                sb.AppendLine($"The clip is for this lyric line: \"{lyricLine}\"");
            else
                sb.AppendLine("The clip is for an instrumental part (no lyrics): calm nature, a park, or gentle pets and farm animals WITHOUT children are fine here; wild animals (tigers, lions, wolves, bears) are NOT; judge beauty and mood.");
            string normSeason = (season ?? "").ToLowerInvariant();
            if (normSeason is "winter" or "spring" or "summer" or "autumn")
                sb.AppendLine($"Season of this line: {normSeason}.");
            if (!string.IsNullOrWhiteSpace(mood))
                sb.AppendLine($"Mood of the song: {mood}.");
            sb.AppendLine();
            sb.AppendLine("REJECT (set \"reject\" to a short reason) if the clip shows any of:");
            sb.AppendLine("hospital, illness, wheelchair, therapy; violence, weapons, fire, storms; alcohol, smoking, coffee makers;");
            sb.AppendLine("construction, road works, warning signs, traffic, parking lots, motorbikes, trains, railways, industrial areas;");
            sb.AppendLine("crowds of adults, aerial shots of people; cooking pots; icy cliffs or glaciers without children;");
            sb.AppendLine("children looking at tablets, phones, computers or TV screens; wild animals such as tigers, lions, wolves or bears;");
            sb.AppendLine("adults without a child (old people only together with a child); adult models posing at the camera;");
            sb.AppendLine("dark, gloomy or sterile rooms, silhouettes against windows; buildings or columns as the subject;");
            sb.AppendLine("cartoons, text, watermarks, blurry or low-quality video; gyms, masks, wild animals the lyric does not name.");
            sb.AppendLine();
            sb.AppendLine("SCORE 1-10 for how well the clip fits the lyric line for small children:");
            sb.AppendLine("9-10: a child clearly does what the line says, bright and joyful; 7-8: a happy child or family scene that fits the mood;");
            sb.AppendLine("5-6: pleasant but only loosely related; 3-4: hardly related or dull; 1-2: should be rejected.");
            if (normSeason is "winter" or "autumn")
                sb.AppendLine($"If the clip does not clearly look like {normSeason}, give at most 4 and set retry true.");
            sb.AppendLine("Set \"retry\" true when the clip does not match the action or feeling of the line.");
            sb.AppendLine();
            sb.AppendLine("Answer ONLY with JSON, no other text, in this form (fill in your own values):");
            sb.AppendLine("{\"score\":<1-10>,\"reject\":<null or \"reason\">,\"retry\":<true/false>,\"children\":<true/false>,\"outdoor\":<true/false>,");
            sb.AppendLine("\"faces\":<true/false>,\"smile\":<true/false>,\"warm\":<true/false>,\"motion\":<true/false>,");
            sb.AppendLine("\"label\":\"<main subject>\",\"labels\":[\"<up to 4 words>\"],");
            sb.AppendLine("\"description\":\"<one short English sentence for a blind viewer, max 15 words>\"}");
            return sb.ToString();
        }


        private static readonly HashSet<string> _childLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "baby", "toddler", "child", "girl", "boy", "infant", "kid",
            "playground", "swing", "slide", "sandbox"
        };

        private static readonly HashSet<string> _faceLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "face", "person", "people", "man", "woman", "girl", "boy", "portrait", "smile"
        };

        private static readonly HashSet<string> _outdoorLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "park", "garden", "field", "meadow", "forest", "beach", "mountain",
            "sky", "nature", "outdoor", "playground", "lawn", "flower", "tree",
            "grass", "lake", "fountain", "snow", "spring", "summer"
        };

        private static readonly HashSet<string> _badLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "coffee", "espresso", "cappuccino", "beer", "wine", "alcohol",
            "office", "cubicle", "desk", "laptop", "keyboard",
            "cigarette", "tobacco", "gun", "weapon", "knife",
            "frog", "toad", "reptile", "snake", "spider",
            "cemetery", "grave", "coffin", "skull", "casino", "nightclub"
        };

        public static async Task<bool> InitializeAsync(
            Action<string> log = null,
            CancellationToken ct = default)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (_initDone) return _onnxAvailable;

                Directory.CreateDirectory(_modelDir);

                if (!IsOnnxRuntimeAvailable())
                {
                    log?.Invoke("🧠 VisionAnalyzer: ONNX Runtime is not installed — FFmpeg mode active");
                    _onnxAvailable = false;
                    _initDone = true;
                    return false;
                }

                if (!File.Exists(_modelPath))
                {
                    log?.Invoke("🧠 VisionAnalyzer: Downloading the MobileNetV2 model (~14 MB)...");
                    bool downloaded = await DownloadFileAsync(MODEL_URL, _modelPath, ct);
                    if (!downloaded)
                    {
                        log?.Invoke("⚠ VisionAnalyzer: Download failed — FFmpeg mode active");
                        _onnxAvailable = false;
                        _initDone = true;
                        return false;
                    }
                    log?.Invoke("✅ VisionAnalyzer: Model preuzet");
                }

                if (!File.Exists(_labelsPath))
                    await DownloadFileAsync(LABELS_URL, _labelsPath, ct);

                if (File.Exists(_labelsPath))
                {
                    string json = await File.ReadAllTextAsync(_labelsPath, ct);
                    var matches = Regex.Matches(json, "\"([^\"]+)\"");
                    _labels = matches.Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                }

                _onnxSession = CreateOnnxSession(_modelPath);
                _onnxAvailable = _onnxSession != null;

                if (_onnxAvailable)
                    log?.Invoke("✅ VisionAnalyzer: ONNX aktivan, " + (_labels?.Length ?? 0) + " labela");
                else
                    log?.Invoke(LanguageManager.GetText("va_onnx_error", LanguageManager.CurrentLanguage ?? "en"));

                _initDone = true;
                return _onnxAvailable;
            }
            catch (Exception ex)
            {
                log?.Invoke(string.Format(LanguageManager.GetText("va_init_error", LanguageManager.CurrentLanguage ?? "en"), ex.Message));
                _onnxAvailable = false;
                _initDone = true;
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Provjeri i inicijalizuj Qwen2-VL via Ollama (pozovi jednom pri startu).
        /// </summary>
        public static async Task<bool> InitializeQwenAsync(
            Action<string> log = null,
            CancellationToken ct = default)
        {
            await _qwenCheckLock.WaitAsync(ct);
            try
            {
                if (_qwenAvailable) return true;

                _ollamaClient = new OllamaClient();
                if (!await _ollamaClient.IsOllamaRunning())
                {
                    log?.Invoke("🧠 VisionAnalyzer: Ollama is not running — Qwen2-VL unavailable");
                    return false;
                }

                // Detect and cache the exact model name — once per entire video
                if (await _ollamaClient.IsModelAvailable(QWEN_MODEL))
                    _resolvedQwenModel = QWEN_MODEL;
                else if (await _ollamaClient.IsModelAvailable(QWEN_MODEL_ALT))
                    _resolvedQwenModel = QWEN_MODEL_ALT;
                else if (await _ollamaClient.IsModelAvailable(QWEN_MODEL_ALT2))
                    _resolvedQwenModel = QWEN_MODEL_ALT2;

                bool hasQwen = _resolvedQwenModel != null;
                if (hasQwen)
                {
                    _qwenAvailable = true;
                    log?.Invoke($"✅ VisionAnalyzer: Qwen2-VL active ({_resolvedQwenModel}) — AI image analysis enabled");
                }
                else
                {
                    log?.Invoke($"⚠️ VisionAnalyzer: Qwen2-VL model is not installed.\n" +
                                $"   Install: ollama pull {QWEN_MODEL}\n" +
                                $"   Using ONNX/FFmpeg fallback.");
                }

                return _qwenAvailable;
            }
            catch
            {
                _qwenAvailable = false;
                return false;
            }
            finally
            {
                _qwenCheckLock.Release();
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static async Task<VisionResult> AnalyzeClipAsync(
            string videoPath,
            string ffmpegPath,
            CancellationToken ct = default,
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null)
        {
            if (!File.Exists(videoPath))
                return MakeResult(5.0, "File does not exist");

            // If videoPath is already an image (jpg/png) — we skip extraction
            string ext = Path.GetExtension(videoPath).ToLowerInvariant();
            bool isImage = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
            if (isImage)
            {
                // Direktna analiza slike bez ekstrakcije
                if (_qwenAvailable && _ollamaClient != null)
                {
                    var qwenResult = await QwenAnalyzeFrame(videoPath, ct, lyricLine, season, mood, context);
                    if (qwenResult != null)
                    {
                        var ffMetrics = await FfmpegFrameAnalyze(videoPath, ffmpegPath, ct);
                        if (qwenResult.Luminance  <= 0) qwenResult.Luminance  = ffMetrics.Luminance;
                        if (qwenResult.Saturation <= 0) qwenResult.Saturation = ffMetrics.Saturation;
                        if (qwenResult.Sharpness  <= 0) qwenResult.Sharpness  = ffMetrics.Sharpness;
                        return qwenResult;
                    }
                }
                if (_onnxAvailable && _onnxSession != null)
                {
                    var onnxResult = await OnnxAnalyzeFrame(videoPath, ct);
                    if (onnxResult != null)
                    {
                        var ffResult = await FfmpegFrameAnalyze(videoPath, ffmpegPath, ct);
                        onnxResult.Luminance  = ffResult.Luminance;
                        onnxResult.Saturation = ffResult.Saturation;
                        onnxResult.Sharpness  = ffResult.Sharpness;
                        double sharpScore = Math.Min(10.0, onnxResult.Sharpness / 10.0);
                        double lumScore   = CalcLumScore(onnxResult.Luminance);
                        onnxResult.Score  = Math.Round(
                            onnxResult.Score * 0.5 + sharpScore * 0.3 + lumScore * 0.2, 1);
                        return onnxResult;
                    }
                }
                return await FfmpegFrameAnalyze(videoPath, ffmpegPath, ct);
            }

            string tempFrame = Path.Combine(
                Path.GetTempPath(),
                "vision_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg");

            try
            {
                bool extracted = await ExtractFrame(videoPath, tempFrame, ffmpegPath, ct);
                if (!extracted)
                    return await FfmpegAnalyze(videoPath, ffmpegPath, ct);

                // ── Prioritet 1: Qwen2-VL via Ollama ────────────────────────
                // FIX (v1.4): Qwen used to see ONE frame from second 1, squeezed to 224×224
                // (the MobileNet input size, distorted from 16:9 to a square). Too small to
                // see faces, smiles or a kettlebell, and blind to anything after second 1.
                // Now Qwen gets a strip of 3 frames spread over the used part of the clip, at
                // 448 px width, aspect ratio kept. The small 224 frame is still used for ONNX/metrics.
                if (_qwenAvailable && _ollamaClient != null)
                {
                    string sheet = tempFrame.Replace(".jpg", "_sheet.jpg");
                    bool haveSheet = await ExtractContactSheet(videoPath, sheet, ffmpegPath, ct);
                    var qwenResult = await QwenAnalyzeFrame(haveSheet ? sheet : tempFrame, ct, lyricLine, season, mood, context, multiFrame: haveSheet);
                    try { if (File.Exists(sheet)) File.Delete(sheet); } catch { }
                    if (qwenResult != null)
                    {
                        // Dopunjavamo sa FFmpeg metrikama (luminance, saturation, sharpness)
                        var ffMetrics = await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
                        if (qwenResult.Luminance  <= 0) qwenResult.Luminance  = ffMetrics.Luminance;
                        if (qwenResult.Saturation <= 0) qwenResult.Saturation = ffMetrics.Saturation;
                        if (qwenResult.Sharpness  <= 0) qwenResult.Sharpness  = ffMetrics.Sharpness;
                        return qwenResult;
                    }
                }

                // ── Prioritet 2: ONNX MobileNetV2 ────────────────────────────
                if (_onnxAvailable && _onnxSession != null)
                {
                    var onnxResult = await OnnxAnalyzeFrame(tempFrame, ct);
                    if (onnxResult != null)
                    {
                        var ffResult = await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
                        onnxResult.Luminance  = ffResult.Luminance;
                        onnxResult.Saturation = ffResult.Saturation;
                        onnxResult.Sharpness  = ffResult.Sharpness;

                        double sharpScore = Math.Min(10.0, onnxResult.Sharpness / 10.0);
                        double lumScore   = CalcLumScore(onnxResult.Luminance);
                        onnxResult.Score  = Math.Round(
                            onnxResult.Score * 0.5 + sharpScore * 0.3 + lumScore * 0.2, 1);

                        return onnxResult;
                    }
                }

                // ── Prioritet 3: FFmpeg heuristika (fallback) ─────────────────
                return await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
            }
            finally
            {
                try { if (File.Exists(tempFrame)) File.Delete(tempFrame); } catch { }
            }
        }

        // ── Qwen2-VL analiza ──────────────────────────────────────────────────

        private static async Task<VisionResult> QwenAnalyzeFrame(
            string imagePath,
            CancellationToken ct,
            string lyricLine = null,
            string season = null,
            string mood = null,
            string context = null,
            bool multiFrame = false)
        {
            try
            {
                // Biramo model koji je dostupan
                // Using cached model — no 3x IsModelAvailable per frame
                string model = _resolvedQwenModel ?? QWEN_MODEL_ALT2;

                // Koristimo dinamicki prompt ako imamo kontekst, inace fallback na staticki
                // v1.4: always the short contextual prompt (QWEN_PROMPT carried a fixed example score)
                string prompt = BuildContextualQwenPrompt(lyricLine, season, mood, context);
                if (multiFrame)
                    prompt = "The image is a strip of 3 frames from the SAME video clip, left to right in time " +
                             "(beginning, middle, end of the part that will be used). Judge the WHOLE clip: if ANY frame breaks a " +
                             "reject rule, reject the clip. Describe the clip as a whole.\n\n" + prompt;

                var (response, error) = await _ollamaClient.VisionAsyncEx(imagePath, prompt, model, ct);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    // Log vidljivo u MainWindow, ne samo Debug
                    LogToMainWindowStatic($"   ⚠️ Qwen VisionError: {error.Substring(0, Math.Min(120, error.Length))}");
                    return null;
                }
                if (string.IsNullOrWhiteSpace(response))
                {
                    LogToMainWindowStatic("   ⚠️ Qwen: empty answer");
                    return null;
                }

                var parsed = ParseQwenResponse(response);
                if (parsed == null)
                    LogToMainWindowStatic($"   ⚠️ Qwen: parse failed. Response: {response.Substring(0, Math.Min(200, response.Length))}");
                return parsed;
            }
            catch
            {
                return null;
            }
        }

        private static VisionResult ParseQwenResponse(string raw)
        {
            try
            {
                // Qwen2.5vl may return text before JSON — extract only the JSON block
                string json = raw ?? "";

                // Ukloni markdown wrap
                json = Regex.Replace(json, @"```json?\s*|\s*```", "").Trim();

                // Ako ima tekst ispred { — uzmi samo od prvog { do zadnjeg }
                int braceStart = json.IndexOf('{');
                int braceEnd   = json.LastIndexOf('}');
                if (braceStart > 0 && braceEnd > braceStart)
                    json = json.Substring(braceStart, braceEnd - braceStart + 1);

                if (string.IsNullOrWhiteSpace(json) || !json.Contains("score"))
                    return null;

                // Jednostavan parser bez Newtonsoft ovisnosti na ovom nivou
                double  GetDouble(string key, double def = 0) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*([\\d.]+)");
                    return m.Success && double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
                }
                bool GetBool(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)");
                    return m.Success && m.Groups[1].Value == "true";
                }
                string GetString(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value : null;
                }
                string[] GetArray(string key) {
                    var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\\[([^\\]]+)\\]");
                    if (!m.Success) return Array.Empty<string>();
                    return Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")
                                .Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
                }

                double score     = GetDouble("score", 5.0);
                string rejectStr = GetString("reject");
                if (rejectStr != null && (rejectStr.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                                          rejectStr.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                                          rejectStr.Trim().Length == 0 || rejectStr.StartsWith("<")))
                    rejectStr = null;   // v1.4: "null" written as text is not a reason

                // Primijeni iste filteri kao ONNX/FFmpeg path
                string topLabel  = GetString("label") ?? "";
                string[] labels  = GetArray("labels");

                if (!string.IsNullOrEmpty(rejectStr))
                    return MakeResult(1.0, rejectStr);

                if (labels.Any(l => _badLabels.Contains(l)))
                    return MakeResult(1.0, string.Join(", ", labels.Where(l => _badLabels.Contains(l))));

                bool retryNeeded = GetBool("retry");

                return new VisionResult
                {
                    Score       = Math.Clamp(score, 1.0, 10.0),
                    IsOutdoor   = GetBool("outdoor"),
                    HasChildren = GetBool("children"),
                    HasFaces    = GetBool("faces"),
                    HasSmile    = GetBool("smile"),
                    IsWarm      = GetBool("warm"),
                    HasMotion   = GetBool("motion"),
                    Luminance   = GetDouble("luminance", 0.5),
                    Saturation  = GetDouble("saturation", 0.5),
                    Sharpness   = GetDouble("sharpness", 50),
                    TopLabel    = topLabel,
                    Labels      = labels,
                    OnnxUsed    = false,  // Qwen2-VL, ne ONNX
                    QwenUsed    = true,
                    RetryNeeded = retryNeeded,
                    Description = GetString("description")
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 3 frames side by side, 448 px wide each, aspect kept. The frames are spread over
        /// the part of the clip that is actually used (the first ~6 s; scenes are cut from the
        /// start of the clip): at 15 %, 50 % and 85 % of min(clip length, 6 s).
        /// Returns false on any problem — the caller then falls back to the single frame.
        /// </summary>
        private static async Task<bool> ExtractContactSheet(
            string videoPath, string outputPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) return false;
                if (File.Exists(outputPath)) File.Delete(outputPath);

                double clipDur = await ProbeDurationAsync(videoPath, ffmpegPath, ct);
                if (clipDur < 1.0) return false;   // unknown or very short → single frame is enough
                double window = Math.Min(clipDur, 6.0);
                double first = window * 0.15;
                double gap = window * 0.35;
                var inv = System.Globalization.CultureInfo.InvariantCulture;

                string args = $"-nostdin -y -ss {first.ToString("F2", inv)} -i \"{videoPath}\" " +
                              // tpad clones the last frame a little, otherwise fps drops the 3rd frame at EOF
                              $"-vf \"tpad=stop_mode=clone:stop_duration={gap.ToString("F2", inv)},fps={(1.0 / gap).ToString("F4", inv)},scale=336:-2,tile=3x1\" -frames:v 1 -q:v 3 \"{outputPath}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath, Arguments = args,
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                var se = proc.StandardError.ReadToEndAsync();
                var so = proc.StandardOutput.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                return proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>Clip length in seconds via ffprobe (next to ffmpeg.exe); 0 when unknown.</summary>
        private static async Task<double> ProbeDurationAsync(string videoPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                string probe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                if (!File.Exists(probe)) return 0;
                var psi = new ProcessStartInfo
                {
                    FileName = probe,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return 0;
                var so = proc.StandardOutput.ReadToEndAsync();
                var se = proc.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return 0;
                }
                string text = (await so).Trim();
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }

        private static async Task<bool> ExtractFrame(
            string videoPath, string outputPath, string ffmpegPath, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                    return false;

                if (File.Exists(outputPath)) File.Delete(outputPath);

                // -ss PRIJE -i = input seeking (brzo, bez dekodiranja cijelog videa)
                // PATCH 9: Robustno — probaj ss=1 prvi, pa fallback ss=0 ako fajl kratak
                string args = $"-nostdin -y -ss 1 -i \"{videoPath}\" -vframes 1 -vf scale=224:224 -q:v 2 \"{outputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName               = ffmpegPath,
                    Arguments              = args,
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardError  = true,   // MORA se citati asinhrono — inace deadlock!
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;

                // KRITICNO: citamo stderr ASINHRONO prije WaitForExit
                // Bez ovoga FFmpeg blokira na punom stderr bufferu (~4KB)
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await proc.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    ct.ThrowIfCancellationRequested();
                    return false;
                }

                bool valid = proc.ExitCode == 0
                          && File.Exists(outputPath)
                          && new FileInfo(outputPath).Length > 512;

                // PATCH 9: Fallback — if ss=1 didn't capture a frame (video shorter than 1s), try ss=0
                if (!valid && File.Exists(ffmpegPath))
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    string fallbackArgs = $"-nostdin -y -ss 0 -i \"{videoPath}\" -vframes 1 -vf scale=224:224 -q:v 2 \"{outputPath}\"";
                    var psi2 = new ProcessStartInfo
                    {
                        FileName = ffmpegPath, Arguments = fallbackArgs,
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardError = true, RedirectStandardOutput = true
                    };
                    using var proc2 = Process.Start(psi2);
                    if (proc2 != null)
                    {
                        _ = proc2.StandardError.ReadToEndAsync();
                        _ = proc2.StandardOutput.ReadToEndAsync();
                        using var t2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        t2.CancelAfter(TimeSpan.FromSeconds(10));
                        try { await proc2.WaitForExitAsync(t2.Token); } catch { try { proc2.Kill(true); } catch { } }
                        valid = proc2.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 512;
                    }
                }
                return valid;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static async Task<VisionResult> OnnxAnalyzeFrame(string imagePath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                try
                {
                    float[] inputData = LoadImageAsTensor(imagePath);
                    if (inputData == null) return null;

                    string[] topLabels = RunOnnxInference(inputData);
                    if (topLabels == null || topLabels.Length == 0) return null;

                    var result = new VisionResult { OnnxUsed = true };
                    result.TopLabel = topLabels[0];
                    result.Labels = topLabels;

                    bool hasBad = topLabels.Any(l => _badLabels.Contains(l));
                    result.HasChildren = topLabels.Any(l => _childLabels.Contains(l));
                    result.HasFaces = topLabels.Any(l => _faceLabels.Contains(l));
                    result.IsOutdoor = topLabels.Any(l => _outdoorLabels.Contains(l));

                    if (hasBad)
                    {
                        result.Score = 1.0;
                        string badLabel = topLabels.FirstOrDefault(l => _badLabels.Contains(l)) ?? "";
                        result.RejectReason = "Neprikladan sadrzaj: " + badLabel;
                    }
                    else
                    {
                        double score = 5.0;
                        if (result.HasChildren) score += 2.5;
                        if (result.HasFaces) score += 1.5;
                        if (result.IsOutdoor) score += 1.0;
                        result.Score = Math.Min(10.0, score);
                    }

                    return result;
                }
                catch { return null; }
            }, ct);
        }

        private static async Task<VisionResult> FfmpegAnalyze(
            string videoPath, string ffmpegPath, CancellationToken ct)
        {
            string tempFrame = Path.Combine(Path.GetTempPath(),
                "vis_" + Guid.NewGuid().ToString().Substring(0, 8) + ".png");
            try
            {
                await ExtractFrame(videoPath, tempFrame, ffmpegPath, ct);
                return await FfmpegFrameAnalyze(tempFrame, ffmpegPath, ct);
            }
            finally
            {
                try { if (File.Exists(tempFrame)) File.Delete(tempFrame); } catch { }
            }
        }

        private static async Task<VisionResult> FfmpegFrameAnalyze(
            string framePath, string ffmpegPath, CancellationToken ct)
        {
            var result = new VisionResult { OnnxUsed = false };

            if (!File.Exists(framePath) || !File.Exists(ffmpegPath))
                return MakeResult(6.0, "FFmpeg is not available");

            try
            {
                // v1.4: signalstats only stores its numbers as frame metadata — without metadata=print
                // nothing was printed, so brightness and colour were always the defaults.
                string args = "-nostdin -i \"" + framePath + "\" -vf \"signalstats,metadata=mode=print,blurdetect\" -f null -";
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return MakeResult(6.0, "FFmpeg pokretanje neuspjesno");
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);

                double luminance = 128.0;
                var lumMatch = Regex.Match(stderr, @"YAVG[=:]([\d.]+)");
                if (lumMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(lumMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        luminance = parsed;
                    }
                }

                double saturation = 50.0;
                var satMatch = Regex.Match(stderr, @"SATAVG[=:]([\d.]+)");
                if (satMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(satMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        saturation = parsed;
                    }
                }

                // v1.4: blurdetect prints "blur mean: X" (about 5 for a sharp frame, 25+ for a
                // very soft one); it never printed "blur_ratio". Mapped to the old 0..1 ratio.
                double blurRatio = 0.5;
                var blurMeanMatch = Regex.Match(stderr, @"blur mean:\s*([\d.]+)");
                if (blurMeanMatch.Success && double.TryParse(blurMeanMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double blurMean))
                    blurRatio = Math.Clamp(blurMean * 0.035, 0.0, 1.0);
                var blurMatch = Regex.Match(stderr, @"blur_ratio:([\d.]+)");
                if (blurMatch.Success)
                {
                    double parsed;
                    if (double.TryParse(blurMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    {
                        blurRatio = parsed;
                    }
                }

                result.Luminance = luminance;
                result.Saturation = saturation;
                result.Sharpness = Math.Round((1.0 - blurRatio) * 100.0, 1);
                result.IsWarm = saturation > 30 && luminance > 60;
                result.IsOutdoor = luminance > 70;

                double lumScore = CalcLumScore(luminance);

                double satScore;
                if (saturation < 10) satScore = 2.0;
                else if (saturation < 30) satScore = 5.0;
                else if (saturation <= 120) satScore = 10.0;
                else satScore = 6.0;

                double sharpScore;
                if (result.Sharpness > 60) sharpScore = 10.0;
                else if (result.Sharpness > 30) sharpScore = 7.0;
                else if (result.Sharpness > 10) sharpScore = 4.0;
                else sharpScore = 1.0;

                result.Score = Math.Round(lumScore * 0.4 + satScore * 0.3 + sharpScore * 0.3, 1);

                if (result.Score < 4.0)
                {
                    result.RejectReason = "Low quality: lum=" + luminance.ToString("F0") +
                        ", sat=" + saturation.ToString("F0") +
                        ", sharp=" + result.Sharpness.ToString("F0");
                }

                return result;
            }
            catch
            {
                return MakeResult(6.0, LanguageManager.GetText("va_analysis_error", LanguageManager.CurrentLanguage ?? "en"));
            }
        }

        private static bool IsOnnxRuntimeAvailable()
        {
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");
                return asm != null;
            }
            catch { return false; }
        }

        private static object CreateOnnxSession(string modelPath)
        {
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");
                var sessionType = asm.GetType("Microsoft.ML.OnnxRuntime.InferenceSession");
                return Activator.CreateInstance(sessionType, modelPath);
            }
            catch { return null; }
        }

        private static string[] RunOnnxInference(float[] inputData)
        {
            if (_onnxSession == null || _labels == null) return null;
            try
            {
                var asm = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");

                var tensorType = asm.GetType("Microsoft.ML.OnnxRuntime.Tensors.DenseTensor`1")
                    .MakeGenericType(typeof(float));
                var dims = new long[] { 1, 3, 224, 224 };
                var tensor = Activator.CreateInstance(tensorType, inputData, dims);

                var namedValueType = asm.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue");
                var createMethod = namedValueType
                    .GetMethod("CreateFromTensor")
                    .MakeGenericMethod(typeof(float));
                var namedInput = createMethod.Invoke(null, new object[] { "input", tensor });

                var inputList = Array.CreateInstance(namedValueType, 1);
                inputList.SetValue(namedInput, 0);

                var runMethod = _onnxSession.GetType().GetMethod("Run",
                    new[] { typeof(System.Collections.Generic.IEnumerable<>)
                        .MakeGenericType(namedValueType) });
                var outputs = runMethod.Invoke(_onnxSession, new object[] { inputList });

                var enumerator = ((System.Collections.IEnumerable)outputs).GetEnumerator();
                if (!enumerator.MoveNext()) return null;
                var firstOutput = enumerator.Current;

                var valueProp = firstOutput.GetType().GetProperty("Value");
                if (valueProp == null) return null;
                var outputTensor = valueProp.GetValue(firstOutput);

                var toArray = outputTensor.GetType().GetMethod("ToArray");
                var scores = (float[])toArray.Invoke(outputTensor, null);

                var indexed = scores
                    .Select((s, i) => new { S = s, I = i })
                    .OrderByDescending(x => x.S)
                    .Take(5)
                    .Where(x => x.I < _labels.Length)
                    .Select(x => _labels[x.I])
                    .ToArray();

                return indexed;
            }
            catch { return null; }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static float[] LoadImageAsTensor(string imagePath)
        {
            try
            {
                using var bmp     = new System.Drawing.Bitmap(imagePath);
                using var resized = new System.Drawing.Bitmap(bmp, 224, 224);

                float[] tensor = new float[3 * 224 * 224];
                float[] mean = { 0.485f, 0.456f, 0.406f };
                float[] std = { 0.229f, 0.224f, 0.225f };

                for (int y = 0; y < 224; y++)
                {
                    for (int x = 0; x < 224; x++)
                    {
                        var px = resized.GetPixel(x, y);
                        tensor[0 * 224 * 224 + y * 224 + x] = (px.R / 255f - mean[0]) / std[0];
                        tensor[1 * 224 * 224 + y * 224 + x] = (px.G / 255f - mean[1]) / std[1];
                        tensor[2 * 224 * 224 + y * 224 + x] = (px.B / 255f - mean[2]) / std[2];
                    }
                }
                return tensor;
            }
            catch { return null; }
        }

        private static double CalcLumScore(double lum)
        {
            if (lum < 30) return 1.0;
            if (lum < 60) return 4.0;
            if (lum < 80) return 6.5;
            if (lum <= 180) return 10.0;
            if (lum <= 210) return 7.0;
            return 3.0;
        }

        private static VisionResult MakeResult(double score, string reason)
        {
            return new VisionResult { Score = score, RejectReason = reason };
        }

        private static async Task<bool> DownloadFileAsync(
            string url, string destPath, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var stream = await client.GetStreamAsync(url, ct);
                using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(file, ct);
                return true;
            }
            catch { return false; }
        }
    }
}
