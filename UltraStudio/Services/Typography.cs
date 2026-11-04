using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UltraStudio.Services
{
    /// <summary>
    /// Srpska tipografija i pismo — bez AI-ja, trenutno i predvidivo:
    /// navodnici „ “, crta i crtica, razmaci, tri tačke, nevidljivi razmak
    /// iza jednoslovnih reči (da "i", "u", "s" ne ostanu same na kraju reda),
    /// i preslovljavanje latinica ↔ ćirilica po pravilima srpskog pravopisa.
    /// </summary>
    public static class Typography
    {
        public sealed class Options
        {
            public bool Quotes = true;          // "..." -> „...“, '...' -> ‚...‘, apostrof -> ’
            public bool EnglishQuotes;          // “...” umesto „...“ (za engleski tekst)
            public bool Dashes = true;          // " - " -> " – ", 10-20 -> 10–20, dijalog "- " -> "— "
            public bool Spaces = true;          // dupli razmaci, razmak pre/posle interpunkcije, razmaci na kraju reda
            public bool Ellipsis = true;        // ... -> …
            public bool NonBreaking = true;     // NBSP iza jednoslovnih reči i između broja i jedinice
        }

        public sealed class Report
        {
            public int Quotes, Dashes, Spaces, Ellipsis, NonBreaking;
            public int Total => Quotes + Dashes + Spaces + Ellipsis + NonBreaking;
        }

        private const char Nbsp = ' ';

        public static string Fix(string text, Options o, out Report r)
        {
            r = new Report();
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text.Replace("\r\n", "\n");

            // Delove koji ne smeju da se diraju (linkovi, mejlovi) zamenimo oznakama pa ih vratimo na kraju
            var protectedParts = new List<string>();
            s = Regex.Replace(s, @"(https?://\S+|www\.\S+|[\w.+-]+@[\w-]+\.[\w.-]+)", m =>
            {
                protectedParts.Add(m.Value);
                return "" + (protectedParts.Count - 1) + "";
            });

            if (o.Spaces)
            {
                int n = 0;
                Rep(ref s, @"[ \t]+\n", "\n", ref n);                          // razmaci na kraju reda
                Rep(ref s, @"[ \t]+$", "", ref n);
                Rep(ref s, @"(?<=\S)[ \t]{2,}(?=\S)", " ", ref n);              // dupli razmaci u redu
                Rep(ref s, @"\n{3,}", "\n\n", ref n);                           // više praznih redova
                Rep(ref s, @"(?<=\S)[ \t]+([,.;:!?…)\]»])", "$1", ref n);       // razmak pre interpunkcije
                Rep(ref s, @"([(\[«])[ \t]+", "$1", ref n);                     // razmak posle otvorene zagrade
                Rep(ref s, @"([,;:])(?=\p{L})", "$1 ", ref n);                   // nedostaje razmak (ne dira 3,5 ni 12:30)
                Rep(ref s, @"(?<=\p{Ll}{2})([!?])(?=\p{Lu}\p{Ll})", "$1 ", ref n);
                Rep(ref s, @"(?<=\p{Ll}{3})(\.)(?=\p{Lu}\p{Ll})", "$1 ", ref n);  // "kraj.Novi" -> "kraj. Novi" (ne dira d.o.o.)
                r.Spaces = n;
            }

            if (o.Ellipsis)
            {
                int n = Regex.Matches(s, @"\.{3,}").Count;
                s = Regex.Replace(s, @"\.{3,}", "…");
                r.Ellipsis = n;
            }

            if (o.Dashes)
            {
                int n = 0;
                // dijalog: crtica na početku reda -> crta
                s = Regex.Replace(s, @"(^|\n)[ \t]*[-–][ \t]+", m => { n++; return m.Groups[1].Value + "— "; });
                // crtica sa razmacima između reči -> crta (u srpskoj tipografiji: polu-crta sa razmacima)
                s = Regex.Replace(s, @"(?<=\S) +-{1,2} +(?=\S)", m => { n++; return " – "; });
                // dupla crtica bez razmaka -> crta
                s = Regex.Replace(s, @"(?<=\p{L})--(?=\p{L})", m => { n++; return "—"; });
                // opseg brojeva: 10-20, 1990-2000 -> polu-crta
                s = Regex.Replace(s, @"(?<=\b\d{1,4})-(?=\d{1,4}\b)", m => { n++; return "–"; });
                r.Dashes = n;
            }

            if (o.Quotes) s = FixQuotes(s, o.EnglishQuotes, ref r.Quotes);

            if (o.NonBreaking)
            {
                int n = 0;
                // jednoslovne reči (i, u, a, o, s, k, v, I, U, A, O, S, K, V) se "lepe" za sledeću reč
                s = Regex.Replace(s, @"(?<=(^|[\s(„“""‚'—–]))([iuaoskvIUAOSKV]) (?=\S)", m => { n++; return m.Groups[2].Value + Nbsp; });
                // broj i jedinica: 10 km, 5 %, 20 €, 3 kg, 100 m
                s = Regex.Replace(s, @"(?<=\d) (?=(%|‰|°|€|\$|din\b|RSD\b|km\b|kg\b|g\b|mg\b|m\b|cm\b|mm\b|l\b|ml\b|h\b|min\b|s\b|KB\b|MB\b|GB\b|TB\b|px\b|pt\b|dpi\b))", m => { n++; return Nbsp.ToString(); });
                r.NonBreaking = n;
            }

            s = Regex.Replace(s, "(\\d+)", m => protectedParts[int.Parse(m.Groups[1].Value)]);
            return text.Contains("\r\n") ? s.Replace("\n", "\r\n") : s;
        }

        private static void Rep(ref string s, string pattern, string replacement, ref int n)
        {
            var re = new Regex(pattern, RegexOptions.Multiline);
            n += re.Matches(s).Count;
            s = re.Replace(s, replacement);
        }

        private static string FixQuotes(string s, bool english, ref int count)
        {
            char open = english ? '“' : '„', close = english ? '”' : '“';
            char sOpen = english ? '‘' : '‚', sClose = '‘';
            if (english) sClose = '’';
            var sb = new StringBuilder(s.Length);
            bool inDouble = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                char prev = i > 0 ? s[i - 1] : '\n';
                char next = i + 1 < s.Length ? s[i + 1] : '\n';
                bool opening = char.IsWhiteSpace(prev) || prev is '(' or '[' or '{' or '—' or '–' or ' ' or '' || (prev is '„' or '“' && c == '\'');

                if (c == '"' || c == '“' || c == '”' || c == '„' || c == '«' || c == '»')
                {
                    char target;
                    if (c == '„' || c == '«') target = open;
                    else if (c == '”' || c == '»') target = close;
                    else if (opening && !char.IsWhiteSpace(next)) target = open;
                    else if (!opening) target = close;
                    else target = inDouble ? close : open;
                    if (c == '“' && english == false)
                    {
                        // “ je već srpski ZATVORENI navodnik; kao otvoreni (engleski stil) ga pretvaramo u „
                        target = opening && !char.IsWhiteSpace(next) ? open : close;
                    }
                    inDouble = target == open;
                    if (target != c) count++;
                    sb.Append(target);
                }
                else if (c == '\'' || c == '’' || c == '‘')
                {
                    char target;
                    if (char.IsLetterOrDigit(prev) && char.IsLetter(next)) target = '’';           // rock’n’roll, O’Brien
                    else if (opening && char.IsDigit(next)) target = '’';                         // ’90-ih
                    else if (opening && !char.IsWhiteSpace(next)) target = sOpen;
                    else if (char.IsLetterOrDigit(prev) || prev is '.' or ',' or '!' or '?' or '…') target = (c == '\'' && !HasOpenSingleBefore(sb, sOpen)) ? '’' : sClose;
                    else target = c;
                    if (target != c) count++;
                    sb.Append(target);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool HasOpenSingleBefore(StringBuilder sb, char sOpen)
        {
            for (int i = sb.Length - 1; i >= 0 && i > sb.Length - 400; i--)
            {
                if (sb[i] == sOpen) return true;
                if (sb[i] == '\n') return false;
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // PRESLOVLJAVANJE
        // ════════════════════════════════════════════════════════════════
        private static readonly Dictionary<char, string> CyrToLat = new()
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['ђ'] = "đ", ['е'] = "e", ['ж'] = "ž", ['з'] = "z",
            ['и'] = "i", ['ј'] = "j", ['к'] = "k", ['л'] = "l", ['љ'] = "lj", ['м'] = "m", ['н'] = "n", ['њ'] = "nj", ['о'] = "o",
            ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['ћ'] = "ć", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c",
            ['ч'] = "č", ['џ'] = "dž", ['ш'] = "š",
        };

        public static string ToLatin(string text, out int changed)
        {
            changed = 0;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char lower = char.ToLowerInvariant(c);
                if (!CyrToLat.TryGetValue(lower, out var lat)) { sb.Append(c); continue; }
                changed++;
                if (c == lower) { sb.Append(lat); continue; }
                // Veliko slovo: Љ -> Lj usred reči sa malim slovima, LJ ako je cela reč velikim slovima
                bool nextUpper = i + 1 < text.Length && char.IsUpper(text[i + 1]);
                bool prevUpper = i > 0 && char.IsUpper(text[i - 1]);
                if (lat.Length == 2 && (nextUpper || (prevUpper && !(i + 1 < text.Length && char.IsLower(text[i + 1])))))
                    sb.Append(lat.ToUpperInvariant());
                else
                    sb.Append(char.ToUpperInvariant(lat[0])).Append(lat.Substring(1));
            }
            return sb.ToString();
        }

        // Reči u kojima "nj", "lj", "dž" NISU jedno slovo (prefiks + reč): kon-junkcija, nad-živeti...
        private static readonly string[] DigraphExceptions =
        {
            "konjunk", "konjug", "injek", "injun", "tanjug", "vanjezič", "vanjug",
            "nadžanj", "nadživ", "nadžnje", "odžali", "odživ", "podžanr", "podžel", "predživot", "nadžup",
        };

        private static readonly Dictionary<string, string> LatToCyr = new()
        {
            ["a"] = "а", ["b"] = "б", ["v"] = "в", ["g"] = "г", ["d"] = "д", ["đ"] = "ђ", ["e"] = "е", ["ž"] = "ж", ["z"] = "з",
            ["i"] = "и", ["j"] = "ј", ["k"] = "к", ["l"] = "л", ["m"] = "м", ["n"] = "н", ["o"] = "о",
            ["p"] = "п", ["r"] = "р", ["s"] = "с", ["t"] = "т", ["ć"] = "ћ", ["u"] = "у", ["f"] = "ф", ["h"] = "х", ["c"] = "ц",
            ["č"] = "ч", ["š"] = "ш", ["lj"] = "љ", ["nj"] = "њ", ["dž"] = "џ",
        };

        /// <summary>
        /// Latinica -> ćirilica. Ne dira linkove, mejlove, reči sa q/w/x/y (strane reči, nazivi
        /// proizvoda) ni reči napisane potpuno velikim slovima kraće od 4 slova (skraćenice: USB, PDF, AI).
        /// </summary>
        public static string ToCyrillic(string text, out int changed)
        {
            int count = 0;
            string result = Regex.Replace(text, @"(https?://\S+|www\.\S+|[\w.+-]+@[\w-]+\.[\w.-]+)|(\p{L}+)", m =>
            {
                if (m.Groups[1].Success) return m.Value;
                string w = m.Value;
                if (w.IndexOfAny(new[] { 'q', 'w', 'x', 'y', 'Q', 'W', 'X', 'Y' }) >= 0) return w;
                if (w.Length <= 4 && w.All(char.IsUpper) && w.Length > 1) return w;
                if (!w.Any(ch => LatToCyr.ContainsKey(char.ToLowerInvariant(ch).ToString()))) return w;
                string lowerW = w.ToLowerInvariant();
                bool noDigraph = DigraphExceptions.Any(e => lowerW.Contains(e));
                var sb = new StringBuilder();
                for (int i = 0; i < w.Length; i++)
                {
                    string one = w[i].ToString(), oneLower = one.ToLowerInvariant();
                    if (!noDigraph && i + 1 < w.Length)
                    {
                        string two = w.Substring(i, 2).ToLowerInvariant();
                        if (two is "lj" or "nj" or "dž" && LatToCyr.TryGetValue(two, out var d))
                        {
                            sb.Append(char.IsUpper(w[i]) ? d.ToUpperInvariant() : d);
                            i++; count++;
                            continue;
                        }
                    }
                    if (LatToCyr.TryGetValue(oneLower, out var cyr))
                    {
                        sb.Append(char.IsUpper(w[i]) ? cyr.ToUpperInvariant() : cyr);
                        count++;
                    }
                    else sb.Append(w[i]);
                }
                return sb.ToString();
            });
            changed = count;
            return result;
        }

        /// <summary>Da li je tekst pretežno ćirilički.</summary>
        public static bool IsMostlyCyrillic(string text)
        {
            int cyr = text.Count(c => c >= 'Ѐ' && c <= 'ӿ');
            int lat = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || "čćšžđČĆŠŽĐ".IndexOf(c) >= 0);
            return cyr > lat;
        }
    }
}
