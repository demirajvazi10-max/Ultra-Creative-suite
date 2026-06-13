using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // TIMELINE AI ASSISTANT — Faza 4B
    // Tekstualne AI komande nad timeline-om putem Ollama.
    // ═══════════════════════════════════════════════════════════════

    public class AssistantCommand
    {
        public string Raw        { get; set; }  // originalni tekst korisnika
        public string Action     { get; set; }  // npr. "remove_short", "sort_by_score", "keep_faces"
        public string Param      { get; set; }  // parametar (npr. "2" za min sekunde)
        public string Explanation{ get; set; }  // šta je AI razumeo
    }

    public class AssistantResult
    {
        public List<TimelineItem> UpdatedItems  { get; set; } = new();
        public AssistantCommand   Command       { get; set; }
        public string             Summary       { get; set; } = "";
        public string             Error         { get; set; }
        public bool               Success       => string.IsNullOrEmpty(Error);
        public int                OriginalCount { get; set; }
        public int                ResultCount   => UpdatedItems.Count;
    }

    public static class TimelineAIAssistant
    {
        // ── Javni API ────────────────────────────────────────────────

        public static async Task<AssistantResult> ExecuteCommandAsync(
            string userText,
            List<TimelineItem> items,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Fail("Unesite komandu.");
            if (items == null || items.Count == 0)
                return Fail("Timeline je prazan. Dodajte klipoive pre korišćenja asistenta.");

            // 1 — pokušaj lokalni Ollama
            AssistantCommand cmd = null;
            try
            {
                var ollama = new OllamaClient();
                bool alive = await ollama.IsOllamaRunning();
                if (alive)
                {
                    string prompt = BuildParsePrompt(userText, items);
                    string json   = await ollama.GenerateAsync(prompt, ct: ct);
                    cmd = ParseCommandJson(json);
                }
            }
            catch { /* Ollama nije dostupna */ }

            // 2 — ako nema Ollame, rule-based parsing
            cmd ??= RuleBasedParse(userText);

            // 3 — izvrši komandu
            return ExecuteCommand(cmd, items);
        }

        // ── Ollama prompt ────────────────────────────────────────────

        private static string BuildParsePrompt(string userText, List<TimelineItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a video editing assistant. Parse the user's command and return ONLY valid JSON.");
            sb.AppendLine("Available actions:");
            sb.AppendLine("  remove_short        - param: min seconds (float)");
            sb.AppendLine("  remove_long         - param: max seconds (float)");
            sb.AppendLine("  keep_faces          - keep only clips with faces (param: ignored)");
            sb.AppendLine("  keep_outdoor        - keep only outdoor clips");
            sb.AppendLine("  keep_indoor         - keep only indoor clips");
            sb.AppendLine("  sort_by_score       - sort by vision score descending");
            sb.AppendLine("  sort_by_duration    - sort by duration ascending");
            sb.AppendLine("  sort_original       - restore original order");
            sb.AppendLine("  keep_top_n          - param: number of clips to keep (int)");
            sb.AppendLine("  remove_static       - remove static/motionless clips");
            sb.AppendLine("  keep_motion         - keep only clips with strong motion");
            sb.AppendLine("  trim_silence        - remove very short clips under 0.5s");
            sb.AppendLine("  unknown             - if command is unclear");
            sb.AppendLine();
            sb.AppendLine($"Timeline has {items.Count} clips.");
            sb.AppendLine($"User command: \"{userText}\"");
            sb.AppendLine();
            sb.AppendLine("Respond ONLY with JSON, no markdown, no explanation:");
            sb.AppendLine("{\"action\":\"...\",\"param\":\"...\",\"explanation\":\"...\"}");
            return sb.ToString();
        }

        private static AssistantCommand ParseCommandJson(string json)
        {
            try
            {
                json = json.Trim();
                // strip markdown fences
                if (json.StartsWith("```")) json = json.Split('\n', 2).Last().TrimEnd('`').Trim();
                var doc = JsonDocument.Parse(json);
                return new AssistantCommand
                {
                    Action      = doc.RootElement.GetProperty("action").GetString() ?? "unknown",
                    Param       = doc.RootElement.TryGetProperty("param", out var p) ? p.GetString() ?? "" : "",
                    Explanation = doc.RootElement.TryGetProperty("explanation", out var ex) ? ex.GetString() ?? "" : "",
                };
            }
            catch { return null; }
        }

        // ── Rule-based fallback ───────────────────────────────────────

        private static AssistantCommand RuleBasedParse(string text)
        {
            text = text.ToLowerInvariant().Trim();
            var cmd = new AssistantCommand { Raw = text };

            // "ukloni klipoive ispod/kraće od X sekund(i/e)"
            var mShort = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:ispod|kra[cć]|manji|manje)\s+(?:od\s+)?([\d,\.]+)\s*(?:sek|s\b)");
            if (mShort.Success)
            {
                cmd.Action      = "remove_short";
                cmd.Param       = mShort.Groups[1].Value.Replace(',', '.');
                cmd.Explanation = $"Ukloni klipoive kraće od {cmd.Param}s";
                return cmd;
            }

            // "ukloni klipoive duže od X sekundi"
            var mLong = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:duž|du[zž]|ve[cć]|vi[sš]e)\s+(?:od\s+)?([\d,\.]+)\s*(?:sek|s\b)");
            if (mLong.Success)
            {
                cmd.Action      = "remove_long";
                cmd.Param       = mLong.Groups[1].Value.Replace(',', '.');
                cmd.Explanation = $"Ukloni klipoive duže od {cmd.Param}s";
                return cmd;
            }

            // "zadrži samo sa licima / lice"
            if (text.Contains("lic") || text.Contains("face") || text.Contains("osob"))
            { cmd.Action = "keep_faces"; cmd.Explanation = "Zadrži samo klipoive sa licima"; return cmd; }

            // "zadrži samo eksterijer / na otvorenom"
            if (text.Contains("exterij") || text.Contains("exterij") || text.Contains("otvor") || text.Contains("outdoor") || text.Contains("spolj"))
            { cmd.Action = "keep_outdoor"; cmd.Explanation = "Zadrži samo klipoive u eksterijeru"; return cmd; }

            // "zadrži samo interior / unutra"
            if (text.Contains("interij") || text.Contains("unutra") || text.Contains("indoor"))
            { cmd.Action = "keep_indoor"; cmd.Explanation = "Zadrži samo klipoive u interijeru"; return cmd; }

            // "sortiraj po skoru / oceni"
            if ((text.Contains("sortiraj") || text.Contains("sort")) &&
                (text.Contains("skor") || text.Contains("ocen") || text.Contains("score")))
            { cmd.Action = "sort_by_score"; cmd.Explanation = "Sortiraj po AI skoru opadajuće"; return cmd; }

            // "sortiraj po trajanju"
            if ((text.Contains("sortiraj") || text.Contains("sort")) && text.Contains("trajanj"))
            { cmd.Action = "sort_by_duration"; cmd.Explanation = "Sortiraj po trajanju rastuće"; return cmd; }

            // "vrati original / reset / originalni redosled"
            if (text.Contains("original") || text.Contains("reset") || text.Contains("vrati"))
            { cmd.Action = "sort_original"; cmd.Explanation = "Vrati originalni redosled"; return cmd; }

            // "zadrži prvih N / top N"
            var mTop = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:prvih|top|zadr[zž]i)\s+(\d+)");
            if (mTop.Success)
            {
                cmd.Action      = "keep_top_n";
                cmd.Param       = mTop.Groups[1].Value;
                cmd.Explanation = $"Zadrži prvih {cmd.Param} klipoiva";
                return cmd;
            }

            // "ukloni statične / bez pokreta"
            if (text.Contains("stati") || text.Contains("bez pokreta") || text.Contains("mirn"))
            { cmd.Action = "remove_static"; cmd.Explanation = "Ukloni statične klipoive"; return cmd; }

            // "zadrži pokret / sa pokretom"
            if (text.Contains("pokret") || text.Contains("motion") || text.Contains("dinami"))
            { cmd.Action = "keep_motion"; cmd.Explanation = "Zadrži samo klipoive sa pokretom"; return cmd; }

            cmd.Action      = "unknown";
            cmd.Explanation = "Komanda nije prepoznata.";
            return cmd;
        }

        // ── Izvršavanje komandi ───────────────────────────────────────

        private static AssistantResult ExecuteCommand(AssistantCommand cmd, List<TimelineItem> items)
        {
            var result = new AssistantResult
            {
                Command       = cmd,
                OriginalCount = items.Count,
            };

            if (cmd == null || cmd.Action == "unknown")
            {
                result.Error = "Komanda nije prepoznata. Pokušajte sa:\n" +
                               "• \"ukloni klipoive kraće od 2 sekunde\"\n" +
                               "• \"zadrži samo klipoive sa licima\"\n" +
                               "• \"sortiraj po skoru\"\n" +
                               "• \"zadrži prvih 10\"\n" +
                               "• \"ukloni statične klipoive\"";
                return result;
            }

            List<TimelineItem> updated;

            switch (cmd.Action)
            {
                case "remove_short":
                    double minS = TryParseDouble(cmd.Param, 2.0);
                    updated = items.Where(i => i.Duration >= minS).ToList();
                    result.Summary = $"Uklonjeno {items.Count - updated.Count} klipoiva kraćih od {minS}s.";
                    break;

                case "remove_long":
                    double maxS = TryParseDouble(cmd.Param, 30.0);
                    updated = items.Where(i => i.Duration <= maxS).ToList();
                    result.Summary = $"Uklonjeno {items.Count - updated.Count} klipoiva dužih od {maxS}s.";
                    break;

                case "keep_faces":
                    // AccessibilityDescription ili ContentDescription sadrži info o licima
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("lic") ||
                        (i.ContentTag ?? "").ToLower().Contains("face") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("face") ||
                        (i.ContentTag ?? "").ToLower().Contains("lic")).ToList();
                    if (updated.Count == 0) updated = items; // nema metadata → ostavi sve
                    result.Summary = $"Zadržano {updated.Count} klipoiva sa detektovanim licima.";
                    break;

                case "keep_outdoor":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("eksterijeur") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("eksterijer") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("outdoor") ||
                        (i.ContentTag ?? "").ToLower().Contains("outdoor")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Zadržano {updated.Count} eksternih klipoiva.";
                    break;

                case "keep_indoor":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("interijeur") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("interijer") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("indoor") ||
                        (i.ContentTag ?? "").ToLower().Contains("indoor")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Zadržano {updated.Count} internih klipoiva.";
                    break;

                case "sort_by_score":
                    updated = items
                        .OrderByDescending(i => ExtractScore(i))
                        .ToList();
                    RenumberFixed(updated);
                    result.Summary = "Klipoivi sortirani po AI skoru (opadajuće).";
                    break;

                case "sort_by_duration":
                    updated = items.OrderBy(i => i.Duration).ToList();
                    RenumberFixed(updated);
                    result.Summary = "Klipoivi sortirani po trajanju (rastuće).";
                    break;

                case "sort_original":
                    updated = items.OrderBy(i => i.TrackIndex).ThenBy(i => i.Start).ToList();
                    RenumberFixed(updated);
                    result.Summary = "Originalni redosled vraćen.";
                    break;

                case "keep_top_n":
                    int n = TryParseInt(cmd.Param, 10);
                    updated = items
                        .OrderByDescending(i => ExtractScore(i))
                        .Take(n).ToList();
                    result.Summary = $"Zadržano prvih {updated.Count} klipoiva po AI skoru.";
                    break;

                case "remove_static":
                    updated = items.Where(i =>
                        !(i.AccessibilityDescription ?? "").ToLower().Contains("statič") &&
                        !(i.ContentTag ?? "").ToLower().Contains("static")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Uklonjeno {items.Count - updated.Count} statičnih klipoiva.";
                    break;

                case "keep_motion":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("pokret") ||
                        (i.ContentTag ?? "").ToLower().Contains("motion")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Zadržano {updated.Count} klipoiva sa pokretom.";
                    break;

                case "trim_silence":
                    updated = items.Where(i => i.Duration >= 0.5).ToList();
                    result.Summary = $"Uklonjeno {items.Count - updated.Count} previše kratkih klipoiva (<0.5s).";
                    break;

                default:
                    result.Error = $"Nepoznata akcija: {cmd.Action}";
                    return result;
            }

            result.UpdatedItems = updated;
            if (string.IsNullOrEmpty(result.Summary))
                result.Summary = $"Izvršeno: {cmd.Explanation}. {result.ResultCount}/{result.OriginalCount} klipoiva.";

            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static double ExtractScore(TimelineItem item)
        {
            // Pokušaj parsiranje iz AccessibilityDescription (format "skor:7.5")
            var desc = item.AccessibilityDescription ?? item.ContentTag ?? "";
            var m    = System.Text.RegularExpressions.Regex.Match(desc, @"skor[:\s]*([\d\.]+)");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s))
                return s;
            // Fallback: po dužini (duži = bolji)
            return item.Duration;
        }

        private static void RenumberFixed(List<TimelineItem> items)
        {
            double cursor = 0;
            foreach (var item in items)
            {
                if (item.UseFixedPosition)
                {
                    item.FixedPosition = cursor;
                    cursor += item.Duration;
                }
            }
        }

        private static double TryParseDouble(string s, double def)
            => double.TryParse(s?.Replace(',', '.'), System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;

        private static int TryParseInt(string s, int def)
            => int.TryParse(s, out int v) ? v : def;

        private static AssistantResult Fail(string error)
            => new AssistantResult { Error = error };
    }
}
