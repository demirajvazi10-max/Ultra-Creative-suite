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
    public class ProofreadingIssue
    {
        public string Type { get; set; } = "spelling"; // spelling|grammar|style
        public string Original { get; set; } = "";
        public string Suggestion { get; set; } = "";
        public string Explanation { get; set; } = "";
    }

    public class ProofreadingResult
    {
        public List<ProofreadingIssue> Issues { get; set; } = new();
        // Ceo tekst, prepisan i uglađen od strane AI-ja — korisno kad ima
        // previše sitnih izmena da bi ih dizajner prihvatao jednu po jednu.
        public string Rewritten { get; set; } = "";
    }

    /// <summary>
    /// Lekt(orisanje) preko lokalnog Ollama teksta modela — ISTI obrazac
    /// (localhost:11434, bez cloud-a) kao OllamaVisionClient, samo tekstualni
    /// model umesto vision modela. Namenjen srpskom jeziku (ćirilica i
    /// latinica), ali radi sa bilo kojim tekstom koji model razume.
    /// </summary>
    public class ProofreadingClient
    {
        private const string OLLAMA_URL = "http://localhost:11434/api/chat";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
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

        public async Task<ProofreadingResult> ProofreadAsync(string text, CancellationToken ct = default)
        {
            if (!await IsRunningAsync())
                throw new Exception(Lang.T("ollama_not_running"));

            if (string.IsNullOrWhiteSpace(text))
                return new ProofreadingResult();

            string prompt =
                "You are a professional Serbian-language proofreader/copy editor (lektor). Proofread the text below. " +
                "Check spelling, grammar (cases/padeži, agreement), AND style (repeated words, overly long or " +
                "awkward sentences) — and suggest a better phrasing where it genuinely helps. " +
                "Respond with ONLY a JSON object, nothing else, no markdown fences:\n" +
                "{\"issues\": [{\"type\": \"spelling|grammar|style\", \"original\": \"exact short phrase from the text\", " +
                "\"suggestion\": \"corrected phrase\", \"explanation\": \"short reason, under 15 words, in Serbian\"}], " +
                "\"rewritten\": \"the full text, lightly polished, same length and meaning\"}\n" +
                "Keep \"original\" short and EXACT (a few words, copy-pasted verbatim from the text below) so it can " +
                "be found with a plain text search. If the text is already correct, return an empty issues array and " +
                "\"rewritten\" equal to the original text. Do not invent issues that aren't there.\n\n" +
                "TEXT:\n" + text;

            var payload = new
            {
                model = OllamaModelConfig.TextModel,
                messages = new[] { new { role = "user", content = prompt } },
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(OLLAMA_URL, content, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Ollama API error {(int)response.StatusCode}: {responseJson}");

            using var doc = JsonDocument.Parse(responseJson);
            string raw = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

            return ParseResult(raw, text);
        }

        private static ProofreadingResult ParseResult(string raw, string originalText)
        {
            var result = new ProofreadingResult { Rewritten = originalText };
            string jsonPart = ExtractJson(raw);

            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                var root = doc.RootElement;

                if (root.TryGetProperty("issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in issuesEl.EnumerateArray())
                    {
                        string original = item.TryGetProperty("original", out var o) ? o.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(original)) continue;
                        result.Issues.Add(new ProofreadingIssue
                        {
                            Type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "spelling" : "spelling",
                            Original = original,
                            Suggestion = item.TryGetProperty("suggestion", out var s) ? s.GetString() ?? "" : "",
                            Explanation = item.TryGetProperty("explanation", out var e) ? e.GetString() ?? "" : ""
                        });
                    }
                }

                if (root.TryGetProperty("rewritten", out var rw) && rw.ValueKind == JsonValueKind.String)
                    result.Rewritten = rw.GetString() ?? originalText;
            }
            catch { /* prazna lista + originalni tekst ako parsiranje padne — bolje nego pući */ }

            return result;
        }

        private static string ExtractJson(string raw)
        {
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end < start) return "{}";
            return raw.Substring(start, end - start + 1);
        }
    }
}
