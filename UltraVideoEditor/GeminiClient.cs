using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4.5 — Google Gemini (optional, with your own key from Google AI Studio).
    /// When a key is saved, Gemini describes the pictures for the audio description and the clip
    /// descriptions, directly in the program language, and translates. Gemini writes natural
    /// Serbian, which the small local vision models cannot. The Flash models have a free tier;
    /// without a key everything works as before (local model + Google Translate).
    ///
    /// The model is chosen from the key's own model list (the newest general "flash" model), so
    /// the program keeps working when Google renames or retires models.
    /// </summary>
    public static class GeminiClient
    {
        public const string KeyName = "gemini";
        public static string Key => KeyStore.Load(KeyName);
        public static bool HasKey => !string.IsNullOrEmpty(Key);

        private const string Base = "https://generativelanguage.googleapis.com/v1beta/";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static string _model;
        private static string _modelForKey;

        /// <summary>The language instruction for the answer.</summary>
        public static string LanguageName(string lang) => lang switch
        {
            "sr" => "Serbian (ekavian, as spoken in Serbia), written in the Latin alphabet",
            "de" => "German",
            _ => "English"
        };

        /// <summary>The newest general Flash model this key can use (not lite, live, TTS, image or preview when a stable one exists).</summary>
        private static async Task<string> ModelAsync(string key, CancellationToken ct)
        {
            if (_model != null && _modelForKey == key) return _model;
            string chosen = "gemini-flash-latest";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, Base + "models?pageSize=200");
                req.Headers.Add("x-goog-api-key", key);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var models = (JObject.Parse(await resp.Content.ReadAsStringAsync(ct))["models"] as JArray) ?? new JArray();
                    var flash = models
                        .Where(m => (m["supportedGenerationMethods"] as JArray)?.Any(x => x.ToString() == "generateContent") == true)
                        .Select(m => m["name"]?.ToString()?.Replace("models/", ""))
                        .Where(n => n != null && Regex.IsMatch(n, @"^gemini-[\d.]+-flash(-\d+)?$"))
                        .Select(n => (Name: n, Version: ParseVersion(Regex.Match(n, @"gemini-([\d.]+)-").Groups[1].Value)))
                        .OrderByDescending(x => x.Version).ToList();
                    if (flash.Count > 0) chosen = flash[0].Name;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Error("Listing the Gemini models", ex); }
            _modelForKey = key;
            AppLog.Info($"Gemini model: {chosen}");
            return _model = chosen;
        }

        private static Version ParseVersion(string v)
        {
            v = (v ?? "").Trim('.');
            if (!v.Contains('.')) v += ".0";
            return Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);
        }

        /// <summary>Asks Gemini (text, and a JPEG picture when given). Busy (429) is retried a few times.</summary>
        public static Task<string> AskAsync(string prompt, string jpegBase64, CancellationToken ct, string key = null)
        {
            var parts = new List<object> { new { text = prompt } };
            if (jpegBase64 != null) parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = jpegBase64 } });
            return SendAsync(parts, new { temperature = 0.2, maxOutputTokens = 1024 }, _http, ct, key);
        }

        private static async Task<string> SendAsync(List<object> parts, object generationConfig, HttpClient http, CancellationToken ct, string key = null)
        {
            key ??= Key;
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("No Gemini key.");
            string model = await ModelAsync(key, ct);
            var body = new { contents = new[] { new { parts } }, generationConfig };
            string json = JsonConvert.SerializeObject(body);

            for (int attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}models/{model}:generateContent")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-goog-api-key", key);
                using var resp = await http.SendAsync(req, ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                if ((int)resp.StatusCode == 429 && attempt < 3)
                {
                    // the free tier allows a limited number of requests a minute: wait and try again
                    await Task.Delay(TimeSpan.FromSeconds(15 * (attempt + 1)), ct);
                    continue;
                }
                if ((int)resp.StatusCode == 404 && attempt == 0 && model != "gemini-flash-latest")
                {
                    model = _model = "gemini-flash-latest";   // the chosen model went away
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    string msg = null;
                    try { msg = JObject.Parse(text)["error"]?["message"]?.ToString(); } catch { }
                    throw new InvalidOperationException("Gemini: " + (msg ?? resp.StatusCode.ToString()));
                }
                var answer = JObject.Parse(text)["candidates"]?[0]?["content"]?["parts"] as JArray;
                string result = answer == null ? null
                    : string.Concat(answer.Where(p => p["thought"]?.Value<bool>() != true).Select(p => p["text"]?.ToString()));
                return result?.Trim();
            }
        }

        // ── whole video (v1.4.5) ───────────────────────────────────────────────

        /// <summary>For long requests (a whole video): no fixed time-out, the Stop button cancels.</summary>
        private static readonly HttpClient _longHttp = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>
        /// Uploads a video to the Gemini File API (resumable upload) and waits until Google has
        /// processed it. Returns the file's name ("files/…", for deleting) and URI (for asking).
        /// <paramref name="progress"/> gets "sent/total" while uploading.
        /// </summary>
        public static async Task<(string Name, string Uri)> UploadVideoAsync(string path, Action<string> progress, CancellationToken ct)
        {
            string key = Key;
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("No Gemini key.");
            long size = new System.IO.FileInfo(path).Length;

            using var start = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/upload/v1beta/files")
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { file = new { display_name = System.IO.Path.GetFileName(path) } }),
                                            Encoding.UTF8, "application/json")
            };
            start.Headers.Add("x-goog-api-key", key);
            start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            start.Headers.Add("X-Goog-Upload-Command", "start");
            start.Headers.Add("X-Goog-Upload-Header-Content-Length", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.Headers.Add("X-Goog-Upload-Header-Content-Type", "video/mp4");
            using var startResp = await _longHttp.SendAsync(start, ct);
            if (!startResp.IsSuccessStatusCode || !startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
                throw new InvalidOperationException("Gemini: the upload could not start (" + (int)startResp.StatusCode + " " +
                                                    await startResp.Content.ReadAsStringAsync(ct) + ")");
            string uploadUrl = urls.First();

            await using var file = System.IO.File.OpenRead(path);
            var content = new ProgressContent(file, size, sent => progress?.Invoke($"{sent * 100 / Math.Max(1, size)}/100"));
            content.Headers.ContentLength = size;
            using var up = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
            up.Headers.Add("X-Goog-Upload-Offset", "0");
            up.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            using var upResp = await _longHttp.SendAsync(up, ct);
            string upJson = await upResp.Content.ReadAsStringAsync(ct);
            if (!upResp.IsSuccessStatusCode) throw new InvalidOperationException("Gemini: the upload failed (" + upJson + ")");
            var f = JObject.Parse(upJson)["file"];
            string name = f?["name"]?.ToString(), uri = f?["uri"]?.ToString(), state = f?["state"]?.ToString();

            // Google prepares the video before it can be used (a few seconds to a few minutes)
            for (int i = 0; state != "ACTIVE" && i < 360; i++)
            {
                if (state == "FAILED") throw new InvalidOperationException("Gemini could not process the video.");
                await Task.Delay(5000, ct);
                using var get = new HttpRequestMessage(HttpMethod.Get, Base + name);
                get.Headers.Add("x-goog-api-key", key);
                using var getResp = await _longHttp.SendAsync(get, ct);
                if (getResp.IsSuccessStatusCode) state = JObject.Parse(await getResp.Content.ReadAsStringAsync(ct))["state"]?.ToString();
            }
            if (state != "ACTIVE") throw new InvalidOperationException("Gemini did not finish processing the video in time.");
            return (name, uri);
        }

        /// <summary>Deletes an uploaded file (Google would delete it after 48 hours anyway).</summary>
        public static async Task DeleteFileAsync(string name)
        {
            try
            {
                using var del = new HttpRequestMessage(HttpMethod.Delete, Base + name);
                del.Headers.Add("x-goog-api-key", Key);
                using var _ = await _longHttp.SendAsync(del);
            }
            catch (Exception ex) { AppLog.Error("Deleting the uploaded video from Gemini", ex); }
        }

        /// <summary>Asks about a part of an uploaded video (seconds from its start).</summary>
        public static Task<string> AskVideoAsync(string fileUri, double from, double to, bool lowResolution, string prompt, CancellationToken ct)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<object>
            {
                new
                {
                    file_data = new { mime_type = "video/mp4", file_uri = fileUri },
                    video_metadata = new { start_offset = Math.Floor(from).ToString(inv) + "s", end_offset = Math.Ceiling(to).ToString(inv) + "s" }
                },
                new { text = prompt }
            };
            object config = lowResolution
                ? new { temperature = 0.3, maxOutputTokens = 16384, mediaResolution = "MEDIA_RESOLUTION_LOW" }
                : new { temperature = 0.3, maxOutputTokens = 16384 };
            return SendAsync(parts, config, _longHttp, ct);
        }

        /// <summary>Upload content that reports how much has been sent.</summary>
        private sealed class ProgressContent : HttpContent
        {
            private readonly System.IO.Stream _source;
            private readonly long _size;
            private readonly Action<long> _progress;
            public ProgressContent(System.IO.Stream source, long size, Action<long> progress) { _source = source; _size = size; _progress = progress; }

            protected override async Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext context)
            {
                var buffer = new byte[1 << 20];
                long sent = 0;
                int read;
                while ((read = await _source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read);
                    sent += read;
                    _progress?.Invoke(sent);
                }
            }

            protected override bool TryComputeLength(out long length) { length = _size; return true; }
        }

        /// <summary>Translates an English sentence (for the audio description) into the program language.</summary>
        public static Task<string> TranslateAsync(string english, string lang, CancellationToken ct) =>
            AskAsync($"Translate this audio description sentence into {LanguageName(lang)}. Keep it short, natural and " +
                     $"in the present tense. Reply with the translation only.\n\n{english}", null, ct);

        /// <summary>A quick test of a key. Returns null when it works, otherwise the reason.</summary>
        public static async Task<string> TestKeyAsync(string key, CancellationToken ct)
        {
            try
            {
                _model = null;
                string r = await AskAsync("Reply with the single word: OK", null, ct, key);
                return string.IsNullOrWhiteSpace(r) ? "no answer" : null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
