// ══════════════════════════════════════════════════════════════════════════════
//  StrictQueryEngine.cs  —  Iskra AI-First Query System
//  UltraVideoEditor  |  Iskra Engine
//
//  ARHITEKTURA (3 sloja):
//
//  SLOJ 1 — Ollama (GLAVNI)
//    For each lyric line, Ollama receives: line + song context + age group + dynamic prompt
//    Returns: precise EN visual query
//    Prolazi kroz: ContentFilter (blacklist/whitelist po uzrastu)
//
//  SLOJ 2 — StrictQueryEngine (FALLBACK)
//    Ako Ollama nije dostupna, vrati glupost, ili filter odbije query:
//    Searches for a direct match in _actionMap (~600 entries, all topics)
//
//  SLOJ 3 — SmartFallback (NIKAD NULL)
//    Ako ni mapa nema match:
//    Kontekstualni fallback na osnovu teme pesme i uzrasta
//    ALWAYS returns something meaningful — black screen does not exist in the video
//
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AgeGroup — detektovani uzrast publike
    // ══════════════════════════════════════════════════════════════════════════
    public enum AgeGroup
    {
        Toddler,    // 0–3 godine
        Kids,       // ages 3–7 (default for children's songs)
        Tween,      // 7–12 godina
        Adult       // 12+
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SongContext — context of the entire song, passed per lyric line
    // ══════════════════════════════════════════════════════════════════════════
    public class SongContext
    {
        public AgeGroup AgeGroup   { get; set; } = AgeGroup.Kids;
        public string   Context    { get; set; } = "outdoor";
        public string   Mood       { get; set; } = "happy";
        public string   Season     { get; set; } = "none";
        public string   Setting    { get; set; } = "outdoor";
        public string   Theme      { get; set; } = "";
        public string   VisualStyle { get; set; } = "bright colorful outdoor children";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LyricTagType — lyric line classification for semantic matching
    //  Action      = visible physical action  ("runs", "jumps", "dances")
    //  Atmospheric = feeling / state / memory ("silence", "longing", "long ago")
    //  Object      = konkretan predmet / priroda ("sunce", "oblak", "cvet")
    //  Narrative   = pripovedanje / generalna slika  (default)
    // ══════════════════════════════════════════════════════════════════════════
    public enum LyricTagType { Action, Atmospheric, Object, Narrative }

    // ══════════════════════════════════════════════════════════════════════════
    //  SentimentPolarity — emocionalni polaritet stiha
    //  Positive  = radost, euforija, toplina, nada
    //  Negative  = sadness, longing, pain, melancholy, fear
    //  Neutral   = opis, pripovedanje, neutralna radnja
    //
    //  Kombinuje se sa LyricTagType:
    //  Action+Positive  → child running joyfully in the sun
    //  Action+Negative  → child running away from something, dramatic
    //  Atmospheric+Neg  → rainy window, loneliness, winter fog
    //  Atmospheric+Pos  → softness, tranquility, warm light
    // ══════════════════════════════════════════════════════════════════════════
    public enum SentimentPolarity { Positive, Negative, Neutral }

    // ══════════════════════════════════════════════════════════════════════════
    //  StrictQueryEngine — Glavna klasa
    // ══════════════════════════════════════════════════════════════════════════
    public static class StrictQueryEngine
    {
        // ── Content filter — words that must NEVER appear in a query for children ──
        private static readonly HashSet<string> _childBlacklist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nude","naked","sexy","sexual","adult","porn","erotic","intimate",
            "kissing couple","romantic kiss","making love","couple bedroom",
            "violence","blood","gore","weapon","gun","knife","fight","war","death",
            "alcohol","beer","wine","whiskey","vodka","cocktail","drunk","bar ",
            "cigarette","smoking","cigar","vape","drug","marijuana","pills",
            "horror","scary","dark alley","abandoned","cemetery","graveyard",
            "nightclub","strip","gambling","casino",
            "coffee","office","business","corporate","meeting","suit","tie",
            "black and white","monochrome","silhouette dark",
        };

        // ── For adults — only explicit content is blocked ─────────────────
        private static readonly HashSet<string> _adultBlacklist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nude","naked","porn","erotic","explicit","sexual intercourse",
            "gore","blood spatter","extreme violence","child abuse",
        };

        // ── Uzrasni prefix koji se dodaje na query ────────────────────────────
        private static string AgePrefix(AgeGroup age) => age switch
        {
            AgeGroup.Toddler => "toddler baby ",
            AgeGroup.Kids    => "child children ",
            AgeGroup.Tween   => "kids youth ",
            AgeGroup.Adult   => "",
            _                => "children "
        };

        // ══════════════════════════════════════════════════════════════════════
        //  ClassifyLyric — semantic classification of a lyric line
        //  Redosled provere: Action > Atmospheric > Object > Narrative
        //  Action: uses the existing _actionMap (same dictionary, no duplication)
        // ══════════════════════════════════════════════════════════════════════
        public static LyricTagType ClassifyLyric(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return LyricTagType.Narrative;
            string lower = lyric.ToLower();

            // Action — ako ima match u _actionMap, stih je akcioni
            foreach (var kv in _actionMap)
            {
                int idx = lower.IndexOf(kv.Key.ToLower(), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                bool leftOk  = idx == 0 || !char.IsLetter(lower[idx - 1]);
                bool rightOk = idx + kv.Key.Length >= lower.Length || !char.IsLetter(lower[idx + kv.Key.Length]);
                if (leftOk && rightOk) return LyricTagType.Action;
            }

            // Atmospheric — feelings, memories, states
            var atmosphericSignals = new[]
            {
                "sećanje","sjećanje","sećam","sjećam","pamtim","pamćenje",
                "čežnja","čežnjom","čeznjem","čeznem",
                "tišina","tišinom","tiho","u tišin",
                "usaml","sam i","sama i","samoća","samoćom",
                "tuga","tugom","tužan","tužna","tuguje",
                "san","sanjam","sanjao","sanjala","u snu","snovi","snovima",
                "davno","nekad","odavno","nekada","pre mnogo","bilo jednom",
                "prošlost","prošlosti","prošlo","prošlom",
                "duša","dušom","dušu","dušo",
                "setno","setna","setni","sjeta","sjetu",
                "nežno","nježno","nežan","nježan",
                "melanh","tamo negde","negde daleko","dalekim",
                "osećam","osjećam","osećaj","osjećaj",
                "miris","mirisem","mirišem",
                "prazni","praznin","prazan","prazna"
            };
            if (atmosphericSignals.Any(s => lower.Contains(s)))
                return LyricTagType.Atmospheric;

            // Object — konkretan predmet/priroda (bez radnje)
            var objectSignals = new[]
            {
                "sunce","mesec","zvezde","nebo","oblak","kiša","duga","vetar","vjetar",
                "cvet","cvijet","trava","drvo","šuma","reka","more","planina","polje",
                "leptir","ptica","pas","mačka","riba","srna","zec",
                "kamen","pesak","voda","vatra","zemlja","led","sneg","snijeg"
            };
            if (objectSignals.Any(s => lower.Contains(s)))
                return LyricTagType.Object;

            return LyricTagType.Narrative;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ClassifySentiment — per-stih emocionalni polaritet
        //  Negative signals take priority (sadness is stronger than neutral)
        // ══════════════════════════════════════════════════════════════════════
        public static SentimentPolarity ClassifySentiment(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return SentimentPolarity.Neutral;
            string lower = lyric.ToLower();

            // Negative signals — sadness, pain, fear, longing, loneliness
            var negativeSignals = new[]
            {
                "tuga","tugom","tužan","tužna","tuguje","tugujemo",
                "bol","boli","bolom","bolna","bolestan","boluje",
                "plače","plačem","plačeš","plačemo","suze","suza","suzama",
                "strah","straha","strahom","bojim","boji","bojimo",
                "sam ","sama ","samoća","samoćom","samoće","usaml",
                "čežnja","čežnjom","čeznjem","čeznem","nedostaješ","nedostaje",
                "ode","otišao","otišla","otišli","rastanak","odlazak","odlaze",
                "nema te","bez tebe","bez tebe","nisam tu","nisi tu",
                "zamara","umoran","umorna","iscrpl","slomljen","slomljeno",
                "mrak","tmina","tama","tamno","sumrak","noć bez",
                "kiša pada","pada kiša","oluja","grmljavina",
                "zaborav","zaboravi","zaboravljen","nestao","nestala",
                "nema nade","beznadje","beznađe","uzalud"
            };
            if (negativeSignals.Any(s => lower.Contains(s)))
                return SentimentPolarity.Negative;

            // Pozitivni signali — radost, toplina, euforija, nada
            var positiveSignals = new[]
            {
                "smej","smije","smeješ","nasmej","nasmeje","nasmejana","nasmejani",
                "sreća","srećan","srećna","srećno","srećni",
                "radost","radostan","radosna","raduje","radujemo",
                "blistaj","blista","sijaj","sija","svetlost","svetlo","svetli",
                "uživaj","uživa","uživamo","uživanje",
                "sloboda","slobodan","slobodna","slobodni",
                "krilima","leti","letimo","visoko","visinu",
                "sunce","sunčan","zlatno","zlato","toplo","toplina",
                "veselo","veseo","vesela","zajedno","druže","drugarstvo",
                "ljubav","volim","volimo","volite","srdačno","srce",
                "ples","pleši","plešemo","zabava","zabavno",
                "novi dan","jutro","zora","proljeće","proleće","cvetovi","cveće",
                "osmeh","osmijeh","osmehni","nada","nadamo","nade"
            };
            if (positiveSignals.Any(s => lower.Contains(s)))
                return SentimentPolarity.Positive;

            return SentimentPolarity.Neutral;
        }
        //  Now receives LyricTagType for a semantic hint to Ollama
        // ══════════════════════════════════════════════════════════════════════
        public static string BuildOllamaPrompt(string lyric, SongContext ctx,
            LyricTagType tagType = LyricTagType.Narrative,
            SentimentPolarity sentiment = SentimentPolarity.Neutral,
            bool needsCloseUp = false)
        {
            // Qwen 2.5 14B style: short and direct — Qwen follows precise instructions very well

            string agePrefix = ctx.AgeGroup switch
            {
                AgeGroup.Toddler => "toddler baby",
                AgeGroup.Kids    => "child children",
                AgeGroup.Tween   => "kids youth",
                AgeGroup.Adult   => "",
                _                => "children"
            };

            string seasonHint = ctx.Season switch
            {
                "winter" => " | Season: winter snow",
                "summer" => " | Season: summer sunny",
                "spring" => " | Season: spring flowers",
                "autumn" => " | Season: autumn leaves",
                _        => ""
            };

            string safetyRule = ctx.AgeGroup != AgeGroup.Adult
                ? "Safe for children only. No adults-only content, violence, alcohol, or scary imagery."
                : "Keep content tasteful and appropriate.";

            string queryStart = string.IsNullOrEmpty(agePrefix) ? "person or scene" : agePrefix;

            // Semantic hint for Ollama — the most important new addition
            // Tells Ollama WHICH TYPE of clip to look for, resolving the Tag-Semantic Gap
            string tagHint = tagType switch
            {
                LyricTagType.Atmospheric =>
                    "SCENE TYPE: ATMOSPHERIC (mood/memory/feeling lyric).\n" +
                    "DO NOT suggest action clips (running, jumping, dancing).\n" +
                    "USE: slow bokeh nature, child gazing distance, soft textures, " +
                    "contemplative face closeup, gentle water/wind/light movement.\n",
                LyricTagType.Action =>
                    "SCENE TYPE: ACTION (physical movement lyric).\n" +
                    "USE: dynamic movement clips — running, jumping, dancing, playing.\n" +
                    "Avoid static or contemplative shots for this lyric.\n",
                LyricTagType.Object =>
                    "SCENE TYPE: OBJECT/NATURE (concrete object in lyric).\n" +
                    "Literal visual IS acceptable here — show the named element.\n" +
                    "Enrich it: add a child interacting with it or ambient context.\n",
                _ =>
                    "SCENE TYPE: NARRATIVE (general/story lyric).\n" +
                    "USE: wide establishing shot or symbolic visual that sets the scene.\n"
            };

            // Sentiment hint — govori Ollami da li je stih pozitivan ili negativan
            // This resolves the "sadness = summer" problem: negative sentiment → dark/cool visuals
            string sentimentHint = sentiment switch
            {
                SentimentPolarity.Positive =>
                    "SENTIMENT: POSITIVE (joy/warmth/energy). " +
                    "Use bright, warm, colorful visuals. Sunny light, open spaces, smiling faces.\n",
                SentimentPolarity.Negative =>
                    "SENTIMENT: NEGATIVE (sadness/longing/melancholy). " +
                    "DO NOT use sunny/happy visuals. Use: rain, window reflections, grey sky, " +
                    "child alone, soft desaturated tones, gentle wind, empty bench, foggy morning.\n",
                _ =>
                    "SENTIMENT: NEUTRAL. Match the visual to the lyric's theme without strong emotional bias.\n"
            };

            // FIX-CLOSEUP: Force close-up shot when the lyric mentions face/hands/eyes/smile/child
            string closeUpHint = needsCloseUp
                ? "SHOT TYPE: CLOSE-UP required. This lyric mentions a child's face, hands, eyes or smile.\n" +
                  "IMPORTANT: Use close-up or medium-close shots only. NO wide landscape shots.\n" +
                  "Example good queries: 'child face closeup happy', 'child hands holding warm', 'child smiling closeup joy'.\n"
                : "";

            return $"Task: Stock video search query for music video.\n\n" +
                   $"Song: {ctx.Theme} | Mood: {ctx.Mood}{seasonHint}\n" +
                   $"Lyric: \"{lyric}\"\n\n" +
                   $"{tagHint}" +
                   $"{sentimentHint}" +
                   $"{closeUpHint}\n" +
                   $"Write ONE stock video search query (3-5 words) that visually matches this lyric.\n" +
                   $"Start with: {queryStart}\n" +
                   $"RULE: Prefer EMOTIONAL or ABSTRACT visuals over LITERAL ones.\n" +
                   $"BAD (literal): 'birds fly' -> 'birds flying sky'\n" +
                   $"GOOD (abstract): 'birds fly' -> 'child arms open freedom'\n" +
                   $"BAD: 'feel the sun' -> 'sun shining'\n" +
                   $"GOOD: 'feel the sun' -> 'child face sunshine glowing joy'\n" +
                   $"Exception: seasons and objects (ice cream, snow) CAN be literal.\n" +
                   $"{safetyRule}\n\n" +
                   $"Good examples:\n" +
                   $"- child running meadow carefree joyful\n" +
                   $"- children snow winter playing happy\n" +
                   $"- child gazing horizon soft bokeh peaceful\n" +
                   $"- family walking nature outdoor together\n\n" +
                   $"Query (write ONLY the query, no explanation):\n";
        }


        // ══════════════════════════════════════════════════════════════════════
        //  LAYER 1: Filter — cleans and validates Ollama output
        // ══════════════════════════════════════════════════════════════════════
        public static string FilterOllamaQuery(string raw, SongContext ctx)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Take only the first line — Ollama sometimes adds an explanation below
            string query = raw.Split('\n')[0].Trim().Trim('"', '\'', '.', ',');

            // If too long, try to shorten to first 6 words instead of discarding
            if (query.Length > 80)
            {
                var tokens80 = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                query = tokens80.Length > 6 ? string.Join(" ", tokens80.Take(6)) : null;
                if (string.IsNullOrWhiteSpace(query)) return null;
            }

            // Discard if it contains BHS diacritics OR any non-ASCII non-Latin character
            // This covers: Serbian (šđčćž), Chinese, Japanese, Arabic, Cyrillic, etc.
            // Qwen ponekad halucinira i vrati kineski tekst + exception stack trace — ovo to hvata
            if (query.Any(c => "šđčćžŠĐČĆŽ".Contains(c))) return null;
            if (query.Any(c => c > 0x024F && !char.IsWhiteSpace(c) && !char.IsPunctuation(c))) return null;
            // Discard if it contains typical stack trace characters (Qwen hallucination)
            if (query.Contains("Exception") || query.Contains("处理") || query.Contains("\n") ||
                query.Contains("ValueHandling") || query.Contains("Recognition")) return null;

            // Discard if it contains blacklist words (content safety)
            var blacklist = ctx.AgeGroup == AgeGroup.Adult ? _adultBlacklist : _childBlacklist;
            if (blacklist.Any(b => query.ToLower().Contains(b.ToLower()))) return null;

            // Discard ONLY if it is literally one of these generic phrases (exact match)
            // Previously: contained any of them — too aggressive, discarded good queries
            var tooGenericExact = new[] { "happy child", "children playing", "happy children",
                                          "child playing", "kids playing", "people" };
            if (tooGenericExact.Any(g => query.ToLower().Trim() == g)) return null;

            // Discard if only 1 word (not a real query)
            if (query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return null;

            // Add age prefix if no age-related words are present
            string prefix = AgePrefix(ctx.AgeGroup);
            bool hasAgeWord = new[] { "child","children","kid","kids","toddler","baby",
                                      "youth","adult","person","people","family","girl","boy" }
                .Any(w => query.ToLower().Contains(w));

            string finalQuery = hasAgeWord ? query : prefix + query;

            return finalQuery.Trim();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  LAYER 2: _actionMap — ~600 entries, covers all common topics
        //
        //  Format: serbian_word → EN visual query
        //  All values are already filtered and safe for children
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, string> _actionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── WALKING / STROLLING ──────────────────────────────────────────────
            { "šeta",        "child walking park path" },
            { "šetaj",       "child walking park path" },
            { "šetajte",     "children walking park path" },
            { "šetam",       "person walking park path" },
            { "šetamo",      "family walking park path" },
            { "šetanje",     "family walking park path" },
            { "šetnju",      "family walking park path" },
            { "šetnjicu",    "child walking park path" },
            { "šetnji",      "family walking park path" },
            { "šeće",        "child walking park path" },
            { "hodaj",       "child walking path outdoor" },
            { "hodajte",     "children walking path outdoor" },
            { "hoda",        "child walking path outdoor" },
            { "hodanje",     "child walking path outdoor" },
            { "koračaj",     "child walking path confident" },
            { "koračam",     "child walking path confident" },
            { "šetalištem",  "family walking promenade" },
            { "šetalistu",   "family walking promenade" },
            { "prošetaj",    "child walking park path" },

            // ── RUNNING ───────────────────────────────────────────────────────
            { "trči",        "child running park" },
            { "trčite",      "children running park" },
            { "trčim",       "child running park" },
            { "trče",        "children running park" },
            { "trčanje",     "children running park" },
            { "trčimo",      "children running together" },
            { "istrčaj",     "child running start outdoor" },
            { "potrčaj",     "child running joyful outdoor" },
            { "trčaću",      "child running park" },

            // ── SKAKANJE ──────────────────────────────────────────────────────
            { "skače",       "child jumping outdoor joyful" },
            { "skačem",      "child jumping outdoor joyful" },
            { "skoči",       "child jumping mid air outdoor" },
            { "skočite",     "children jumping outdoor" },
            { "skači",       "child jumping mid air outdoor" },
            { "skakanje",    "children jumping playground" },
            { "poskakuj",    "child hopping joyful outdoor" },
            { "poskakujem",  "child hopping joyful outdoor" },
            { "skok",        "child jump outdoor" },

            // ── PLES ──────────────────────────────────────────────────────────
            { "pleše",       "child dancing outdoor joyful" },
            { "plešem",      "child dancing outdoor joyful" },
            { "plešite",     "children dancing together" },
            { "plesati",     "children dancing together" },
            { "ples",        "children dancing colorful" },
            { "plešemo",     "children dancing together" },
            { "zapleši",     "child dancing spin outdoor" },
            { "zaplešimo",   "children dancing together" },
            { "vrti",        "child spinning dancing outdoor" },
            { "vrtim",       "child spinning dancing outdoor" },
            { "zavrti",      "child spinning outdoor happy" },

            // ── PEVANJE ───────────────────────────────────────────────────────
            { "peva",        "child singing joyful outdoor" },
            { "pjeva",       "child singing joyful outdoor" },
            { "pevaj",       "child singing open mouth" },
            { "pjevaj",      "child singing open mouth" },
            { "pevajte",     "children singing chorus" },
            { "pjevajte",    "children singing chorus" },
            { "pevamo",      "children singing together" },
            { "pjevamo",     "children singing together" },
            { "pevanje",     "child singing music stage" },
            { "pjevanje",    "child singing music stage" },
            { "zapevaj",     "child singing joyful" },
            { "zapjevaj",    "child singing joyful" },

            // ── SMEJANJE ──────────────────────────────────────────────────────
            { "smej",        "child laughing happy closeup" },
            { "smejte",      "children laughing happy" },
            { "smeje",       "child laughing happy closeup" },
            { "smejemo",     "children laughing happy" },
            { "smejem",      "child laughing happy closeup" },
            { "smijeh",      "child laughing happy closeup" },
            { "nasmej",      "child laughing happy closeup" },
            { "nasmejte",    "children laughing happy" },
            { "osmeh",       "child smiling closeup bright" },
            { "osmijeh",     "child smiling closeup bright" },
            { "osmehni",     "child smiling closeup bright" },
            { "smeje se",    "child laughing happy closeup" },
            { "nasmejana",   "girl laughing happy outdoor" },
            { "nasmejani",   "children laughing happy" },
            { "nasmešena",   "child smiling happy outdoor" },

            // ── SHINING / GLEAMING / ENJOYMENT ───────────────────────────────────
            { "blistaj",     "child face sunshine glowing" },
            { "blistajte",   "children sunshine glowing" },
            { "blista",      "child face sunshine glowing" },
            { "sijaj",       "sunshine golden rays outdoor" },
            { "sija",        "sunshine golden rays outdoor" },
            { "uživaj",      "child enjoying outdoor happy" },
            { "uživajte",    "children enjoying outdoor" },
            { "uživa",       "child enjoying outdoor happy" },
            { "uživamo",     "children enjoying outdoor" },
            { "uživanju",    "child enjoying outdoor happy" },
            { "osećaj",      "child happy outdoor carefree" },
            { "osjećaj",     "child happy outdoor carefree" },

            // ── SUNCE / NEBO / VREME ──────────────────────────────────────────
            { "sunce",       "sunshine golden park outdoor" },
            { "suncu",       "sunshine golden park outdoor" },
            { "sunca",       "sunshine golden outdoor" },
            { "sunčano",     "sunny day park outdoor bright" },
            { "sunčan",      "sunny day park outdoor bright" },
            { "nebo",        "blue sky clouds outdoor sunny" },
            { "nebu",        "blue sky clouds outdoor sunny" },
            { "oblak",       "white clouds blue sky outdoor" },
            { "oblaci",      "white clouds blue sky outdoor" },
            { "kiša",        "child rain puddle boots splashing" },
            { "kišu",        "child rain puddle boots splashing" },
            { "duga",        "rainbow colorful sky outdoor" },
            { "vetar",       "child wind hair outdoor laughing" },
            { "vjetar",      "child wind hair outdoor laughing" },
            { "vetru",       "child wind outdoor laughing" },
            { "mraz",        "frost winter outdoor cold" },

            // ── SEASONS ──────────────────────────────────────────────────────
            { "proleće",     "spring flowers children park" },
            { "proljeće",    "spring flowers children park" },
            { "proleća",     "spring flowers children park" },
            { "proljeća",    "spring flowers children park" },
            { "prolećem",    "spring flowers outdoor nature" },
            { "jesen",       "autumn leaves children park" },
            { "jeseni",      "autumn leaves children park" },
            { "jesenjem",    "autumn leaves park outdoor" },
            { "zima",        "winter snow children outdoor" },
            { "zime",        "winter snow children outdoor" },
            { "zimi",        "winter snow children outdoor" },
            { "zimski",      "winter snow children outdoor" },
            { "zimskog",     "winter outdoor snow nature" },
            { "zimskom",     "winter outdoor snow nature" },
            { "leto",        "summer sunny children outdoor" },
            { "ljeto",       "summer sunny children outdoor" },
            { "leti",        "summer sunny children outdoor" },
            { "ljeti",       "summer sunny children outdoor" },
            { "leta",        "summer sunny children outdoor" },

            // ── SNEG ──────────────────────────────────────────────────────────
            { "sneg",        "children playing snow winter" },
            { "snijeg",      "children playing snow winter" },
            { "snegu",       "children playing snow winter" },
            { "snijegu",     "children playing snow winter" },
            { "snehu",       "children playing snow winter" },
            { "snežno",      "winter snow park children" },
            { "snežan",      "snowy winter park outdoor" },
            { "snežnom",     "snowy winter park outdoor" },
            { "grudvu",      "child snowball winter fun" },
            { "grudvanje",   "children snowball fight winter" },
            { "grudvaju",    "children snowball fight winter" },
            { "sanjkanje",   "child sledding snow winter" },
            { "sanka",       "child sledding snow winter" },
            { "sankaju",     "child sledding snow winter" },
            { "klizanje",    "child ice skating winter" },
            { "klizalište",  "child ice skating rink winter" },

            // ── ZIMSKA OPREMA ──────────────────────────────────────────────────
            { "čizme",       "child winter boots snow" },
            { "čizmama",     "child winter boots snow closeup" },
            { "čizmu",       "child winter boots snow" },
            { "rukavice",    "child winter mittens snow hands" },
            { "rukavicama",  "child winter mittens snow hands" },
            { "rukavicu",    "child winter mittens snow" },
            { "skafander",   "child snowsuit playing snow" },
            { "skafanderu",  "child snowsuit playing snow" },
            { "kapu",        "child winter hat smiling snow" },
            { "kapi",        "child winter hat smiling snow" },
            { "šal",         "child scarf winter cozy outdoor" },
            { "šalom",       "child scarf winter cozy outdoor" },
            { "šalu",        "child scarf winter cozy outdoor" },
            { "jaknu",       "child jacket outdoor colorful" },
            { "jakna",       "child jacket outdoor colorful" },
            { "kaput",       "child coat winter outdoor" },
            { "kišobran",    "child umbrella rain colorful" },
            { "kišobranom",  "child umbrella rain colorful" },

            // ── FOOD AND DRINK ───────────────────────────────────────────────────
            { "sladoled",    "child eating ice cream" },
            { "sladoleda",   "child eating ice cream" },
            { "sladoledom",  "child eating ice cream" },
            { "čaj",         "child drinking hot tea cup" },
            { "čaja",        "child drinking hot tea cup" },
            { "čajem",       "child drinking hot tea cup" },
            { "čokolada",    "child hot chocolate cup cozy" },
            { "čokoladu",    "child hot chocolate cup cozy" },
            { "čokoladom",   "child hot chocolate cup cozy" },
            { "topla",       "child hot drink cup cozy winter" },
            { "topli",       "child hot drink cup cozy winter" },
            { "jabuka",      "child eating apple healthy" },
            { "jabuku",      "child eating apple healthy" },
            { "jabuke",      "child eating apple healthy" },
            { "banana",      "child eating banana fruit" },
            { "bananu",      "child eating banana fruit" },
            { "jagoda",      "child eating strawberry summer" },
            { "jagode",      "child eating strawberry summer" },
            { "kolač",       "child cookie baking kitchen" },
            { "kolače",      "child cookie baking kitchen" },
            { "torta",       "birthday cake children candles" },
            { "tortu",       "birthday cake children candles" },
            { "torte",       "birthday cake children candles" },
            { "sok",         "child drinking juice glass" },
            { "soka",        "child drinking juice glass" },
            { "sokom",       "child drinking juice glass" },
            { "mleko",       "child drinking milk glass" },
            { "mlijeko",     "child drinking milk glass" },
            { "mleku",       "child drinking milk glass" },
            { "hleb",        "child bread healthy food" },
            { "hljeb",       "child bread healthy food" },
            { "pizza",       "children eating pizza happy" },
            { "pizzu",       "children eating pizza happy" },
            { "sendvič",     "child eating sandwich outdoor" },
            { "voće",        "child eating fruit healthy outdoor" },
            { "povrće",      "child vegetables healthy food" },
            { "med",         "honey jar nature outdoor" },
            { "jede",        "child eating outdoor happy" },
            { "jedemo",      "family eating outdoor picnic" },
            { "jedu",        "children eating outdoor happy" },
            { "jedi",        "child eating outdoor happy" },
            { "pije",        "child drinking outdoor happy" },
            { "pijemo",      "family drinking outdoor" },
            { "piju",        "children drinking outdoor" },
            { "pij",         "child drinking glass closeup" },
            { "kupite",      "family buying ice cream" },
            { "kupi",        "child buying ice cream shop" },

            // ── PARK / PRIRODA / MESTA ─────────────────────────────────────────
            { "park",        "children park sunny" },
            { "parku",       "children park sunny" },
            { "parka",       "children park sunny" },
            { "parkić",      "children playground park" },
            { "parkićem",    "children playground park" },
            { "parkiću",     "children playground park" },
            { "grad",        "child city street walk" },
            { "gradu",       "child city street walk" },
            { "grada",       "child city street walk" },
            { "ulicom",      "child street walk outdoor" },
            { "ulici",       "child street walk outdoor" },
            { "priroda",     "child nature outdoor path" },
            { "prirodom",    "child nature outdoor path" },
            { "prirodi",     "child nature outdoor path" },
            { "šuma",        "children forest path exploring" },
            { "šumi",        "children forest path exploring" },
            { "šumom",       "children forest path exploring" },
            { "livada",      "children meadow flowers running" },
            { "livadi",      "children meadow flowers running" },
            { "livadom",     "children meadow flowers running" },
            { "polje",       "child field outdoor running" },
            { "polju",       "child field outdoor running" },
            { "poljem",      "child field outdoor running" },
            { "plaža",       "child beach sea summer" },
            { "plaži",       "child beach sea summer" },
            { "plažom",      "child beach sea summer" },
            { "more",        "child sea beach summer waves" },
            { "moru",        "child sea beach summer waves" },
            { "jezero",      "child lake outdoor summer" },
            { "jezeru",      "child lake outdoor summer" },
            { "reka",        "child river outdoor nature" },
            { "reci",        "child river outdoor nature" },
            { "rekom",       "child river outdoor nature" },
            { "rijeka",      "child river outdoor nature" },
            { "potok",       "child stream outdoor nature" },
            { "dvorište",    "children backyard playing sunny" },
            { "dvorištu",    "children backyard playing sunny" },
            { "vrt",         "child garden flowers outdoor" },
            { "vrtu",        "child garden flowers outdoor" },
            { "kuća",        "child home family outdoor" },
            { "kući",        "child home family outdoor" },
            { "kuće",        "child home family outdoor" },
            { "škola",       "children school outdoor happy" },
            { "školi",       "children school outdoor happy" },
            { "školu",       "children school outdoor happy" },
            { "brdo",        "children hill outdoor nature" },
            { "planina",     "mountain nature outdoor children" },
            { "planini",     "mountain nature outdoor children" },
            { "pesak",       "child playing sand beach" },
            { "pijesak",     "child playing sand beach" },
            { "lokva",       "child jumping puddle boots" },
            { "lokvi",       "child jumping puddle boots" },
            { "blato",       "child playing mud outdoor" },

            // ── PLANTS / FLOWERS ─────────────────────────────────────────────────
            { "cvet",        "child flower garden colorful" },
            { "cvjet",       "child flower garden colorful" },
            { "cvijet",      "child flower garden colorful" },
            { "cvetovi",     "children flowers garden colorful" },
            { "cveće",       "children flowers garden colorful" },
            { "cvijeće",     "children flowers garden colorful" },
            { "trava",       "children playing grass outdoor" },
            { "travi",       "children playing grass outdoor" },
            { "travom",      "children playing grass outdoor" },
            { "drvo",        "child tree climbing outdoor" },
            { "drvetu",      "child tree climbing outdoor" },
            { "drveće",      "children trees outdoor nature" },
            { "list",        "child autumn leaf outdoor" },
            { "lišće",       "child autumn leaves park" },
            { "granu",       "child tree branch outdoor" },
            { "šareni",      "colorful outdoor children happy" },
            { "šarena",      "colorful outdoor children happy" },

            // ── PORODICA ───────────────────────────────────────────────────────
            { "mama",        "mother child hug outdoor" },
            { "mamu",        "mother child hug outdoor" },
            { "mami",        "mother child hug outdoor" },
            { "mama i",      "mother child outdoor together" },
            { "tata",        "father child playing outdoor" },
            { "tatu",        "father child playing outdoor" },
            { "tati",        "father child playing outdoor" },
            { "tata i",      "father child outdoor together" },
            { "roditelji",   "parents children outdoor happy" },
            { "roditeljima", "parents children outdoor happy" },
            { "baka",        "grandmother grandchild park" },
            { "baki",        "grandmother grandchild park" },
            { "baku",        "grandmother grandchild park" },
            { "deka",        "grandfather grandchild park" },
            { "deki",        "grandfather grandchild park" },
            { "deku",        "grandfather grandchild park" },
            { "djed",        "grandfather grandchild park" },
            { "deda",        "grandfather grandchild park" },
            { "brat",        "siblings playing outdoor" },
            { "brata",       "siblings playing outdoor" },
            { "bratu",       "siblings playing outdoor" },
            { "sestra",      "siblings playing outdoor" },
            { "sestru",      "siblings playing outdoor" },
            { "sestro",      "siblings playing outdoor" },
            { "porodica",    "family outdoor together park" },
            { "porodicu",    "family outdoor together park" },
            { "porodicom",   "family outdoor together park" },
            { "porodici",    "family outdoor together park" },
            { "rukicu",      "parent child holding hands walk" },
            { "rukom",       "parent child holding hands" },
            { "zajedno",     "children together park playing" },
            { "svi",         "children group outdoor happy" },

            // ── PRIJATELJI / DECA ──────────────────────────────────────────────
            { "drugar",      "children friends playing" },
            { "drugare",     "children friends playing" },
            { "drugari",     "children friends playing" },
            { "drugova",     "children friends playing" },
            { "prijatelj",   "children friends outdoor" },
            { "prijatelji",  "children friends outdoor" },
            { "dete",        "child outdoor happy" },
            { "deca",        "children outdoor happy" },
            { "djeca",       "children outdoor happy" },
            { "deci",        "children outdoor happy" },
            { "dijete",      "child outdoor happy" },
            { "beba",        "baby happy outdoor" },
            { "bebom",       "baby happy outdoor" },
            { "mali",        "small child outdoor" },
            { "mala",        "small child outdoor" },
            { "klinci",      "children outdoor playing" },
            { "klince",      "children outdoor playing" },

            // ── ZDRAVLJE / TELO / AKTIVNOST ────────────────────────────────────
            { "zdravo",      "healthy child active outdoor" },
            { "zdravlje",    "healthy child active outdoor" },
            { "zdravim",     "healthy child active outdoor" },
            { "zdravi",      "healthy children active outdoor" },
            { "telo",        "child active healthy outdoor" },
            { "tijelo",      "child active healthy outdoor" },
            { "telu",        "child active healthy outdoor" },
            { "jako",        "child strong healthy outdoor" },
            { "jaki",        "children strong healthy outdoor" },
            { "lako",        "child carefree outdoor nature" },
            { "laka",        "child carefree outdoor nature" },
            { "snažno",      "child strong healthy outdoor" },
            { "aktivno",     "child active outdoor running" },
            { "fit",         "child active outdoor healthy" },
            { "sport",       "children sport outdoor active" },
            { "sportu",      "children sport outdoor active" },
            { "vežba",       "child exercise outdoor active" },
            { "vežbaj",      "child exercise outdoor active" },

            // ── AKTIVNOSTI ──────────────────────────────────────────────────────
            { "igra",        "children playing park" },
            { "igraju",      "children playing park" },
            { "igraj",       "children playing park" },
            { "igramo",      "children playing together" },
            { "igramo se",   "children playing together" },
            { "igranje",     "children playing outdoor" },
            { "plivanje",    "child swimming pool summer" },
            { "pliva",       "child swimming pool summer" },
            { "plivaj",      "child swimming pool summer" },
            { "bicikl",      "child riding bicycle park" },
            { "biciklom",    "child riding bicycle park" },
            { "voze",        "children riding bicycle park" },
            { "vozi",        "child riding bicycle park" },
            { "lopta",       "children playing ball park" },
            { "loptu",       "children playing ball park" },
            { "loptom",      "children playing ball park" },
            { "ljuljačka",   "child swing playground happy" },
            { "ljuljačku",   "child swing playground happy" },
            { "ljulja",      "child swing playground happy" },
            { "tobogan",     "child slide playground happy" },
            { "toboganom",   "child slide playground happy" },
            { "klackalica",  "children seesaw playground" },
            { "knjiga",      "child reading book outdoor" },
            { "knjigom",     "child reading book outdoor" },
            { "čitanje",     "child reading book outdoor" },
            { "čita",        "child reading book outdoor" },
            { "crtanje",     "child drawing colorful" },
            { "crta",        "child drawing colorful" },
            { "bojanje",     "child coloring book colorful" },
            { "slika",       "child painting colorful" },
            { "slikanje",    "child painting colorful" },
            { "zmaj",        "child flying kite outdoor field" },
            { "zmaja",       "child flying kite outdoor field" },
            { "trampolin",   "child trampoline jumping outdoor" },
            { "roleri",      "child roller skates path" },
            { "trotinet",    "child scooter park path" },
            { "fudbal",      "children football soccer park" },
            { "košarka",     "children basketball outdoor" },
            { "penjanje",    "child climbing outdoor" },
            { "penje",       "child climbing outdoor" },
            { "skrivanje",   "children hide seek outdoor" },
            { "skrivača",    "children hide seek outdoor" },
            { "trampom",     "children trampoline jumping" },

            // ── MUZIKA / INSTRUMENTI ───────────────────────────────────────────
            { "muzika",      "children music colorful" },
            { "muziku",      "children music colorful" },
            { "muzičar",     "child musician instrument" },
            { "pesma",       "child singing music outdoor" },
            { "pjesma",      "child singing music outdoor" },
            { "pesmu",       "child singing music outdoor" },
            { "melodija",    "child music happy outdoor" },
            { "ritam",       "children music rhythm clapping" },
            { "gitara",      "child playing guitar music" },
            { "gitaru",      "child playing guitar music" },
            { "klavir",      "child playing piano music" },
            { "klaviru",     "child playing piano music" },
            { "bubnjevi",    "child playing drums music" },
            { "flauta",      "child playing flute music" },
            { "violina",     "child playing violin music" },
            { "nota",        "music notes children colorful" },
            { "note",        "music notes children colorful" },
            { "vatromet",    "fireworks colorful night sky" },
            { "svira",       "child playing instrument music" },

            // ── ANIMALS ──────────────────────────────────────────────────────
            { "pas",         "dog playing child outdoor" },
            { "psa",         "dog playing child outdoor" },
            { "psu",         "dog playing child outdoor" },
            { "psom",        "dog playing child outdoor" },
            { "psić",        "puppy cute child playing" },
            { "psića",       "puppy cute child playing" },
            { "mačka",       "cat child home cute" },
            { "mačku",       "cat child home cute" },
            { "mačke",       "cat child home cute" },
            { "mačić",       "kitten cute child playing" },
            { "leptir",      "butterfly flower garden child" },
            { "leptira",     "butterfly flower garden child" },
            { "leptiri",     "butterflies flower garden" },
            { "ptica",       "birds flying sky nature" },
            { "ptice",       "birds flying sky nature" },
            { "pticom",      "birds flying sky nature" },
            { "zec",         "rabbit cute child" },
            { "zeca",        "rabbit cute child" },
            { "zečić",       "bunny cute child" },
            { "konj",        "horse child riding field" },
            { "konja",       "horse child riding field" },
            { "krava",       "cow farm child outdoor" },
            { "ovca",        "sheep farm child outdoor" },
            { "patka",       "duck water outdoor cute" },
            { "patku",       "duck water outdoor cute" },
            { "pile",        "chick cute outdoor child" },
            { "piletom",     "chicken farm outdoor child" },
            { "pčela",       "bee flower garden nature" },
            { "bubamara",    "ladybug flower garden child" },
            { "mrav",        "ant outdoor nature child" },
            { "puž",         "snail outdoor nature child" },
            { "riba",        "fish water outdoor child" },
            { "ribe",        "fish water outdoor child" },
            { "delfin",      "dolphin sea water child" },
            { "pingvin",     "penguin winter cute child" },
            { "medved",      "bear teddy outdoor child" },
            { "medvjed",     "bear teddy outdoor child" },
            { "slon",        "elephant child outdoor" },
            { "žirafa",      "giraffe child outdoor" },
            { "majmun",      "monkey child outdoor playful" },
            { "lisica",      "fox outdoor nature child" },
            { "jež",         "hedgehog cute outdoor child" },
            { "vjeverica",   "squirrel outdoor nature child" },
            { "vjevericu",   "squirrel outdoor nature child" },
            { "žaba",        "frog outdoor nature child" },
            { "patak",       "duck outdoor water child" },
            { "papagaj",     "parrot colorful bird child" },
            { "sova",        "owl nature outdoor child" },
            { "jagnje",      "lamb cute outdoor child" },

            // ── OBJECTS / TOYS ─────────────────────────────────────────────────
            { "balon",       "child balloon colorful outdoor" },
            { "balone",      "children balloons colorful" },
            { "balonom",     "child balloon colorful outdoor" },
            { "igračka",     "child toy colorful playing" },
            { "igračku",     "child toy colorful playing" },
            { "igračke",     "children toys colorful playing" },
            { "lutka",       "child doll playing" },
            { "lutku",       "child doll playing" },
            { "kocke",       "child building blocks colorful" },
            { "slagalica",   "child puzzle solving" },
            { "slagalicu",   "child puzzle solving" },
            { "auto",        "child toy car playing" },
            { "autić",       "child toy car playing" },
            { "voz",         "child toy train playing" },
            { "avion",       "child toy airplane outdoor" },
            { "brod",        "child toy boat water" },
            { "raketa",      "child toy rocket play" },
            { "zvezda",      "star night sky child looking" },
            { "zvezde",      "stars night sky child" },
            { "zvijezda",    "star night sky child looking" },
            { "mesec",       "moon night sky child" },
            { "mjesec",      "moon night sky child" },
            { "ruksak",      "child backpack school outdoor" },
            { "torba",       "child bag outdoor" },
            { "šešir",       "child hat outdoor sunny" },
            { "naočare",     "child sunglasses outdoor fun" },
            { "lopaticu",    "child sand shovel beach" },
            { "lopaticom",   "child sand shovel beach" },
            { "kantu",       "child sand bucket beach" },
            { "kantom",      "child sand bucket beach" },
            { "džemper",     "child sweater colorful outdoor" },
            { "majicu",      "child tshirt colorful outdoor" },
            { "cipele",      "child shoes outdoor" },

            // ── EMOCIJE / STANJA ───────────────────────────────────────────────
            { "sreća",       "child happy outdoor joyful" },
            { "srećan",      "child happy outdoor joyful" },
            { "srećna",      "girl happy outdoor joyful" },
            { "radost",      "child joyful outdoor happy" },
            { "radosti",     "children joyful outdoor happy" },
            { "veselje",     "children joyful outdoor happy" },
            { "vesel",       "children joyful outdoor happy" },
            { "veselo",      "children joyful outdoor happy" },
            { "sloboda",     "child arms open field outdoor" },
            { "slobodan",    "child arms open field outdoor" },
            { "slobodna",    "child arms open field outdoor" },
            { "mir",         "child peaceful nature outdoor" },
            { "miran",       "child peaceful nature outdoor" },
            { "mirno",       "child peaceful nature outdoor" },
            { "ljubav",      "family love outdoor hug" },
            { "ljubavi",     "family love outdoor hug" },
            { "voli",        "family love outdoor hug" },
            { "volim",       "child love family hug" },
            { "zagrli",      "child hug family outdoor" },
            { "zagrljaj",    "child hug family outdoor" },
            { "nasmeh",      "child smiling happy outdoor" },
            { "igrive",      "children playful outdoor" },
            { "energija",    "children active outdoor energetic" },
            { "snaga",       "child strong healthy outdoor" },

            // ── GENERAL ACTIONS ───────────────────────────────────────────────────
            { "gledam",      "child looking outdoor curious" },
            { "gledaj",      "child looking outdoor curious" },
            { "gledamo",     "children looking outdoor" },
            { "vidiš",       "child seeing outdoor curious" },
            { "vidi",        "child seeing outdoor curious" },
            { "znaš",        "child learning outdoor curious" },
            { "znaj",        "child learning outdoor curious" },
            { "učiti",       "child learning outdoor curious" },
            { "upoznaj",     "child exploring outdoor curious" },
            { "upoznajmo",   "children exploring outdoor" },
            { "otvoriš",     "child opening eyes morning" },
            { "otvori",      "child opening eyes morning" },
            { "ustani",      "child waking up morning" },
            { "ustajanje",   "child waking up morning" },
            { "spavaj",      "child sleeping cozy bedroom" },
            { "spava",       "child sleeping cozy bedroom" },
            { "sni",         "child dreaming cozy bedroom" },
            { "sanja",       "child dreaming cozy bedroom" },
            { "dođi",        "child outdoor inviting friends" },
            { "hajde",       "children outdoor together" },
            { "povedite",    "family children outdoor walk" },
            { "povedi",      "parent child outdoor walk" },
            { "pođi",        "child walking outdoor path" },
            { "idemo",       "family children outdoor walk" },
            { "idi",         "child walking outdoor" },
            { "pravac",      "child walking purposeful outdoor" },
            { "oseti",       "child outdoor nature feeling" },
            { "osjeti",      "child outdoor nature feeling" },

            // ── RAZNO ──────────────────────────────────────────────────────────
            { "noć",         "child bedroom cozy night" },
            { "noći",        "child bedroom cozy night" },
            { "jutro",       "child morning outdoor bright" },
            { "jutros",      "child morning outdoor bright" },
            { "dan",         "child daytime outdoor sunny" },
            { "dana",        "child daytime outdoor sunny" },
            { "oči",         "child eyes closeup happy" },
            { "oko",         "child eyes closeup curious" },
            { "ruka",        "child hands outdoor" },
            { "ruke",        "child hands outdoor" },
            { "noge",        "child feet path outdoor" },
            { "nogama",      "child feet walking path" },
            { "kraj",        "child outdoor nature end journey" },
            { "sve",         "children outdoor together happy" },
            { "naše",        "children together outdoor happy" },
            { "vaše",        "children together outdoor happy" },
        };

        // ══════════════════════════════════════════════════════════════════════
        //  SLOJ 3: SmartFallback — nikad null, nikad crni ekran
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, string[]> _contextFallbacks =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["outdoor"]   = new[] { "children playing park sunny", "child running outdoor bright", "family walking park path" },
            ["seasons"]   = new[] { "children outdoor seasonal nature", "child playing nature outdoor", "family outdoor nature walk" },
            ["health"]    = new[] { "children active outdoor healthy", "child running park healthy", "children exercise outdoor fun" },
            ["nature"]    = new[] { "children nature outdoor exploring", "child forest path outdoor", "children meadow flowers outdoor" },
            ["lullaby"]   = new[] { "child sleeping cozy peaceful", "baby sleeping soft light", "child bedroom cozy night" },
            ["music"]     = new[] { "children music colorful outdoor", "child singing joyful outdoor", "children instrument music fun" },
            ["dance"]     = new[] { "children dancing outdoor colorful", "child dancing joyful outdoor", "children dance fun outdoor" },
            ["party"]     = new[] { "children birthday party outdoor", "kids celebration colorful outdoor", "children happy party outdoor" },
            ["school"]    = new[] { "children school outdoor happy", "kids learning outdoor bright", "children school friends outdoor" },
            ["animal"]    = new[] { "child animals outdoor nature", "children pet outdoor happy", "kids animals nature outdoor" },
            ["christmas"] = new[] { "children christmas outdoor snow", "kids holiday colorful outdoor", "children winter holiday outdoor" },
            ["love"]      = new[] { "family outdoor together warm", "children hugging outdoor happy", "family love outdoor bright" },
            ["sad"]       = new[] { "child peaceful outdoor nature", "child calm outdoor nature", "children gentle outdoor peaceful" },
            ["adventure"] = new[] { "children adventure outdoor exploring", "kids outdoor adventure nature", "children exploring nature outdoor" },
            ["fun"]       = new[] { "children playing outdoor fun", "kids outdoor happy sunny", "children fun outdoor bright" },
        };

        public static string GetSmartFallback(SongContext ctx)
        {
            string key = ctx?.Context ?? "fun";
            if (_contextFallbacks.TryGetValue(key, out var options))
            {
                // Rotating — each call returns a different frame
                int idx = Math.Abs(DateTime.Now.Millisecond % options.Length);
                string query = options[idx];

                // Dodaj sezonski kontekst
                if (!string.IsNullOrEmpty(ctx?.Season) && ctx.Season != "none" && ctx.Season != "all")
                    query += " " + ctx.Season;

                return query;
            }
            // Apsolutni minimum — uvek radi
            return "children playing outdoor sunny";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetHardCodedQuery — search in _actionMap with priority scoring
        //  Priority: concrete objects (food/clothing/toys) > actions > states
        //  Resolves the "walk+run+smile" problem — picks the most relevant keyword
        // ══════════════════════════════════════════════════════════════════════
        public static string GetHardCodedQuery(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            string lower = lyric.ToLower();

            // Bonus by category — more concrete object = higher bonus
            // Ovo osigurava da "sladoled" pobedi "leti" u istom stihu
            var categoryBonus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                // Food and drink (most concrete, directly visual)
                {"sladoled",50},{"čokolada",50},{"čaj",45},{"torta",50},{"jabuka",45},
                {"banana",45},{"sendvič",45},{"sokić",45},{"mleko",40},{"voće",40},
                // Clothing and winter gear
                {"skafander",50},{"rukavice",45},{"čizme",45},{"kapu",40},{"šal",40},
                {"šešir",40},{"cipele",40},{"jakna",40},{"šorce",35},
                // Toys and vehicles
                {"autić",50},{"autic",50},{"bicikl",50},{"romobil",50},{"lopta",45},
                {"balon",45},{"tobogan",45},{"ljuljaška",45},{"peskara",45},{"zmaj",40},
                // Animals
                {"leptir",45},{"ptica",40},{"patka",40},{"zec",40},{"pas",35},
                {"mačka",35},{"konj",40},{"riba",35},
                // Concrete action (stronger than state description)
                {"trči",30},{"skače",30},{"pleše",30},{"peva",30},{"vozi",30},
                {"pliva",30},{"crta",30},{"gradi",30},{"kopa",30},{"baca",30},
                // Family — specific
                {"baka",35},{"deka",35},
            };

            var results = new List<(int pos, int len, int score, string query)>();
            foreach (var kv in _actionMap)
            {
                string keyLower = kv.Key.ToLower();
                int idx = lower.IndexOf(keyLower, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                bool leftOk  = idx == 0 || !char.IsLetter(lower[idx - 1]);
                bool rightOk = idx + kv.Key.Length >= lower.Length ||
                               !char.IsLetter(lower[idx + kv.Key.Length]);
                if (!leftOk || !rightOk) continue;

                // Base score = keyword length × 2 (longer = more specific)
                int score = kv.Key.Length * 2;

                // Bonus za konkretnu kategoriju
                foreach (var bonus in categoryBonus)
                    if (keyLower == bonus.Key.ToLower() || keyLower.StartsWith(bonus.Key.ToLower()))
                    { score += bonus.Value; break; }

                results.Add((idx, kv.Key.Length, score, kv.Value));
            }

            if (results.Count == 0) return null;

            // Take the highest score; on tie take the longer keyword
            return results
                .OrderByDescending(r => r.score)
                .ThenByDescending(r => r.len)
                .First().query;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetAllHardCodedMatches — svi matchevi za multi-anchor scene
        // ══════════════════════════════════════════════════════════════════════
        public static List<string> GetAllHardCodedMatches(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return new List<string>();
            string lower = lyric.ToLower();

            var results = new List<(int pos, int len, string query)>();
            foreach (var kv in _actionMap)
            {
                int idx = lower.IndexOf(kv.Key.ToLower(), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                bool leftOk  = idx == 0 || !char.IsLetter(lower[idx - 1]);
                bool rightOk = idx + kv.Key.Length >= lower.Length ||
                               !char.IsLetter(lower[idx + kv.Key.Length]);
                if (leftOk && rightOk)
                    results.Add((idx, kv.Key.Length, kv.Value));
            }

            // Deduplication — remove overlap, keep the longest at each position
            var sorted = results.OrderBy(r => r.pos).ThenByDescending(r => r.len).ToList();
            var clean = new List<(int pos, int len, string query)>();
            foreach (var r in sorted)
                if (!clean.Any(c => c.pos <= r.pos && r.pos < c.pos + c.len))
                    clean.Add(r);

            return clean.Select(r => r.query).ToList();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  GetBestMatchForLyric — one longest match + position
        // ══════════════════════════════════════════════════════════════════════
        public static (string query, int position) GetBestMatchForLyric(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return (null, -1);
            string lower = lyric.ToLower();

            (string query, int position, int keyLen) best = (null, -1, 0);
            foreach (var kv in _actionMap)
            {
                int idx = lower.IndexOf(kv.Key.ToLower(), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                bool leftOk  = idx == 0 || !char.IsLetter(lower[idx - 1]);
                bool rightOk = idx + kv.Key.Length >= lower.Length ||
                               !char.IsLetter(lower[idx + kv.Key.Length]);
                if (leftOk && rightOk && kv.Key.Length > best.keyLen)
                    best = (kv.Value, idx, kv.Key.Length);
            }
            return (best.query, best.position);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DetectAgeGroup — detektuje uzrast iz teksta pesme
        // ══════════════════════════════════════════════════════════════════════
        public static AgeGroup DetectAgeGroup(string fullLyrics, string detectedContext)
        {
            if (string.IsNullOrWhiteSpace(fullLyrics)) return AgeGroup.Kids;
            string lower = fullLyrics.ToLower();

            // Signali za malu decu (0-3)
            var toddlerSignals = new[] { "beba", "bebom", "bebi", "uspavanka", "uspavaj",
                                          "laku noć", "spavaj", "kolijevka", "kolibri" };
            if (toddlerSignals.Any(s => lower.Contains(s))) return AgeGroup.Toddler;

            // Signali za odrasle
            var adultSignals = new[] { "ljubav", "srce moje", "draga", "dragi", "volim te",
                                        "suze", "čežnja", "prošlost", "sjećanje", "sećanje",
                                        "bez tebe", "rastanak", "odlazak", "zaboravi" };
            int adultScore = adultSignals.Count(s => lower.Contains(s));
            if (adultScore >= 2) return AgeGroup.Adult;

            // Children's contexts
            var kidsContexts = new[] { "outdoor","seasons","health","fun","school",
                                        "animal","music","dance","lullaby","adventure" };
            if (kidsContexts.Contains(detectedContext?.ToLower())) return AgeGroup.Kids;

            // Default: deca
            return AgeGroup.Kids;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DetectSeasonFromLyric — detektuje sezonu IZ KONKRETNOG STIHA
        //  Poziva se per-stih, ne jednom za celu pesmu
        // ══════════════════════════════════════════════════════════════════════
        public static string DetectSeasonFromLyric(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return null;
            string lower = lyric.ToLower();

            // If the lyric mentions multiple seasons at once → do not override the global
            // "During spring and autumn, cold winters and warm summers" → null (all seasons, keep global)
            int seasonMentions = 0;
            var allSeasonWords = new[] { "zima","zime","zimi","sneg","snijeg","snegu","leto","ljeto","leti","ljeti","leta","proleće","proljeće","proleća","proljeća","jesen","jeseni" };
            foreach (var w in allSeasonWords) {
                int idx = lower.IndexOf(w);
                if (idx >= 0 && (idx == 0 || !char.IsLetter(lower[idx-1])) &&
                    (idx + w.Length >= lower.Length || !char.IsLetter(lower[idx+w.Length])))
                    seasonMentions++;
            }
            if (seasonMentions >= 2) return null; // multi-season line, keep global

            // Winter signals — specific, not ambiguous
            var winterWords = new[] {
                "zima","zime","zimi","zimski","zimskog","zimskom",
                "sneg","snegu","snehu","snijeg","snijegu","snežno","snežan",
                "čizme","rukavice","skafander","mraz","led","sanjkanje","klizanje",
                "grudvanje","grudvu","sanka"
            };
            if (winterWords.Any(w => { int i = lower.IndexOf(w); return i>=0 && (i==0||!char.IsLetter(lower[i-1])) && (i+w.Length>=lower.Length||!char.IsLetter(lower[i+w.Length])); }))
                return "winter";

            // Letnji signali
            var summerWords = new[] {
                "leto","ljeto","leti","ljeti","leta","sladoled","plaža","more","plivanje",
                "sunce","suncu","sunčano","vrućina","vruće"
            };
            if (summerWords.Any(w => { int i = lower.IndexOf(w); return i>=0 && (i==0||!char.IsLetter(lower[i-1])) && (i+w.Length>=lower.Length||!char.IsLetter(lower[i+w.Length])); }))
                return "summer";

            // Spring signals
            var springWords = new[] {
                "proleće","proljeće","proleća","proljeća","prolećem",
                "cvet","cvijet","cveće","cvijeće","livada","trava"
            };
            if (springWords.Any(w => { int i = lower.IndexOf(w); return i>=0 && (i==0||!char.IsLetter(lower[i-1])) && (i+w.Length>=lower.Length||!char.IsLetter(lower[i+w.Length])); }))
                return "spring";

            // Jesenji signali
            var autumnWords = new[] {
                "jesen","jeseni","jesenjem","lišće","list","listovi"
            };
            if (autumnWords.Any(w => { int i = lower.IndexOf(w); return i>=0 && (i==0||!char.IsLetter(lower[i-1])) && (i+w.Length>=lower.Length||!char.IsLetter(lower[i+w.Length])); }))
                return "autumn";

            return null; // stih ne pominje sezonu — zadržava globalnu
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ValidateAndFilter — finalna sanitizacija pre slanja na API
        //  This is the only point through which a query MUST pass
        // ══════════════════════════════════════════════════════════════════════
        public static string ValidateAndFilter(string query, SongContext ctx)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            var blacklist = ctx?.AgeGroup == AgeGroup.Adult ? _adultBlacklist : _childBlacklist;
            if (blacklist.Any(b => query.ToLower().Contains(b.ToLower()))) return null;

            // Max 8 words
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 8)
                query = string.Join(" ", tokens.Take(8));

            return query.Trim();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BuildOllamaSystemPrompt — jednom po pesmi, za kontekst
        // ══════════════════════════════════════════════════════════════════════
        public static string BuildOllamaSystemPrompt(SongContext ctx)
        {
            string ageRule = ctx.AgeGroup switch
            {
                AgeGroup.Toddler => "Audience: babies 0-3. Ultra gentle, colorful, simple visuals only.",
                AgeGroup.Kids    => "Audience: children 3-7. Bright, joyful, outdoor, child-safe visuals only.",
                AgeGroup.Tween   => "Audience: kids 7-12. Dynamic but age-appropriate visuals.",
                AgeGroup.Adult   => "Audience: adults. Natural, emotional, tasteful visuals.",
                _                => "Audience: children. Child-safe visuals only."
            };

            return $"You are a stock video search expert for a music video editor.\n" +
                   $"Song theme: {ctx.Theme}. Mood: {ctx.Mood}. Season: {ctx.Season}. Setting: {ctx.Setting}.\n" +
                   $"{ageRule}\n" +
                   $"For each lyric line I give you, respond with ONLY a 3-6 word English stock video search query.\n" +
                   $"Be specific and visual. Focus on the ACTION or MAIN OBJECT.\n" +
                   $"Never include adult content, violence, or anything inappropriate for the target audience.\n" +
                   $"Respond with the query only — no explanation, no punctuation at end.";
        }
    }
}
