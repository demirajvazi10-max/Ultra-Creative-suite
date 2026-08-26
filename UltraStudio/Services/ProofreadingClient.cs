using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Lektor(isanje) preko lokalnog Ollama teksta modela — sa streaming podrškom
    /// i automatskim deljenjem dugih tekstova po odlomcima da bi izbegli timeout.
    /// Dugi dokumenti se dele na odlomke od max ~800 reči i obrađuju sekvencijalno,
    /// uz progress callback koji dijalog može da prikaže korisniku.
    /// </summary>
    public class ProofreadingClient
    {
        private const string OLLAMA_URL = "http://localhost:11434/api/chat";

        // Bez hard timeout-a — koristimo CancellationToken za odustajanje,
        // a HttpCompletionOption.ResponseHeadersRead za streaming (ne čeka ceo body).
        private static readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly HttpClient _pingClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        // Oko 800 reči po odlomku — 14b model komforno obradi za ~30s
        private const int MAX_WORDS_PER_CHUNK = 800;

        public async Task<bool> IsRunningAsync()
        {
            try
            {
                var r = await _pingClient.GetAsync("http://localhost:11434/api/tags");
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>
        /// Lekturiše tekst, uz opcioni progress callback:
        ///   onProgress(chunkIndex, totalChunks, statusText) — poziva se sa UI threada (Dispatcher).
        /// Za kratke tekstove (≤ MAX_WORDS_PER_CHUNK reči) obradi u jednom pozivu.
        /// Za duže tekstove automatski deli po odlomcima i spaja rezultate.
        /// </summary>
        public async Task<ProofreadingResult> ProofreadAsync(
            string text,
            Action<int, int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (!await IsRunningAsync())
                throw new Exception(Lang.T("ollama_not_running"));

            if (string.IsNullOrWhiteSpace(text))
                return new ProofreadingResult();

            var chunks = SplitIntoChunks(text, MAX_WORDS_PER_CHUNK);
            int total = chunks.Count;

            var allIssues = new List<ProofreadingIssue>();
            var rewrittenParts = new List<string>();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                string statusText = total == 1
                    ? Lang.T("proof_running")
                    : string.Format(Lang.T("proof_running_chunk"), i + 1, total);

                onProgress?.Invoke(i, total, statusText);
                DebugLog.Write($"ProofreadingClient: obrada odlomka {i + 1}/{total} ({chunks[i].Split(' ').Length} reči)...");

                var chunkResult = await ProofreadChunkAsync(chunks[i], ct);
                allIssues.AddRange(chunkResult.Issues);
                rewrittenParts.Add(chunkResult.Rewritten);
            }

            onProgress?.Invoke(total, total, Lang.T("proof_finalizing"));

            return new ProofreadingResult
            {
                Issues = allIssues,
                Rewritten = string.Join("\n\n", rewrittenParts)
            };
        }

        private async Task<ProofreadingResult> ProofreadChunkAsync(string chunkText, CancellationToken ct)
        {
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
                "TEXT:\n" + chunkText;

            var payload = new
            {
                model = OllamaModelConfig.TextModel,
                messages = new[] { new { role = "user", content = prompt } },
                stream = true  // streaming — ne čeka ceo body pre nego što dobije odgovor
            };

            var json = JsonSerializer.Serialize(payload);
            var requestContent = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, OLLAMA_URL) { Content = requestContent };
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync(ct);
                throw new Exception($"Ollama API error {(int)response.StatusCode}: {err}");
            }

            // Čitanje streaming odgovora — svaka linija je JSON objekat sa "done" poljem
            var rawBuilder = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var lineDoc = JsonDocument.Parse(line);
                    var root = lineDoc.RootElement;

                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var contentProp))
                    {
                        rawBuilder.Append(contentProp.GetString());
                    }

                    if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                        break;
                }
                catch { /* Preskočiti neispravne linije */ }
            }

            return ParseResult(rawBuilder.ToString(), chunkText);
        }

        private static List<string> SplitIntoChunks(string text, int maxWords)
        {
            // Deli po praznim redovima (odlomcima), grupiše dok ne premaši maxWords
            string[] paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

            var chunks = new List<string>();
            var current = new StringBuilder();
            int currentWords = 0;

            foreach (var para in paragraphs)
            {
                int paraWords = para.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

                // Pojedinačni odlomak duži od maxWords? Podeli po rečenicama.
                if (paraWords > maxWords)
                {
                    if (current.Length > 0)
                    {
                        chunks.Add(current.ToString().Trim());
                        current.Clear();
                        currentWords = 0;
                    }
                    chunks.AddRange(SplitBySentences(para, maxWords));
                    continue;
                }

                if (currentWords + paraWords > maxWords && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                    currentWords = 0;
                }

                current.AppendLine(para);
                current.AppendLine();
                currentWords += paraWords;
            }

            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());

            return chunks.Count > 0 ? chunks : new List<string> { text };
        }

        private static List<string> SplitBySentences(string text, int maxWords)
        {
            // Deli po tački/uzvičniku/upitniku + razmaku ili kraju reda
            var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?])\s+");
            var chunks = new List<string>();
            var current = new StringBuilder();
            int currentWords = 0;

            foreach (var sentence in sentences)
            {
                int sentWords = sentence.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (currentWords + sentWords > maxWords && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                    currentWords = 0;
                }
                current.Append(sentence).Append(' ');
                currentWords += sentWords;
            }

            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());

            return chunks.Count > 0 ? chunks : new List<string> { text };
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
