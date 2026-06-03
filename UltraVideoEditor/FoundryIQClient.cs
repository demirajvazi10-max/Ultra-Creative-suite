// ══════════════════════════════════════════════════════════════════════════════
//  FoundryIQClient.cs  —  Microsoft Foundry IQ Integration
//  UltraVideoEditor  |  UltraCreativeSuite
//
//  ULOGA U ARHITEKTURI (Sloj 0 — ispred Ollame):
//
//  SLOJ 0 — Foundry IQ (NOVI — accessibility knowledge base)
//    Knowledge base sadrži: WCAG smjernice, audio deskriptore za slijepe korisnike,
//    video accessibility standarde i best practices za kreativne aplikacije.
//    Za svaki stih, Foundry IQ vraća: accessibility-aware vizuelni kontekst
//    koji se prosleđuje Ollami kao dodatni hint.
//
//  SLOJ 1 — Ollama (GLAVNI) — prima Foundry IQ hint kao kontekst
//  SLOJ 2 — StrictQueryEngine (FALLBACK)
//  SLOJ 3 — SmartFallback (NIKAD NULL)
//
//  Kako koristiti:
//    var fiqClient = new FoundryIQClient(apiKey, knowledgeBaseId);
//    string hint   = await fiqClient.GetAccessibilityHintAsync(lyric, sentiment, tagType);
//    // hint se ubacuje u BuildOllamaPrompt kao AccessibilityContext
//
//  Fallback: ako Foundry IQ nije dostupan (no API key, network error),
//  sistem transparentno pada na Sloj 1 (Ollama) — bez prekida rada.
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    /// <summary>
    /// Foundry IQ Client — Microsoft Azure AI Foundry knowledge retrieval.
    /// Koristi Foundry IQ knowledge base sa WCAG i accessibility smjernicama
    /// da enrichuje video query pipeline za slijepe i slabovide korisnike.
    /// </summary>
    public class FoundryIQClient
    {
        // ── Foundry IQ API endpoint ───────────────────────────────────────────
        // Format: https://{resource}.services.ai.azure.com/agents/v1.0/
        //         knowledgebases/{knowledgeBaseId}/query
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly string _knowledgeBaseId;
        private readonly HttpClient _httpClient;

        // Statički check client — brz health check bez socket exhaustion
        private static readonly HttpClient _checkClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // Cache za ponovljene upite — isti stih+sentiment ne šalje se dva puta
        private readonly Dictionary<string, string> _hintCache
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Status dostupnosti ────────────────────────────────────────────────
        private bool _available = true;
        private DateTime _lastFailure = DateTime.MinValue;
        private const int RETRY_AFTER_MINUTES = 5;

        /// <summary>
        /// Da li je Foundry IQ konfigurisan (API key i endpoint postoje).
        /// Ako nije — sistem radi normalno bez Foundry IQ sloja.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_apiKey) &&
            !string.IsNullOrWhiteSpace(_endpoint) &&
            !string.IsNullOrWhiteSpace(_knowledgeBaseId);

        // ── Konstruktor ───────────────────────────────────────────────────────
        public FoundryIQClient(string endpoint = null, string apiKey = null, string knowledgeBaseId = null)
        {
            _endpoint        = endpoint        ?? "";
            _apiKey          = apiKey          ?? "";
            _knowledgeBaseId = knowledgeBaseId ?? "";

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(15);

            if (!string.IsNullOrWhiteSpace(_apiKey))
                _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetAccessibilityHintAsync
        //
        //  Pita Foundry IQ knowledge base za accessibility-aware vizuelni hint.
        //  Knowledge base sadrži:
        //    - WCAG 2.2 smjernice za video sadržaj
        //    - Audio deskriptore za slijepe korisnike (scene descriptions)
        //    - Best practices za screen-reader-friendly kreativne aplikacije
        //    - Primjeri dostupnih vizuelnih metafora po emocijama
        //
        //  Vraća: kratki hint string koji se ubacuje u Ollama prompt
        //  Primjer: "Use high-contrast visuals; avoid rapid cuts; describe
        //            motion explicitly for audio description layer."
        //
        //  Ako Foundry IQ nije dostupan → vraća null (sistem nastavlja normalno)
        // ══════════════════════════════════════════════════════════════════════
        public async Task<string> GetAccessibilityHintAsync(
            string lyric,
            SentimentPolarity sentiment,
            LyricTagType tagType,
            AgeGroup ageGroup = AgeGroup.Kids,
            CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            // Provjeri da li smo u retry periodu
            if (!_available &&
                (DateTime.Now - _lastFailure).TotalMinutes < RETRY_AFTER_MINUTES)
                return null;

            // Cache lookup
            string cacheKey = $"{lyric?.ToLower()}|{sentiment}|{tagType}";
            if (_hintCache.TryGetValue(cacheKey, out string cached))
                return cached;

            try
            {
                string query = BuildFoundryQuery(lyric, sentiment, tagType, ageGroup);
                string hint  = await QueryKnowledgeBaseAsync(query, ct);

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
                    $"[FoundryIQ] Nije dostupan: {ex.Message} — nastavljam bez Foundry IQ sloja.");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetSceneDescriptionAsync
        //
        //  Za audio deskriptiore — vraća opis scene koji screen reader izgovara
        //  slijepim korisnicima dok se video reproducira.
        //
        //  Primjer ulaza:  "child running meadow joyful spring"
        //  Primjer izlaza: "A child runs through a flower-filled meadow on a
        //                   bright spring day, arms outstretched, smiling."
        //
        //  Ovo direktno napaja "Govorna najava za slijepe korisnike" feature
        //  koji već postoji u aplikaciji (AutomationProperties.Name).
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
                string query = $"Generate a short, vivid audio description (1-2 sentences) " +
                               $"for a video clip matching: \"{videoQuery}\". " +
                               $"This description will be read aloud by a screen reader to a blind user. " +
                               $"Focus on motion, light, mood, and key visual elements. " +
                               $"Related lyric: \"{lyric}\".";

                string description = await QueryKnowledgeBaseAsync(query, ct);

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

        // ══════════════════════════════════════════════════════════════════════
        //  BuildFoundryQuery — gradi query za Foundry IQ knowledge base
        // ══════════════════════════════════════════════════════════════════════
        private string BuildFoundryQuery(
            string lyric,
            SentimentPolarity sentiment,
            LyricTagType tagType,
            AgeGroup ageGroup)
        {
            string sentimentStr = sentiment switch
            {
                SentimentPolarity.Positive => "positive, joyful",
                SentimentPolarity.Negative => "melancholic, sad",
                _                          => "neutral"
            };

            string tagStr = tagType switch
            {
                LyricTagType.Action      => "physical action/movement",
                LyricTagType.Atmospheric => "mood/atmosphere/feeling",
                LyricTagType.Object      => "concrete object/nature element",
                _                        => "narrative/story scene"
            };

            string ageStr = ageGroup switch
            {
                AgeGroup.Toddler => "toddlers (0-3 years)",
                AgeGroup.Kids    => "children (3-7 years)",
                AgeGroup.Tween   => "tweens (7-12 years)",
                _                => "adult audience"
            };

            return $"WCAG accessibility guidelines and best practices for a video scene: " +
                   $"lyric type is {tagStr}, sentiment is {sentimentStr}, audience is {ageStr}. " +
                   $"Lyric: \"{lyric}\". " +
                   $"What visual accessibility considerations and audio description hints " +
                   $"should be applied to make this scene accessible for blind and low-vision users?";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  QueryKnowledgeBaseAsync — HTTP call ka Foundry IQ
        // ══════════════════════════════════════════════════════════════════════
        private async Task<string> QueryKnowledgeBaseAsync(string query, CancellationToken ct)
        {
            // Foundry IQ Agentic Retrieval API format
            // Dokumentacija: https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-foundry-iq
            var requestBody = new
            {
                query       = query,
                top         = 3,        // Broj dokumenata za retrieval
                queryType   = "semantic" // Semantic search (ne keyword)
            };

            string url      = $"{_endpoint.TrimEnd('/')}/knowledgebases/{_knowledgeBaseId}/query";
            string bodyJson = JsonConvert.SerializeObject(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Foundry IQ HTTP {(int)response.StatusCode}: {errorBody}");
            }

            string responseJson = await response.Content.ReadAsStringAsync();
            return ParseFoundryResponse(responseJson);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ParseFoundryResponse — parsira Foundry IQ odgovor
        //  Format odgovora: { "results": [{ "content": "...", "score": 0.9 }] }
        // ══════════════════════════════════════════════════════════════════════
        private string ParseFoundryResponse(string json)
        {
            try
            {
                dynamic parsed = JsonConvert.DeserializeObject(json);

                // Uzmi top result sa najvišim score-om
                var results = parsed?.results;
                if (results == null || results.Count == 0)
                    return null;

                // Spoji top 3 dokumenta u jedan hint string (max 300 char)
                var parts = new List<string>();
                foreach (var result in results)
                {
                    string content = result?.content?.ToString();
                    if (!string.IsNullOrWhiteSpace(content))
                        parts.Add(content.Trim());
                    if (parts.Count >= 3) break;
                }

                if (parts.Count == 0) return null;

                string combined = string.Join(" | ", parts);
                // Skrati na 300 karaktera da ne zagušuje Ollama prompt
                return combined.Length > 300 ? combined.Substring(0, 297) + "..." : combined;
            }
            catch
            {
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  IsFoundryIQAvailable — brz health check
        // ══════════════════════════════════════════════════════════════════════
        public async Task<bool> IsFoundryIQAvailable()
        {
            if (!IsConfigured) return false;
            try
            {
                string healthUrl = $"{_endpoint.TrimEnd('/')}/knowledgebases";
                var response = await _checkClient.GetAsync(healthUrl);
                return response.IsSuccessStatusCode ||
                       (int)response.StatusCode == 401; // 401 = endpoint postoji, auth needed
            }
            catch
            {
                return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  FoundryIQConfig — podešavanja za Foundry IQ integraciju
    //  Čuva se zajedno sa ostalim API ključevima u ApiKeyDialog
    // ══════════════════════════════════════════════════════════════════════════
    public class FoundryIQConfig
    {
        /// <summary>Azure AI Foundry resource endpoint URL</summary>
        public string Endpoint        { get; set; } = "";

        /// <summary>API key za Foundry IQ resource</summary>
        public string ApiKey          { get; set; } = "";

        /// <summary>Knowledge Base ID iz Microsoft Foundry portala</summary>
        public string KnowledgeBaseId { get; set; } = "";

        /// <summary>Da li je Foundry IQ integracija aktivna</summary>
        public bool   Enabled         { get; set; } = false;

        public bool IsValid =>
            Enabled &&
            !string.IsNullOrWhiteSpace(Endpoint) &&
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(KnowledgeBaseId);
    }
}
