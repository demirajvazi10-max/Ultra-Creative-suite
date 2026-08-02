using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UltraStudio.Localization;

namespace UltraStudio.Services
{
    public class ImageSuggestion
    {
        public string Action { get; set; } = "none"; // brightness|contrast|saturation|sharpen|blur|grayscale|sepia|none
        public double Value { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Lokalni AI opis slike preko Ollame + Qwen2-VL — isti model i isti
    /// obrazac (localhost:11434) koji Video Editor već koristi za analizu
    /// kadrova. Bez API ključa, bez cloud-a, radi i bez interneta čim je
    /// model jednom preuzet.
    /// </summary>
    public class OllamaVisionClient
    {
        private const string OLLAMA_URL = "http://localhost:11434/api/chat";
        private const string VISION_MODEL = "qwen2.5vl:latest";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
        private static readonly HttpClient _pingClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        public async Task<bool> IsRunningAsync()
        {
            try
            {
                var r = await _pingClient.GetAsync("http://localhost:11434/api/tags");
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<string> ChatWithImageAsync(string prompt, string base64Image, CancellationToken ct)
        {
            if (!await IsRunningAsync())
                throw new Exception(Lang.T("ollama_not_running"));

            var payload = new
            {
                model = VISION_MODEL,
                messages = new[]
                {
                    new { role = "user", content = prompt, images = new[] { base64Image } }
                },
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(OLLAMA_URL, content, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Ollama API error {(int)response.StatusCode}: {responseJson}");

            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        /// <summary>
        /// Bogat, slobodan opis slike — BEZ prisile na strukturu, da model ne
        /// "štedi" na detaljima zarad ispravnog formatiranja. Isti prompt kao
        /// prvobitna verzija koja je davala bolje opise.
        /// </summary>
        public async Task<string> DescribeImageAsync(string base64Image, CancellationToken ct = default)
        {
            const string prompt =
                "Describe this image in detail for someone who cannot see it: what's in the frame, " +
                "the people/objects and what they're doing, the setting, composition, lighting, colors, " +
                "and mood. Be thorough and specific, several sentences.";
            return await ChatWithImageAsync(prompt, base64Image, ct);
        }

        /// <summary>
        /// POSEBAN, kratak poziv za SVE relevantne predloge odjednom (ne samo jedan) —
        /// AI prolazi kroz svaku kategoriju podešavanja i predlaže samo one koje
        /// stvarno imaju smisla za ovu sliku.
        /// </summary>
        public async Task<List<ImageSuggestion>> SuggestEditsAsync(string base64Image, CancellationToken ct = default)
        {
            const string prompt =
                "Look at this image as a photo editor would. Consider EACH of these adjustment types: " +
                "brightness, contrast, saturation, sharpen, blur, grayscale, sepia. " +
                "Respond with ONLY a JSON array, nothing else, no markdown fences — one object per adjustment " +
                "you'd actually recommend (skip any that wouldn't meaningfully help this image; the array can " +
                "be empty if none are needed):\n" +
                "[{\"action\": \"brightness|contrast|saturation|sharpen|blur|grayscale|sepia\", " +
                "\"value\": a number from -100 to 100 (0-10 for sharpen/blur), " +
                "\"reason\": \"short reason, under 12 words\"}]\n" +
                "Only include adjustments with a genuine, specific reason — not a generic list of everything.";

            string raw = await ChatWithImageAsync(prompt, base64Image, ct);
            string jsonPart = ExtractJsonArray(raw);
            var result = new List<ImageSuggestion>();

            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string action = item.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(action) || action == "none") continue;
                    result.Add(new ImageSuggestion
                    {
                        Action = action,
                        Value = item.TryGetProperty("value", out var v) ? v.GetDouble() : 0,
                        Reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""
                    });
                }
            }
            catch { /* prazna lista ako parsiranje padne — bolje nego pući */ }

            return result;
        }

        /// <summary>
        /// Traži od AI-ja da locira opisani objekat na slici i vrati JEDNU tačku
        /// (u pikselima originalne rezolucije) unutar njega — ulaz za SAM segmentaciju.
        /// </summary>
        public async Task<(int x, int y)?> FindPointForDescriptionAsync(
            string base64Image, int origWidth, int origHeight, string description, CancellationToken ct = default)
        {
            string prompt =
                $"This image is {origWidth}x{origHeight} pixels. Find \"{description}\" in the image and respond " +
                "with ONLY a JSON object (no other text): {\"found\": true or false, \"x\": pixel x coordinate, " +
                "\"y\": pixel y coordinate} — x,y should be a point roughly in the CENTER of that object, " +
                "in the original image's pixel coordinates (0,0 is top-left).";

            string raw = await ChatWithImageAsync(prompt, base64Image, ct);
            string jsonPart = ExtractJson(raw);

            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                var root = doc.RootElement;
                bool found = root.TryGetProperty("found", out var f) && f.GetBoolean();
                if (!found) return null;
                int x = root.GetProperty("x").GetInt32();
                int y = root.GetProperty("y").GetInt32();
                return (Math.Clamp(x, 0, origWidth - 1), Math.Clamp(y, 0, origHeight - 1));
            }
            catch { return null; }
        }

        private static string ExtractJson(string raw)
        {
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end < start) return raw;
            return raw.Substring(start, end - start + 1);
        }

        private static string ExtractJsonArray(string raw)
        {
            int start = raw.IndexOf('[');
            int end = raw.LastIndexOf(']');
            if (start < 0 || end < start) return "[]";
            return raw.Substring(start, end - start + 1);
        }
    }
}
