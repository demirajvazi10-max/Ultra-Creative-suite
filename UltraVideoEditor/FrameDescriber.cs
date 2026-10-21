using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4 (R4) — "what is in the picture right here?" for ANY clip, including your own footage.
    ///
    ///  • <see cref="DescribeFrameAsync"/> describes one exact frame (the one under the playhead).
    ///  • <see cref="DescribeByPartsAsync"/> finds the shot changes inside a clip and describes
    ///    every part, so the editor hears what is on screen while moving through the clip and
    ///    can decide where to cut — what a sighted editor does by looking at the viewer.
    ///
    /// Uses the local vision model through Ollama (Qwen2.5-VL first, the model the AI music video
    /// creator already uses). Nothing leaves the computer.
    /// </summary>
    public static class FrameDescriber
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static string _model;
        // v1.4.5: Qwen3-VL first (much better descriptions); the instruct variant before "thinking"
        private static readonly string[] ModelPreference = { "qwen3-vl", "qwen2.5vl", "qwen2.5-vl", "qwen2-vl", "llava", "moondream" };

        /// <summary>Shortest part a clip is divided into, and the longest before it is split.</summary>
        private const double MinPart = 2.0, MaxPart = 8.0;
        private const int MaxParts = 40;

        /// <summary>The installed vision model, or null when none is installed / Ollama is not running.</summary>
        public static async Task<string> FindModelAsync()
        {
            if (_model != null) return _model;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string body = await client.GetStringAsync("http://localhost:11434/api/tags");
                var names = (JObject.Parse(body)["models"] as JArray)?
                            .Select(m => m["name"]?.ToString() ?? "").ToList() ?? new List<string>();
                foreach (string pref in ModelPreference)
                {
                    var hits = names.Where(n => n.StartsWith(pref, StringComparison.OrdinalIgnoreCase)).ToList();
                    string hit = hits.FirstOrDefault(n => !n.Contains("thinking", StringComparison.OrdinalIgnoreCase)) ?? hits.FirstOrDefault();
                    if (hit != null) return _model = hit;
                }
            }
            catch { }
            return null;
        }

        /// <summary>What kind of picture a file has.</summary>
        public enum PictureKind { None, Cover, Video, Image }

        /// <summary>Result of describing one frame: the text, or why there is none.</summary>
        public sealed class FrameResult
        {
            public string Text;
            /// <summary>"no_model", "no_picture" or "failed"; null on success.</summary>
            public string Error;
            /// <summary>For "failed": the last line FFmpeg or Ollama wrote, for the log.</summary>
            public string Detail;
            /// <summary>True when the file only has a still cover picture (a song with album art).</summary>
            public bool CoverOnly;
        }

        private static readonly Dictionary<string, PictureKind> _kinds = new Dictionary<string, PictureKind>();

        /// <summary>
        /// v1.4 — does the file have moving pictures, only a cover picture, or no picture at all?
        /// A song exported as .mp4 often has no video, or just album art stored as one frame
        /// ("attached picture"); asking FFmpeg for the frame at 0:34 of such a file gives nothing,
        /// which is why describing those clips used to fail.
        /// </summary>
        public static async Task<PictureKind> ProbePictureAsync(string path, CancellationToken ct)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp") return PictureKind.Image;
            lock (_kinds) { if (_kinds.TryGetValue(path, out var known)) return known; }

            var kind = PictureKind.Video;   // when ffprobe is missing, assume the usual case
            string probe = AppPaths.FFprobe;
            if (File.Exists(probe))
            {
                try
                {
                    using var p = new Process
                    {
                        StartInfo = new ProcessStartInfo(probe,
                            $"-v error -select_streams v -show_entries stream=codec_name:stream_disposition=attached_pic -of csv=p=0 \"{path}\"")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        }
                    };
                    p.Start();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.DrainStderr();
                    await p.WaitForExitOrKillAsync(ct);
                    var lines = (await outTask).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    if (lines.Count == 0) kind = PictureKind.None;
                    else if (lines.All(l => l.EndsWith(",1"))) kind = PictureKind.Cover;
                    else kind = PictureKind.Video;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Error("Probing the picture streams of " + path, ex); }
            }
            lock (_kinds) { _kinds[path] = kind; }
            return kind;
        }

        /// <summary>Describes the frame at <paramref name="fileTime"/> seconds of the file.</summary>
        public static Task<FrameResult> DescribeFrameAsync(string path, double fileTime, CancellationToken ct) =>
            DescribeFrameAsync(path, fileTime, false, ct);

        /// <summary>
        /// <paramref name="forViewers"/>: a short sentence for blind VIEWERS (audio description) instead of the
        /// editor's description (no shot sizes, no "the image shows").
        /// </summary>
        public static async Task<FrameResult> DescribeFrameAsync(string path, double fileTime, bool forViewers, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new FrameResult { Error = "no_picture" };

            var kind = await ProbePictureAsync(path, ct);
            if (kind == PictureKind.None) return new FrameResult { Error = "no_picture" };
            bool still = kind != PictureKind.Video;   // a picture or a cover: one frame, no seeking
            if (still) fileTime = 0;

            // v1.4.5: the language is part of the key — before, a description made in English was
            // given back from the cache after switching the program to Serbian. The English
            // description (what the model writes) is kept too, so another language only translates.
            string lang = LanguageManager.CurrentLanguage ?? "en";
            string where = $"{path}|{Math.Round(Math.Max(0, fileTime), 1).ToString(CultureInfo.InvariantCulture)}";
            string key = $"{(forViewers ? "V|" : "")}{lang}|{where}";
            string enKey = $"{(forViewers ? "V|" : "")}en|{where}";
            string english;
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return new FrameResult { Text = cached, CoverOnly = kind == PictureKind.Cover };
                _cache.TryGetValue(enKey, out english);
            }

            string model = await FindModelAsync();
            if (model == null && !GeminiClient.HasKey) return new FrameResult { Error = "no_model" };
            if (english != null)
            {
                string again = await TranslateAsync(model, english, lang, ct);
                lock (_cache) { _cache[key] = again; }
                return new FrameResult { Text = again, CoverOnly = kind == PictureKind.Cover };
            }

            string frame = Path.Combine(Path.GetTempPath(), $"uve_frame_{Guid.NewGuid():N}.jpg");
            try
            {
                var inv = CultureInfo.InvariantCulture;
                var err = new StringBuilder();
                string ss = fileTime > 0.01 ? $"-ss {fileTime.ToString("0.###", inv)} " : "";
                // -update 1: one picture, not an image sequence (FFmpeg 7+ warns otherwise)
                string tail = $"-map 0:v:0 -frames:v 1 -vf \"scale=448:-2\" -q:v 3 -update 1 \"{frame}\"";
                await RunFfmpegAsync($"-nostdin -y -hide_banner -loglevel error {ss}-i \"{path}\" {tail}", ct, err);
                if (!File.Exists(frame) && ss.Length > 0)
                {
                    // past the end of the file, or a stream with very few frames: try the first frame
                    err.Clear();
                    await RunFfmpegAsync($"-nostdin -y -hide_banner -loglevel error -i \"{path}\" {tail}", ct, err);
                }
                if (!File.Exists(frame) || new FileInfo(frame).Length < 100)
                {
                    string detail = LastLine(err.ToString());
                    AppLog.Error($"Taking a frame from '{path}' at {fileTime.ToString("0.##", inv)} s", new Exception(detail));
                    return new FrameResult { Error = "failed", Detail = detail };
                }

                string base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(frame, ct));

                // v1.4.5: with a Gemini key, Gemini looks at the picture and writes directly in the
                // program language (natural Serbian); the local model is the fallback
                if (GeminiClient.HasKey)
                {
                    try
                    {
                        string g = await GeminiClient.AskAsync(Prompt(forViewers, GeminiClient.LanguageName(lang)), base64, ct);
                        if (!string.IsNullOrWhiteSpace(g))
                        {
                            g = Clean(g);
                            if (lang == "en" && forViewers) g = StripCameraWords(g);
                            if (lang == "sr") g = CyrillicToLatin(g);
                            lock (_cache) { _cache[key] = g; }
                            return new FrameResult { Text = g, CoverOnly = kind == PictureKind.Cover };
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { LogTranslateOnce("Gemini (describing)", ex); }
                    if (model == null) return new FrameResult { Error = "failed", Detail = "Gemini gave no answer." };
                }

                string text = await AskModelAsync(model, base64, forViewers, ct);
                if (string.IsNullOrWhiteSpace(text)) return new FrameResult { Error = "failed", Detail = "The vision model gave no answer." };
                text = Clean(text);
                if (forViewers) text = StripCameraWords(text);
                lock (_cache) { _cache[enKey] = text; }
                text = await TranslateAsync(model, text, lang, ct);
                lock (_cache) { _cache[key] = text; }
                return new FrameResult { Text = text, CoverOnly = kind == PictureKind.Cover };
            }
            finally
            {
                try { File.Delete(frame); } catch { }
            }
        }

        private static string LastLine(string s)
        {
            var lines = (s ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count == 0 ? "FFmpeg wrote no picture." : lines[^1];
        }

        /// <summary>
        /// Divides the clip at its shot changes (parts of 2–8 seconds) and describes each part.
        /// Times in the result are seconds from the clip's start on the timeline.
        /// <paramref name="progress"/> gets (done, total) after every part. Returns null when no
        /// vision model is available; <paramref name="problem"/> gets "no_picture", "cover" or the
        /// last error when the clip could not be described normally.
        /// </summary>
        public static async Task<List<FrameDescription>> DescribeByPartsAsync(
            TimelineItem item, Action<int, int> progress, Action<string> problem, CancellationToken ct)
        {
            var result = new List<FrameDescription>();
            if (item == null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path)) { problem?.Invoke("no_picture"); return result; }
            if (!await CanDescribeAsync()) return null;

            double dur = Math.Max(0.1, item.Duration);
            var kind = await ProbePictureAsync(item.Path, ct);
            if (kind == PictureKind.None) { problem?.Invoke("no_picture"); return result; }
            if (item.IsImage || kind != PictureKind.Video)
            {
                // one still picture: one description for the whole clip
                var r = await DescribeFrameAsync(item.Path, 0, ct);
                if (r.Text != null) result.Add(new FrameDescription { Start = 0, End = dur, Text = r.Text });
                else problem?.Invoke(r.Error == "failed" ? r.Detail : r.Error);
                if (kind == PictureKind.Cover) problem?.Invoke("cover");
                progress?.Invoke(1, 1);
                return result;
            }

            double speed = item.VideoEffect?.Speed is double s && s > 0.05 && s < 20 ? s : 1.0;
            var cuts = await FindShotChangesAsync(item.Path, item.SourceOffset, dur * speed, ct);
            // file seconds → timeline seconds inside the clip
            var bounds = new List<double> { 0 };
            bounds.AddRange(cuts.Select(c => c / speed).Where(c => c > 0.05 && c < dur - 0.05));
            bounds.Add(dur);
            var parts = BuildParts(bounds, dur);

            for (int i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (a, b) = parts[i];
                double mid = (a + b) / 2.0;
                var r = await DescribeFrameAsync(item.Path, item.SourceOffset + mid * speed, ct);
                if (!string.IsNullOrWhiteSpace(r.Text))
                    result.Add(new FrameDescription { Start = a, End = b, Text = r.Text });
                else if (r.Error == "no_model") return null;
                else if (result.Count == 0 && i >= 2)
                {
                    // the first three parts all failed — the rest will too; say why instead of
                    // grinding through every part
                    problem?.Invoke(r.Detail ?? r.Error);
                    return result;
                }
                progress?.Invoke(i + 1, parts.Count);
            }
            return result;
        }

        /// <summary>Merges parts shorter than 2 s, splits parts longer than 8 s, at most 40 parts.</summary>
        private static List<(double, double)> BuildParts(List<double> bounds, double dur)
        {
            bounds = bounds.Distinct().OrderBy(x => x).ToList();
            var merged = new List<double> { 0 };
            foreach (double b in bounds.Skip(1))
            {
                if (b - merged[^1] < MinPart && b < dur) continue;   // too short: joins the next part
                merged.Add(b);
            }
            if (merged.Count > 2 && dur - merged[^2] < MinPart) merged.RemoveAt(merged.Count - 2);

            var parts = new List<(double, double)>();
            for (int i = 0; i + 1 < merged.Count; i++)
            {
                double a = merged[i], b = merged[i + 1];
                int pieces = Math.Max(1, (int)Math.Ceiling((b - a) / MaxPart));
                for (int k = 0; k < pieces; k++)
                    parts.Add((a + (b - a) * k / pieces, a + (b - a) * (k + 1) / pieces));
            }
            if (parts.Count > MaxParts)
            {
                // a very long clip: equal parts instead
                parts.Clear();
                for (int k = 0; k < MaxParts; k++) parts.Add((dur * k / MaxParts, dur * (k + 1) / MaxParts));
            }
            return parts;
        }

        /// <summary>Shot changes (seconds from <paramref name="offset"/>) found with FFmpeg's scene score.</summary>
        public static async Task<List<double>> FindShotChangesAsync(string path, double offset, double length, CancellationToken ct)
        {
            var times = new List<double>();
            var inv = CultureInfo.InvariantCulture;
            string ss = offset > 0.01 ? $"-ss {offset.ToString("0.###", inv)} " : "";
            var sb = new StringBuilder();
            await RunFfmpegAsync($"-nostdin -hide_banner {ss}-t {length.ToString("0.###", inv)} -i \"{path}\" -an " +
                                 "-vf \"scale=320:-2,select='gt(scene,0.3)',showinfo\" -f null -", ct, sb);
            foreach (Match m in Regex.Matches(sb.ToString(), @"pts_time:([0-9.]+)"))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, inv, out double t)) times.Add(t);
            return times;
        }

        private static string Prompt(bool forViewers, string lang) => forViewers
            // v1.4: audio description, like on Netflix — what a blind viewer needs, briefly
            ? "You write audio description for blind viewers of a music video. Write ONE short, complete " +
              "sentence (6 to 12 words, present tense) that says who is there and what happens. Always finish " +
              "the sentence. If the frame is mostly text (a title or credits), just read the text. Do not " +
              $"mention the camera, the shot, the image or the frame. No introduction. Answer only in {lang}."
            : "You describe video frames for a blind video editor. In one or two short sentences say " +
              "who or what is in the picture, what is happening, where, and the shot size " +
              "(close-up, medium shot or wide shot). Mention readable text if there is any. " +
              $"No introduction, do not start with 'The image'. Answer in {lang}.";

        /// <summary>Something can describe pictures: Gemini (with a key) or a local vision model.</summary>
        public static async Task<bool> CanDescribeAsync() => GeminiClient.HasKey || await FindModelAsync() != null;

        private static async Task<string> AskModelAsync(string model, string base64, bool forViewers, CancellationToken ct)
        {
            // v1.4.5: always English — the description is translated afterwards (TranslateAsync)
            var request = new
            {
                model,
                prompt = Prompt(forViewers, "English"),
                images = new[] { base64 },
                stream = false,
                keep_alive = "30m",
                options = new { temperature = 0.1, num_predict = 200, num_ctx = 4096 }
            };
            var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("http://localhost:11434/api/generate", content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["response"]?.ToString()?.Trim();
        }

        /// <summary>
        /// v1.4.5: the vision model always describes in ENGLISH (the small models describe well in
        /// English, but their Serbian was poor: Croatian forms, invented words, "Detaljno…"). The
        /// English sentence is then translated: with Google Cloud Translation when a Google Cloud
        /// key is saved, otherwise with the free Google Translate service, and only when both fail
        /// by the local model. Serbian comes back in Latin letters.
        /// </summary>
        private static async Task<string> TranslateAsync(string model, string english, string lang, CancellationToken ct)
        {
            if (lang == "en" || string.IsNullOrWhiteSpace(english)) return english;
            string result = null;
            if (GeminiClient.HasKey)
            {
                try { result = await GeminiClient.TranslateAsync(english, lang, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogTranslateOnce("Gemini", ex); }
            }
            string key = string.IsNullOrWhiteSpace(result) ? NeuralTts.GoogleKey : null;
            if (!string.IsNullOrEmpty(key))
            {
                try { result = await CloudTranslateAsync(english, lang, key, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogTranslateOnce("Google Cloud Translation", ex); }
            }
            if (string.IsNullOrWhiteSpace(result))
            {
                try { result = await FreeTranslateAsync(english, lang, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogTranslateOnce("Google Translate", ex); }
            }
            if (string.IsNullOrWhiteSpace(result) && model != null)
            {
                try { result = await ModelTranslateAsync(model, english, lang, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogTranslateOnce("local model", ex); }
            }
            if (string.IsNullOrWhiteSpace(result)) return english;
            result = Clean(System.Net.WebUtility.HtmlDecode(result));
            return lang == "sr" ? CyrillicToLatin(result) : result;
        }

        private static readonly HashSet<string> _translateLogged = new HashSet<string>();
        private static void LogTranslateOnce(string service, Exception ex)
        {
            lock (_translateLogged) { if (!_translateLogged.Add(service)) return; }
            AppLog.Error($"Translating a picture description with {service}", ex);
        }

        private static async Task<string> CloudTranslateAsync(string text, string lang, string key, CancellationToken ct)
        {
            var body = new { q = text, source = "en", target = lang, format = "text" };
            var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(key), content, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(JObject.Parse(json)["error"]?["message"]?.ToString() ?? resp.StatusCode.ToString());
            return JObject.Parse(json)["data"]?["translations"]?[0]?["translatedText"]?.ToString();
        }

        private static async Task<string> FreeTranslateAsync(string text, string lang, CancellationToken ct)
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl={lang}&dt=t&q={Uri.EscapeDataString(text)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var parts = JArray.Parse(await resp.Content.ReadAsStringAsync(ct))[0] as JArray;
            if (parts == null) return null;
            var sb = new StringBuilder();
            foreach (var p in parts) sb.Append(p?[0]?.ToString());
            return sb.ToString();
        }

        private static async Task<string> ModelTranslateAsync(string model, string text, string lang, CancellationToken ct)
        {
            string target = lang == "sr" ? "Serbian (ekavian, as spoken in Serbia), written in the Latin alphabet" : "German";
            var request = new
            {
                model,
                prompt = $"Translate this sentence into {target}. Keep it short and natural. " +
                         $"Reply with the translation only, nothing else.\n\n{text}",
                stream = false,
                keep_alive = "30m",
                options = new { temperature = 0.1, num_predict = 200 }
            };
            var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("http://localhost:11434/api/generate", content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["response"]?.ToString()?.Trim();
        }

        /// <summary>
        /// Camera words at the start of a viewer description ("Close-up of a girl…" → "A girl…"):
        /// the viewer prompt forbids them, but small models still write them.
        /// </summary>
        private static string StripCameraWords(string text)
        {
            string t = System.Text.RegularExpressions.Regex.Replace(text,
                @"^(an?\s+)?(extreme\s+)?(close[- ]?up|detailed|detail|wide|medium|long|aerial|overhead)(\s+(shot|view|image|scene|picture))?\s+(of|shows|showing|with)\s+",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            t = System.Text.RegularExpressions.Regex.Replace(t, @"^(in\s+)?(this|the)\s+(shot|frame|scene|picture),?\s+", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (t.Length < 3) return text;
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        private static readonly HashSet<string> EnglishWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "with", "and", "in", "on", "of", "her", "his", "their", "at", "to",
            "while", "through", "girl", "boy", "man", "woman", "child", "children", "people", "walks", "stands",
            "sits", "holding", "wearing", "standing", "sitting", "walking", "playing", "smiling", "looks", "text", "reads"
        };

        private static bool LooksEnglish(string text)
        {
            var words = System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[^\p{L}']+").Where(w => w.Length > 0).ToList();
            if (words.Count == 0) return false;
            int hits = words.Count(w => EnglishWords.Contains(w));
            return hits >= 2 && hits * 4 >= words.Count;   // a quarter of the words are common English words
        }

        private static string CyrillicToLatin(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Any(c => c >= '\u0400' && c <= '\u04FF')) return s;
            var map = new Dictionary<char, string>
            {
                ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['ђ']="đ",['е']="e",['ж']="ž",['з']="z",['и']="i",
                ['ј']="j",['к']="k",['л']="l",['љ']="lj",['м']="m",['н']="n",['њ']="nj",['о']="o",['п']="p",['р']="r",
                ['с']="s",['т']="t",['ћ']="ć",['у']="u",['ф']="f",['х']="h",['ц']="c",['ч']="č",['џ']="dž",['ш']="š"
            };
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                char lower = char.ToLowerInvariant(c);
                if (map.TryGetValue(lower, out string lat))
                    sb.Append(c == lower ? lat : char.ToUpperInvariant(lat[0]) + lat.Substring(1));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Clean(string text)
        {
            // a "thinking" model writes its reasoning first: only the answer after it is kept
            int think = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (think >= 0) text = text.Substring(think + 8);
            text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"');
            foreach (string p in new[] { "The image shows ", "This image shows ", "The image depicts ", "The video frames show ", "The video frame shows ", "The frame shows ", "In this image, ", "Na slici je ", "Na slici se vidi " })
                if (text.Length > p.Length + 1 && text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    text = char.ToUpper(text[p.Length]) + text.Substring(p.Length + 1);
            return text.Length > 300 ? text.Substring(0, 297) + "…" : text;
        }

        private static async Task<bool> RunFfmpegAsync(string args, CancellationToken ct, StringBuilder stderr)
        {
            if (!File.Exists(AppPaths.FFmpeg)) return false;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(AppPaths.FFmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };
            p.Start();
            p.DrainStdout();
            if (stderr != null)
            {
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
                p.BeginErrorReadLine();
            }
            else p.DrainStderr();
            await p.WaitForExitOrKillAsync(ct);
            return p.ExitCode == 0;
        }
    }
}
