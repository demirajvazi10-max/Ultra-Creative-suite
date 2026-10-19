using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraVideoEditor
{
    /// <summary>
    /// Text helpers for Serbian lyrics (Latin and Cyrillic).
    ///
    /// Why this exists: matching lyric words with plain string.Contains() caused two
    /// classes of bugs in the AI video creator:
    ///   1. Diacritics: "šuma", "kiša", "čizme" never matched the stems "suma", "kisa",
    ///      "cizme", so the right ambient sound / query was silently skipped.
    ///   2. Substring false positives: "led" matched "gledaj", "pas" matched "opasno",
    ///      "grad" matched "nagrada", "cat" matched "location", "rain" matched "train"...
    ///      which put a blizzard, a dog or city traffic under lines that had nothing to
    ///      do with them.
    ///
    /// Normalize() lowercases, transliterates Cyrillic to Latin and strips diacritics
    /// (š→s, č/ć→c, ž→z, đ→dj), and turns punctuation into spaces. HasStem() then
    /// matches only at the start of a word.
    /// </summary>
    public static class SerbianText
    {
        private static readonly Dictionary<char, string> Map = new Dictionary<char, string>
        {
            // Latin diacritics
            ['š'] = "s", ['č'] = "c", ['ć'] = "c", ['ž'] = "z", ['đ'] = "dj",
            // Serbian Cyrillic
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['ђ'] = "dj",
            ['е'] = "e", ['ж'] = "z", ['з'] = "z", ['и'] = "i", ['ј'] = "j", ['к'] = "k",
            ['л'] = "l", ['љ'] = "lj", ['м'] = "m", ['н'] = "n", ['њ'] = "nj", ['о'] = "o",
            ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['ћ'] = "c", ['у'] = "u",
            ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "c", ['џ'] = "dz", ['ш'] = "s",
        };

        /// <summary>
        /// Lowercase, Cyrillic→Latin, no diacritics, punctuation→space, single spaces.
        /// "Kad je ZIMA, nosi čizme!" → "kad je zima nosi cizme"
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (char raw in text.ToLowerInvariant())
            {
                if (Map.TryGetValue(raw, out string rep)) { sb.Append(rep); continue; }
                if (raw >= 'a' && raw <= 'z' || raw >= '0' && raw <= '9') { sb.Append(raw); continue; }
                // Other accented Latin letters (é, ü...) → strip the accent
                string decomposed = raw.ToString().Normalize(NormalizationForm.FormD);
                char baseChar = decomposed[0];
                if (baseChar >= 'a' && baseChar <= 'z') { sb.Append(baseChar); continue; }
                sb.Append(' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// <summary>Normalized single word (no spaces at all).</summary>
        public static string NormalizeWord(string word) => Normalize(word).Replace(" ", "");

        /// <summary>
        /// True if <paramref name="normalizedText"/> (already passed through Normalize)
        /// contains a word that STARTS with <paramref name="stem"/>.
        /// A stem prefixed with '=' must match the WHOLE word ("=led" matches "led" but not "leden").
        /// A stem containing a space is matched as a phrase starting at a word boundary.
        /// </summary>
        public static bool HasStem(string normalizedText, string stem)
        {
            if (string.IsNullOrEmpty(normalizedText) || string.IsNullOrEmpty(stem)) return false;
            bool exact = stem[0] == '=';
            string s = exact ? stem.Substring(1) : stem;
            if (s.Length == 0) return false;

            int idx = 0;
            while ((idx = normalizedText.IndexOf(s, idx, StringComparison.Ordinal)) >= 0)
            {
                bool startOk = idx == 0 || normalizedText[idx - 1] == ' ';
                int end = idx + s.Length;
                bool endOk = !exact || end == normalizedText.Length || normalizedText[end] == ' ';
                if (startOk && endOk) return true;
                idx++;
            }
            return false;
        }

        /// <summary>True if any of the stems matches (see HasStem).</summary>
        public static bool HasAny(string normalizedText, params string[] stems)
            => stems.Any(s => HasStem(normalizedText, s));

        /// <summary>Levenshtein-based similarity 0..1 between two normalized words.</summary>
        public static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            if (a == b) return 1;
            int n = a.Length, m = b.Length;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return 1.0 - (double)prev[m] / Math.Max(n, m);
        }
    }
}
