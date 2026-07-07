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
        public string Explanation{ get; set; }  // what the AI understood
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
                return Fail("Enter a command.");
            if (items == null || items.Count == 0)
                return Fail("Timeline is empty. Add clips before using the assistant.");

            // 1 — try local Ollama
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

            // 3 — execute command
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

            // "remove clips below/shorter than X second(s)"
            var mShort = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:ispod|kra[cć]|manji|manje)\s+(?:od\s+)?([\d,\.]+)\s*(?:sek|s\b)");
            if (mShort.Success)
            {
                cmd.Action      = "remove_short";
                cmd.Param       = mShort.Groups[1].Value.Replace(',', '.');
                cmd.Explanation = $"Remove clips shorter than {cmd.Param}s";
                return cmd;
            }

            // "remove clips longer than X seconds"
            var mLong = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:duž|du[zž]|ve[cć]|vi[sš]e)\s+(?:od\s+)?([\d,\.]+)\s*(?:sek|s\b)");
            if (mLong.Success)
            {
                cmd.Action      = "remove_long";
                cmd.Param       = mLong.Groups[1].Value.Replace(',', '.');
                cmd.Explanation = $"Remove clips longer than {cmd.Param}s";
                return cmd;
            }

            // "keep only with faces / face"
            if (text.Contains("lic") || text.Contains("face") || text.Contains("osob"))
            { cmd.Action = "keep_faces"; cmd.Explanation = "Keep only clips with faces"; return cmd; }

            // "keep only exterior / outdoors"
            if (text.Contains("exterij") || text.Contains("exterij") || text.Contains("otvor") || text.Contains("outdoor") || text.Contains("spolj"))
            { cmd.Action = "keep_outdoor"; cmd.Explanation = "Keep only exterior clips"; return cmd; }

            // "keep only interior / indoors"
            if (text.Contains("interij") || text.Contains("unutra") || text.Contains("indoor"))
            { cmd.Action = "keep_indoor"; cmd.Explanation = "Keep only interior clips"; return cmd; }

            // "sortiraj po skoru / oceni"
            if ((text.Contains("sortiraj") || text.Contains("sort")) &&
                (text.Contains("skor") || text.Contains("ocen") || text.Contains("score")))
            { cmd.Action = "sort_by_score"; cmd.Explanation = "Sort by AI score descending"; return cmd; }

            // "sortiraj po trajanju"
            if ((text.Contains("sortiraj") || text.Contains("sort")) && text.Contains("trajanj"))
            { cmd.Action = "sort_by_duration"; cmd.Explanation = "Sort by duration ascending"; return cmd; }

            // "vrati original / reset / originalni redosled"
            if (text.Contains("original") || text.Contains("reset") || text.Contains("vrati"))
            { cmd.Action = "sort_original"; cmd.Explanation = "Restore original order"; return cmd; }

            // "keep first N / top N"
            var mTop = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:prvih|top|zadr[zž]i)\s+(\d+)");
            if (mTop.Success)
            {
                cmd.Action      = "keep_top_n";
                cmd.Param       = mTop.Groups[1].Value;
                cmd.Explanation = $"Keep first {cmd.Param} clips";
                return cmd;
            }

            // "remove static / without motion"
            if (text.Contains("stati") || text.Contains("bez pokreta") || text.Contains("mirn"))
            { cmd.Action = "remove_static"; cmd.Explanation = "Remove static clips"; return cmd; }

            // "keep motion / with motion"
            if (text.Contains("pokret") || text.Contains("motion") || text.Contains("dinami"))
            { cmd.Action = "keep_motion"; cmd.Explanation = "Keep only clips with motion"; return cmd; }

            cmd.Action      = "unknown";
            cmd.Explanation = "Command not recognized.";
            return cmd;
        }

        // ── Command execution ───────────────────────────────────────

        private static AssistantResult ExecuteCommand(AssistantCommand cmd, List<TimelineItem> items)
        {
            var result = new AssistantResult
            {
                Command       = cmd,
                OriginalCount = items.Count,
            };

            if (cmd == null || cmd.Action == "unknown")
            {
                result.Error = "Command not recognized. Try:\n" +
                               "• \"remove clips shorter than 2 seconds\"\n" +
                               "• \"keep only clips with faces\"\n" +
                               "• \"sortiraj po skoru\"\n" +
                               "• \"keep first 10\"\n" +
                               "• \"remove static clips\"";
                return result;
            }

            List<TimelineItem> updated;

            switch (cmd.Action)
            {
                case "remove_short":
                    double minS = TryParseDouble(cmd.Param, 2.0);
                    updated = items.Where(i => i.Duration >= minS).ToList();
                    result.Summary = $"Removed {items.Count - updated.Count} clips shorter than {minS}s.";
                    break;

                case "remove_long":
                    double maxS = TryParseDouble(cmd.Param, 30.0);
                    updated = items.Where(i => i.Duration <= maxS).ToList();
                    result.Summary = $"Removed {items.Count - updated.Count} clips longer than {maxS}s.";
                    break;

                case "keep_faces":
                    // AccessibilityDescription or ContentDescription contains info about faces
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("lic") ||
                        (i.ContentTag ?? "").ToLower().Contains("face") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("face") ||
                        (i.ContentTag ?? "").ToLower().Contains("lic")).ToList();
                    if (updated.Count == 0) updated = items; // nema metadata → ostavi sve
                    result.Summary = $"Kept {updated.Count} clips with detected faces.";
                    break;

                case "keep_outdoor":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("eksterijeur") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("eksterijer") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("outdoor") ||
                        (i.ContentTag ?? "").ToLower().Contains("outdoor")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Kept {updated.Count} exterior clips.";
                    break;

                case "keep_indoor":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("interijeur") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("interijer") ||
                        (i.AccessibilityDescription ?? "").ToLower().Contains("indoor") ||
                        (i.ContentTag ?? "").ToLower().Contains("indoor")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Kept {updated.Count} interior clips.";
                    break;

                case "sort_by_score":
                    updated = items
                        .OrderByDescending(i => ExtractScore(i))
                        .ToList();
                    RenumberFixed(updated);
                    result.Summary = "Clips sorted by AI score (descending).";
                    break;

                case "sort_by_duration":
                    updated = items.OrderBy(i => i.Duration).ToList();
                    RenumberFixed(updated);
                    result.Summary = "Clips sorted by duration (ascending).";
                    break;

                case "sort_original":
                    updated = items.OrderBy(i => i.TrackIndex).ThenBy(i => i.Start).ToList();
                    RenumberFixed(updated);
                    result.Summary = "Original order restored.";
                    break;

                case "keep_top_n":
                    int n = TryParseInt(cmd.Param, 10);
                    updated = items
                        .OrderByDescending(i => ExtractScore(i))
                        .Take(n).ToList();
                    result.Summary = $"Kept first {updated.Count} clips by AI score.";
                    break;

                case "remove_static":
                    updated = items.Where(i =>
                        !(i.AccessibilityDescription ?? "").ToLower().Contains("static") &&
                        !(i.ContentTag ?? "").ToLower().Contains("static")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Removed {items.Count - updated.Count} static clips.";
                    break;

                case "keep_motion":
                    updated = items.Where(i =>
                        (i.AccessibilityDescription ?? "").ToLower().Contains("pokret") ||
                        (i.ContentTag ?? "").ToLower().Contains("motion")).ToList();
                    if (updated.Count == 0) updated = items;
                    result.Summary = $"Kept {updated.Count} clips with motion.";
                    break;

                case "trim_silence":
                    updated = items.Where(i => i.Duration >= 0.5).ToList();
                    result.Summary = $"Removed {items.Count - updated.Count} clips that are too short (<0.5s).";
                    break;

                default:
                    result.Error = $"Nepoznata akcija: {cmd.Action}";
                    return result;
            }

            result.UpdatedItems = updated;
            if (string.IsNullOrEmpty(result.Summary))
                result.Summary = $"Executed: {cmd.Explanation}. {result.ResultCount}/{result.OriginalCount} clips.";

            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static double ExtractScore(TimelineItem item)
        {
            // Try parsing from AccessibilityDescription (format "score:7.5")
            var desc = item.AccessibilityDescription ?? item.ContentTag ?? "";
            var m    = System.Text.RegularExpressions.Regex.Match(desc, @"skor[:\s]*([\d\.]+)");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s))
                return s;
            // Fallback: by duration (longer = better)
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
