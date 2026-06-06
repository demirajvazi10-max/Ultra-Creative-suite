// ══════════════════════════════════════════════════════════════════════════════
//  FoundryIQClient.cs  —  Microsoft Azure AI Foundry Integration
//  UltraVideoEditor  |  UltraCreativeSuite
//
//  ULOGA U ARHITEKTURI (Sloj 0 — ispred Ollame):
//
//  SLOJ 0 — Azure AI Foundry (accessibility hints via chat completions)
//    Koristi tvoj Azure AI Foundry project endpoint i API key.
//    Za svaki stih šalje upit Azure modelu koji vraća accessibility-aware
//    vizuelni kontekst (WCAG-based) koji se prosleđuje Ollami kao hint.
//
//  SLOJ 1 — Ollama (GLAVNI) — prima Azure hint kao kontekst
//  SLOJ 2 — StrictQueryEngine (FALLBACK)
//  SLOJ 3 — SmartFallback (NIKAD NULL)
//
//  Fallback: ako Azure nije dostupan (no API key, network error),
//  sistem transparentno pada na Sloj 1 (Ollama) — bez prekida rada.
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    /// <summary>
    /// Azure AI Foundry Client — Microsoft IQ intelligence layer.
    /// Koristi Azure AI Foundry chat completions API da generise
    /// accessibility-aware vizuelne hinove za slijepe i slabovide korisnike.
    /// </summary>
    public class FoundryIQClient
    {
        // ── Azure AI Foundry API ──────────────────────────────────────────────
        // Endpoint format: https://{resource}.services.ai.azure.com
        // API: /models/chat/completions?api-version=2024-05-01-preview
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _deploymentName;   // npr. "gpt-4o-mini" ili "gpt-4o"
        private readonly HttpClient _httpClient;

        private static readonly HttpClient _checkClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // Cache — isti stih+sentiment ne šalje se dva puta
        private readonly Dictionary<string, string> _hintCache
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Circuit breaker
        private bool _available = true;
        private DateTime _lastFailure = DateTime.MinValue;
        private const int RETRY_AFTER_MINUTES = 5;

        // System prompt za accessibility hints
        private const string SYSTEM_PROMPT =
            "You are an accessibility expert for video content. " +
            "Your role is to provide brief, practical accessibility hints for video scenes " +
            "based on WCAG 2.2 guidelines. Focus on: visual descriptions for blind users, " +
            "high-contrast recommendations, audio description cues, and avoiding rapid cuts. " +
            "Keep responses under 100 words. Be concise and actionable.";

        /// <summary>
        /// Da li je Azure AI Foundry konfigurisan.
        /// Ako nije — sistem radi normalno bez Azure sloja.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_apiKey) &&
            !string.IsNullOrWhiteSpace(_endpoint);

        // ── Konstruktor ───────────────────────────────────────────────────────
        public FoundryIQClient(
            string endpoint       = null,
            string apiKey         = null,
            string deploymentName = "gpt-4o-mini")
        {
            _endpoint       = (endpoint       ?? "").TrimEnd('/');
            _apiKey         = apiKey          ?? "";
            _deploymentName = deploymentName  ?? "gpt-4o-mini";

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            if (!string.IsNullOrWhiteSpace(_apiKey))
                _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetAccessibilityHintAsync
        //
        //  Pita Azure AI za accessibility hint za dati stih.
        //  Vraća null ako Azure nije dostupan — sistem nastavlja normalno.
        // ══════════════════════════════════════════════════════════════════════
        public async Task<string> GetAccessibilityHintAsync(
            string lyric,
            SentimentPolarity sentiment,
            LyricTagType tagType,
            AgeGroup ageGroup = AgeGroup.Kids,
            CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            if (!_available &&
                (DateTime.Now - _lastFailure).TotalMinutes < RETRY_AFTER_MINUTES)
                return null;

            string cacheKey = $"{lyric?.ToLower()}|{sentiment}|{tagType}";
            if (_hintCache.TryGetValue(cacheKey, out string cached))
                return cached;

            try
            {
                string userMessage = BuildUserMessage(lyric, sentiment, tagType, ageGroup);
                string hint = await CallAzureAsync(userMessage, ct);

                if (!string.IsNullOrWhiteSpace(hint))
                {
                    _available = true;
                    _hintCache[cacheKey] = hint;
                    return hint;
                }

                return null;
            }
            catch (Exception ex)
            {
                _available   = false;
                _lastFailure = DateTime.Now;
                System.Diagnostics.Debug.WriteLine(
                    $"[AzureFoundry] Nije dostupan: {ex.Message} — nastavljam bez Azure sloja.");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetSceneDescriptionAsync
        //
        //  Generise audio opis scene za screen reader.
        //  Napaja "Govorna najava za slijepe korisnike" feature.
        // ══════════════════════════════════════════════════════════════════════
        public async Task<string> GetSceneDescriptionAsync(
            string videoQuery,
            string lyric,
            CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            if (!_available &&
                (DateTime.Now - _lastFailure).TotalMinutes < RETRY_AFTER_MINUTES)
                return null;

            string cacheKey = $"desc|{videoQuery}";
            if (_hintCache.TryGetValue(cacheKey, out string cached))
                return cached;

            try
            {
                string message =
                    $"Generate a short audio description (1-2 sentences, max 40 words) " +
                    $"for a video clip matching: \"{videoQuery}\". " +
                    $"This will be read aloud by JAWS or NVDA to a blind user. " +
                    $"Describe motion, light, mood. Related lyric: \"{lyric}\".";

                string description = await CallAzureAsync(message, ct);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    _hintCache[cacheKey] = description;
                    return description;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // ── BuildUserMessage ──────────────────────────────────────────────────
        private string BuildUserMessage(
            string lyric,
            SentimentPolarity sentiment,
            LyricTagType tagType,
            AgeGroup ageGroup)
        {
            string sentimentStr = sentiment switch
            {
                SentimentPolarity.Positive => "positive/joyful",
                SentimentPolarity.Negative => "melancholic/sad",
                _                          => "neutral"
            };

            string tagStr = tagType switch
            {
                LyricTagType.Action      => "physical action/movement",
                LyricTagType.Atmospheric => "mood/atmosphere",
                LyricTagType.Object      => "concrete object/nature",
                _                        => "narrative scene"
            };

            string ageStr = ageGroup switch
            {
                AgeGroup.Toddler => "toddlers (0-3)",
                AgeGroup.Kids    => "children (3-7)",
                AgeGroup.Tween   => "tweens (7-12)",
                _                => "adults"
            };

            return $"Provide accessibility hints for a video scene: " +
                   $"lyric type={tagStr}, sentiment={sentimentStr}, audience={ageStr}. " +
                   $"Lyric: \"{lyric}\". " +
                   $"What visual accessibility considerations should be applied for blind/low-vision users?";
        }

        // ── CallAzureAsync — HTTP call ka Azure AI Foundry ────────────────────
        private async Task<string> CallAzureAsync(string userMessage, CancellationToken ct)
        {
            // Azure AI Foundry chat completions endpoint
            string url = $"{_endpoint}/models/chat/completions?api-version=2024-05-01-preview";

            var requestBody = new
            {
                model    = _deploymentName,
                messages = new[]
                {
                    new { role = "system", content = SYSTEM_PROMPT },
                    new { role = "user",   content = userMessage }
                },
                max_tokens  = 150,
                temperature = 0.3
            };

            string bodyJson = JsonConvert.SerializeObject(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Azure HTTP {(int)response.StatusCode}: {err}");
            }

            string responseJson = await response.Content.ReadAsStringAsync();
            return ParseChatResponse(responseJson);
        }

        // ── ParseChatResponse ─────────────────────────────────────────────────
        private string ParseChatResponse(string json)
        {
            try
            {
                dynamic parsed = JsonConvert.DeserializeObject(json);
                string content = parsed?.choices?[0]?.message?.content?.ToString();

                if (string.IsNullOrWhiteSpace(content)) return null;

                // Skrati na 300 karaktera
                return content.Length > 300 ? content.Substring(0, 297) + "..." : content.Trim();
            }
            catch
            {
                return null;
            }
        }

        // ── IsAvailable — brz health check ───────────────────────────────────
        public async Task<bool> IsAvailable()
        {
            if (!IsConfigured) return false;
            try
            {
                string url = $"{_endpoint}/models?api-version=2024-05-01-preview";
                var response = await _checkClient.GetAsync(url);
                return response.IsSuccessStatusCode ||
                       (int)response.StatusCode == 401;
            }
            catch
            {
                return false;
            }
        }
    }

    // ── FoundryIQConfig ───────────────────────────────────────────────────────
    public class FoundryIQConfig
    {
        public string Endpoint       { get; set; } = "";
        public string ApiKey         { get; set; } = "";
        public string DeploymentName { get; set; } = "gpt-4o-mini";
        public bool   Enabled        { get; set; } = false;

        public bool IsValid =>
            Enabled &&
            !string.IsNullOrWhiteSpace(Endpoint) &&
            !string.IsNullOrWhiteSpace(ApiKey);
    }
}
