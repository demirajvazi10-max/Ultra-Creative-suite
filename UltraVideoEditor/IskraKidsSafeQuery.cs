// ══════════════════════════════════════════════════════════════════════════════
//  IskraKidsSafeQuery.cs
//  UltraVideoEditor | Iskra Engine
//
//  Pravilo 3 — Kids-Safe Semantics za Pixabay/Pexels API pretrage.
//  Ovaj fajl je novi dodatak projektu. Ne mijenja postojeće fajlove.
//  Koristi se iz AIVideoCreator.xaml.cs umjesto SanitizeQueryForChildren().
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// Gradi Pixabay/Pexels query stringove koji su 100% bezbedni za djecu 3-7g.
    ///
    /// PRAVILO 3 — tri sloja zaštite:
    ///   Sloj 1 — Pozitivni sufiks: " bright sunny happy kids colorful" na svakom upitu
    ///   Sloj 2 — Hard-block: 80+ zabranjenih kategorija uklonjeno iz samog upita
    ///   Sloj 3 — IsHitSafe(): provjera Pixabay tags stringova PRIJE preuzimanja
    ///
    /// Sezonsko zaključavanje:
    ///   Ako stih pominje "ljeto" → query zaključan na sunny/beach/park, NIKAD snow.
    ///   Ako stih pominje "zima" → query zaključan na snow/cozy, NIKAD tropical.
    /// </summary>
    public static class IskraKidsSafeQuery
    {
        // ── Sloj 1: Pozitivni sufiks — uvijek dodan na svaki upit ─────────────
        // Kratak suffix — Pixabay VIDEO API prihvata max ~100 znakova u q=
    // Dug suffix (+ negativni termini) uzrokuje HTTP 400 Bad Request
    private const string KIDS_POSITIVE_SUFFIX = " kids sunny";

        // ── Sloj 2: Hard-block termini — uklanjaju se iz query stringa ────────
        private static readonly HashSet<string> _queryBlockTerms =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Medicinsko / trudnoća
            "pregnant", "pregnancy", "maternity", "prenatal", "ultrasound",
            "embryo", "fetus", "womb", "obstetric", "sonogram", "medical",
            "clinic", "surgery", "hospital", "anatomy", "anatomical",
            "nude", "naked", "nudity", "bare skin", "belly", "abdomen",
            "torso", "abdominal", "stomach anatomy",
            // Opasno
            "dark", "scary", "horror", "violence", "weapon", "gun", "knife",
            "death", "cemetery", "graveyard", "funeral", "ghost", "blood",
            // Gym / fitness
            "gym", "fitness", "workout", "kettlebell", "dumbbell", "barbell",
            "weightlifting", "bodybuilding", "crossfit", "treadmill",
            "plank", "pushup", "situp", "squat", "deadlift", "hiit", "aerobic",
            // Egzotične životinje / Afrika
            "elephant", "savanna", "safari", "africa", "zebra", "giraffe",
            "hippo", "rhinoceros", "cheetah", "leopard", "camel", "sahara",
            // Neprikladno ponašanje životinja
            "monkey grooming", "baboon", "mating", "fighting animals",
            // Audio / DJ oprema
            "gramophone", "turntable", "vinyl", "dj mixer", "mixing console",
            "recording studio", "synthesizer", "amplifier",
            // Animirano / digitalno
            "animation", "cartoon", "3d render", "ai generated", "abstract",
            "illustration", "vector", "silhouette", "black and white",
        };

        // ── Sloj 3: Zabranjeni Pixabay tag termini (za IsHitSafe) ─────────────
        private static readonly HashSet<string> _hitBlockTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Medicinsko / trudnoća
            "pregnant", "pregnancy", "maternity", "prenatal", "ultrasound",
            "embryo", "fetus", "womb", "obstetric", "sonogram",
            "medical", "clinic", "surgery", "hospital",
            "anatomy", "anatomical", "nude", "naked", "nudity",
            "belly", "abdomen", "torso", "abdominal", "bare skin",
            // Opasno / strašno
            "dark", "scary", "horror", "violence", "weapon", "gun", "knife",
            "death", "cemetery", "graveyard", "funeral", "ghost", "blood", "gore",
            // Gym / fitness
            "gym", "fitness", "workout", "kettlebell", "dumbbell", "barbell",
            "weightlifting", "bodybuilding", "crossfit", "treadmill",
            "plank", "pushup", "situp", "squat", "deadlift", "hiit",
            "aerobic", "exercise machine", "bench press", "pull-up",
            // Egzotične životinje
            "elephant", "savanna", "safari", "africa", "african wildlife",
            "zebra", "giraffe", "hippopotamus", "rhinoceros", "cheetah", "leopard",
            "camel", "sahara", "serengeti",
            // Neprikladno ponašanje životinja
            "monkey grooming", "baboon", "animal mating", "animals fighting",
            // Audio / DJ oprema
            "gramophone", "turntable", "vinyl record", "dj mixer",
            "mixing console", "recording studio", "synthesizer",
            // Odrasli bez djece
            "solo woman", "woman alone", "adult portrait", "model",
            "teenager", "teen", "young adult", "lifestyle influencer",
            "dating", "romance adult", "couple", "intimate",
            // Animirano / digitalno
            "animation", "cartoon", "3d render", "ai generated", "abstract art",
            "illustration", "vector", "silhouette", "black and white photo",
            // Medicinsko oprema
            "hospital gown", "examination table", "medical scan", "body scan",
            "stethoscope", "syringe", "injection", "pill", "medicine",
            // ── NOVO: Apstraktni i konceptualni studio materijal ─────────────
            "abstract", "lava lamp", "lava", "fluid art", "liquid abstract",
            "paint dripping", "ink water", "color explosion", "paint splash",
            "macro", "extreme macro", "microscopic", "close-up insect",
            "texture background", "seamless pattern", "studio isolated",
            "white background", "product isolated", "object isolated",
            "slowmotion liquid", "high speed liquid",
            // ── NOVO: Insekat / puž / gmizavac bez konteksta djece ───────────
            "snail", "slug", "worm", "ant closeup", "spider", "cobweb",
            "fly closeup", "beetle", "cockroach", "bug macro", "insect macro",
            "caterpillar closeup", "lizard", "snake", "reptile",
            // ── NOVO: Kućni ljubimci bez djece ───────────────────────────────
            "cat portrait", "cat alone", "sleeping cat", "cat resting",
            "cat lying", "lazy cat", "dog portrait", "dog alone", "pet sleeping",
            // ── NOVO: Nasumične ptice na vodi ────────────────────────────────
            "ducks water", "duck pond", "duck feeding", "geese water",
            "bird feeding", "pigeon", "seagull flock", "crow",
        };

        // ── Sezonski maperi — zaključavaju query na odgovarajuće kadrove ───────
        private static readonly Dictionary<string, string> _seasonForcePositive =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ljeto"]    = "summer sunny beach children outdoor bright",
            ["leto"]     = "summer sunny park children outdoor bright",
            ["proleće"]  = "spring flowers park children sunny bright",
            ["proljeće"] = "spring flowers garden children bright",
            ["jesen"]    = "autumn leaves park children golden colorful",
            ["zima"]     = "winter snow children playing cozy warm",
        };

        // Termini koji se moraju UKLONITI za datu sezonu (suprotne sezone)
        private static readonly Dictionary<string, string[]> _seasonBlockOpposite =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ljeto"]    = new[] { "snow", "winter", "ice", "frost", "blizzard" },
            ["leto"]     = new[] { "snow", "winter", "ice", "frost", "blizzard" },
            ["proleće"]  = new[] { "snow", "blizzard", "ice", "frozen" },
            ["proljeće"] = new[] { "snow", "blizzard", "ice", "frozen" },
            ["jesen"]    = new[] { "snow", "blizzard", "tropical", "beach summer" },
            ["zima"]     = new[] { "beach", "tropical", "summer heat", "scorching" },
        };

        // Medicinski termini koji zamjenjuju cijeli query kad se nađu
        private static readonly Dictionary<string, string> _medicalReplacements =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "body",       "child active outdoor" },
            { "stomach",    "child happy laughing" },
            { "belly",      "child outdoor playing" },
            { "abdomen",    "child outdoor active" },
            { "torso",      "child outdoor active" },
            { "anatomy",    "children nature outdoor" },
            { "medical",    "children outdoor healthy" },
            { "pregnant",   "children park family outdoor" },
            { "pregnancy",  "family outdoor park nature" },
            { "prenatal",   "children outdoor sunny park" },
            { "ultrasound", "children nature park outdoor" },
            { "fetus",      "children outdoor sunny" },
            { "womb",       "children outdoor park" },
            { "clinic",     "children outdoor park sunny" },
            { "hospital",   "children outdoor park nature" },
            { "surgery",    "children outdoor park playing" },
            { "nude",       "children outdoor park happy" },
            { "naked",      "children outdoor playing happy" },
            { "muscles",    "children jumping playing outdoor" },
            { "strong man", "children active outdoor park" },
        };

        // ── Javni API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Gradi Pixabay/Pexels query string koji je 100% bezbedan za djecu 3-7g.
        /// </summary>
        /// <param name="rawKeywords">AI-generisani keywords za scenu</param>
        /// <param name="detectedSeason">Sezona iz analize pjesme (spring/summer/autumn/winter/none)</param>
        /// <param name="context">Kontekst scene (outdoor/lullaby/party...)</param>
        /// <param name="isSeasonLocked">True kada stih eksplicitno pominje godišnje doba</param>
        public static string Build(
            string rawKeywords,
            string detectedSeason = "none",
            string context = "fun",
            bool isSeasonLocked = false)
        {
            if (string.IsNullOrWhiteSpace(rawKeywords))
                rawKeywords = "children playing park outdoor";

            // Sloj 1: Zamijeni medicinske/anatomske termine sigurnim alternativama
            rawKeywords = ApplyMedicalReplacement(rawKeywords);

            // Sloj 2: Sezonsko zaključavanje — ako stih pominje sezonu
            if (isSeasonLocked)
            {
                foreach (var kv in _seasonForcePositive)
                {
                    if (rawKeywords.ToLower().Contains(kv.Key.ToLower()))
                    {
                        rawKeywords = kv.Value;
                        break;
                    }
                }
            }

            // Sloj 3: Ukloni hard-block termine iz samog upita
            var tokens = rawKeywords
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !_queryBlockTerms.Contains(t))
                .ToList();

            // Sloj 4: Ukloni suprotnu sezonu iz tokena
            if (!string.IsNullOrEmpty(detectedSeason) && detectedSeason != "none")
            {
                string seasonKey = MapSeasonKey(detectedSeason);
                if (!string.IsNullOrEmpty(seasonKey) &&
                    _seasonBlockOpposite.TryGetValue(seasonKey, out string[] blocked))
                {
                    tokens = tokens
                        .Where(t => !blocked.Any(b =>
                            t.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();
                }
            }

            // Sloj 5: Sastavi query — max 8 tokena + kids-positive sufiks
            string query = string.Join(" ",
                tokens.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));

            // Sloj 6: Dodaj globalni kids-safe pozitivni sufiks
            query = query.TrimEnd() + KIDS_POSITIVE_SUFFIX;

            return query;
        }

        /// <summary>
        /// Provjerava da li Pixabay hit (tags string) sadrži zabranjeni sadržaj.
        /// Vraća true ako je hit SIGURAN za djecu (tj. može se preuzeti).
        /// </summary>
        public static bool IsHitSafe(string pixabayTagsLower)
        {
            if (string.IsNullOrWhiteSpace(pixabayTagsLower)) return true;
            // Optimizacija: direktna Contains provjera umjesto HashSet Any()
            foreach (var tag in _hitBlockTags)
                if (pixabayTagsLower.Contains(tag))
                    return false;
            return true;
        }

        /// <summary>
        /// Provjera da li tekst lirika pominje godišnje doba.
        /// Koristi se za isSeasonLocked parametar u Build().
        /// </summary>
        public static bool IsSeasonInLyric(string lyric)
        {
            if (string.IsNullOrWhiteSpace(lyric)) return false;
            string lower = lyric.ToLower();
            return _seasonForcePositive.Keys.Any(k => lower.Contains(k.ToLower()));
        }

        // ── Privatni helperi ───────────────────────────────────────────────────

        private static string ApplyMedicalReplacement(string query)
        {
            string lower = query.ToLower();
            foreach (var kv in _medicalReplacements)
                if (lower.Contains(kv.Key.ToLower()))
                    return kv.Value + " outdoor sunny";
            return query;
        }

        private static string MapSeasonKey(string season)
        {
            // Mapira engleske sezone na BHS ključeve za _seasonBlockOpposite
            return season?.ToLower() switch
            {
                "summer" => "ljeto",
                "spring" => "proleće",
                "autumn" or "fall" => "jesen",
                "winter" => "zima",
                _ => season
            };
        }
    }
}
