// ══════════════════════════════════════════════════════════════════════════════
//  MediaProviders.cs  —  Iskra Multi-Source Media Plugin System
//  UltraVideoEditor  |  Iskra Engine
//
//  ARHITEKTURA:
//  ─────────────────────────────────────────────────────────────────────────────
//  IMediaProvider  — interfejs koji svaki provajder mora implementirati
//  MediaProviderRegistry — singleton holding all registered providers
//  MediaProviderSettings — stores/loads API keys (encrypted, no code changes)
//
//  ADDING A NEW PROVIDER (4 steps, without changing existing code):
//  ─────────────────────────────────────────────────────────────────────
//  KORAK 1: Napravi klasu koja implementira IMediaProvider (vidi primjer dole)
//  KORAK 2: Registruj je u MediaProviderRegistry.RegisterDefaults() — 1 linija
//  KORAK 3: U MediaProvidersDialog.xaml dodaj UI blok (copy-paste Coverr bloka)
//  STEP 4: User enters API key via Tools → Media Providers — no code!
//
//  PRIMJER MINIMALNOG PROVAJDERA:
//  ─────────────────────────────────────────────────────────────────────
//  public class MojServisProvider : IMediaProvider
//  {
//      public string Name        => "MojServis";
//      public string Description => "Opis servisa i limiti";
//      public string ApiKeyUrl   => "https://mojservis.com/api";
//      public bool IsConfigured  => !string.IsNullOrEmpty(MediaProviderSettings.LoadKey(Name));
//
//      public async Task<List<MediaSearchResult>> SearchAsync(
//          string query, bool searchVideo, int minWidth,
//          double minDurationSec, double maxDurationSec,
//          int maxResults, CancellationToken ct)
//      {
//          string key = MediaProviderSettings.LoadKey(Name);
//          if (string.IsNullOrEmpty(key)) return new();
//          // ... implementacija API poziva ...
//          // Vrati List<MediaSearchResult> — sistem automatski filtrira kids-safe
//      }
//  }
//
//  TRENUTNO IMPLEMENTIRANI PROVAJDERI:
//  - PixabayProvider   (primarni, video + slike)
//  - PexelsProvider    (fallback, video + slike)
//  - CoverrProvider    (placeholder — besplatan, djeciji sadrzaj)
// ══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Search result — common format for all providers
    // ══════════════════════════════════════════════════════════════════════════
    public class MediaSearchResult
    {
        /// <summary>Direktni URL do video/slike fajla za preuzimanje</summary>
        public string DownloadUrl { get; set; }

        /// <summary>Stranica na kojoj se medij nalazi (za atribuciju)</summary>
        public string PageUrl { get; set; }

        /// <summary>Tagovi/opis medija (za IsHitSafe provjeru)</summary>
        public string Tags { get; set; }

        /// <summary>Trajanje video klipa u sekundama (0 za slike)</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Width in pixels</summary>
        public int Width { get; set; }

        /// <summary>Visina u pikselima</summary>
        public int Height { get; set; }

        /// <summary>Ime provajdera koji je vratio ovaj rezultat</summary>
        public string ProviderName { get; set; }

        /// <summary>True = video, False = slika</summary>
        public bool IsVideo { get; set; }

        /// <summary>Autor/fotograf (za atribuciju)</summary>
        public string Author { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IMediaProvider — interfejs koji svaki novi provajder mora implementirati
    // ══════════════════════════════════════════════════════════════════════════
    public interface IMediaProvider
    {
        /// <summary>Ime provajdera (prikazuje se u Settings panelu)</summary>
        string Name { get; }

        /// <summary>Kratki opis (za tooltip u Settings-u)</summary>
        string Description { get; }

        /// <summary>URL for registration/API key (for Settings panel)</summary>
        string ApiKeyUrl { get; }

        /// <summary>True if the provider is configured (has an API key)</summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Search for videos or images.
        /// Returns a list of results, sorted by relevance.
        /// Prazna lista = nema rezultata (ne baci exception).
        /// </summary>
        Task<List<MediaSearchResult>> SearchAsync(
            string query,
            bool searchVideo,
            int minWidth,
            double minDurationSec,
            double maxDurationSec,
            int maxResults,
            CancellationToken ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MediaProviderRegistry — Singleton, holds all registered providers
    //
    //  WATERFALL logika:
    //  1. Primary provider (Pixabay) — try
    //  2. If 0 results → next (Pexels)
    //  3. If 0 results → next (Coverr etc.)
    //  4. Ako svi failuju → Emergency pool
    // ══════════════════════════════════════════════════════════════════════════
    public class MediaProviderRegistry
    {
        private static MediaProviderRegistry _instance;
        public static MediaProviderRegistry Instance
            => _instance ??= new MediaProviderRegistry();

        private readonly List<IMediaProvider> _providers = new();

        private MediaProviderRegistry()
        {
            RegisterDefaults();
        }

        private void RegisterDefaults()
        {
            // Order = waterfall priority — built-in providers
            _providers.Add(new PixabayProvider());
            _providers.Add(new PexelsProvider());
            _providers.Add(new CoverrProvider());

            // Dynamic JSON providers from .iskraprovider files
            // Korisnik stavi fajl u folder → automatski se pojavljuje u Settings panelu
            foreach (var jp in JsonProviderLoader.LoadAll())
                _providers.Add(jp);
        }

        /// <summary>
        /// Reload dynamic providers without restarting the application.
        /// Poziva se kada korisnik doda novi .iskraprovider fajl.
        /// </summary>
        public void ReloadJsonProviders()
        {
            _providers.RemoveAll(p => p is JsonMediaProvider);
            foreach (var jp in JsonProviderLoader.LoadAll())
                _providers.Add(jp);
        }

        /// <summary>Svi registrovani provajderi (za Settings panel).</summary>
        public IReadOnlyList<IMediaProvider> All => _providers;

        /// <summary>Only configured ones (have an API key).</summary>
        public IReadOnlyList<IMediaProvider> Configured
            => _providers.Where(p => p.IsConfigured).ToList();

        /// <summary>
        /// Waterfall search: tries providers in order until a result is obtained.
        /// Returns (result, providerName) or (null, null) if all fail.
        /// </summary>
        public async Task<(MediaSearchResult result, string providerName)> SearchWaterfallAsync(
            string query,
            bool searchVideo,
            int minWidth,
            double minDurationSec,
            double maxDurationSec,
            int maxResults = 30,
            CancellationToken ct = default,
            Action<string> log = null)
        {
            foreach (var provider in Configured)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var results = await provider.SearchAsync(
                        query, searchVideo, minWidth,
                        minDurationSec, maxDurationSec, maxResults, ct);

                    // Kids-Safe filter na rezultatima
                    var safe = results
                        .Where(r => IskraKidsSafeQuery.IsHitSafe(r.Tags?.ToLower() ?? ""))
                        .ToList();

                    if (safe.Count > 0)
                    {
                        // Take a random one from the first 10 (variety)
                        var pick = safe[new Random().Next(Math.Min(10, safe.Count))];
                        log?.Invoke($"   ✅ [{provider.Name}] Nadjen: {pick.Tags?.Substring(0, Math.Min(40, pick.Tags?.Length ?? 0))}");
                        return (pick, provider.Name);
                    }

                    log?.Invoke($"   ⏭ [{provider.Name}] 0 kids-safe results — trying next");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"   ❌ [{provider.Name}] Error: {ex.Message.Substring(0, Math.Min(60, ex.Message.Length))}");
                }
            }
            return (null, null);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MediaProviderSettings — Stores/loads API keys without changing code
    //  Koristi isti Windows DPAPI mehanizam kao stari GetPixabayApiKey()
    // ══════════════════════════════════════════════════════════════════════════
    public static class MediaProviderSettings
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraVideoEditor", "Providers");

        /// <summary>Load API key for the given provider.</summary>
        public static string LoadKey(string providerName)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string safeName = MakeSafe(providerName);

                // Try encrypted .bin file
                string binPath = Path.Combine(SettingsDir, $"{safeName}.bin");
                if (File.Exists(binPath))
                {
                    byte[] enc = File.ReadAllBytes(binPath);
                    byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dec).Trim();
                }

                // Fallback: .txt file (for manual setup)
                string txtPath = Path.Combine(SettingsDir, $"{safeName}.txt");
                if (File.Exists(txtPath))
                    return File.ReadAllText(txtPath).Trim();
            }
            catch { }
            return null;
        }

        /// <summary>Save API key for the given provider (encrypted).</summary>
        public static bool SaveKey(string providerName, string key)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                string safeName = MakeSafe(providerName);
                string binPath = Path.Combine(SettingsDir, $"{safeName}.bin");
                byte[] data = Encoding.UTF8.GetBytes(key.Trim());
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(binPath, enc);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Delete API key (deactivates provider).</summary>
        public static void DeleteKey(string providerName)
        {
            string safeName = MakeSafe(providerName);
            foreach (var ext in new[] { ".bin", ".txt" })
            {
                string path = Path.Combine(SettingsDir, safeName + ext);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>Returns a list of all providers that have a saved key.</summary>
        public static List<string> GetConfiguredProviders()
        {
            if (!Directory.Exists(SettingsDir)) return new();
            return Directory.GetFiles(SettingsDir, "*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList();
        }

        private static string MakeSafe(string name)
            => name.ToLower().Replace(" ", "_").Replace("/", "").Replace("\\", "");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PixabayProvider — Implementacija za Pixabay API
    // ══════════════════════════════════════════════════════════════════════════
    public class PixabayProvider : IMediaProvider
    {
        public string Name        => "Pixabay";
        public string Description => "Free video and images. Fastest, 5000 req/hour.";
        public string ApiKeyUrl   => "https://pixabay.com/api/docs/";

        public bool IsConfigured
        {
            get
            {
                string k = MediaProviderSettings.LoadKey(Name);
                return !string.IsNullOrWhiteSpace(k);
            }
        }

        public async Task<List<MediaSearchResult>> SearchAsync(
            string query, bool searchVideo, int minWidth,
            double minDurationSec, double maxDurationSec,
            int maxResults, CancellationToken ct)
        {
            string key = MediaProviderSettings.LoadKey(Name);
            if (string.IsNullOrEmpty(key)) return new();

            // Trim query na 100 znakova (Pixabay preporuka)
            string q = query.Length > 100 ? query.Substring(0, 97).TrimEnd() : query;
            q = Uri.EscapeDataString(q);

            string url = searchVideo
                ? $"https://pixabay.com/api/videos/?key={key}&q={q}&safesearch=true&per_page={maxResults}&video_type=film&min_duration={(int)minDurationSec}&max_duration={(int)maxDurationSec}"
                : $"https://pixabay.com/api/?key={key}&q={q}&image_type=photo&safesearch=true&per_page={maxResults}&min_width={minWidth}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string json = await http.GetStringAsync(url, ct);
            var root = JObject.Parse(json);
            var hits = root["hits"] as JArray;
            if (hits == null) return new();

            var results = new List<MediaSearchResult>();
            foreach (var hit in hits)
            {
                if (searchVideo)
                {
                    // Odaberi najboljу rezoluciju
                    var videos = hit["videos"];
                    string dlUrl = videos?["large"]?["url"]?.ToString()
                                ?? videos?["medium"]?["url"]?.ToString()
                                ?? videos?["small"]?["url"]?.ToString();
                    if (string.IsNullOrEmpty(dlUrl)) continue;

                    results.Add(new MediaSearchResult
                    {
                        DownloadUrl     = dlUrl,
                        PageUrl         = hit["pageURL"]?.ToString(),
                        Tags            = hit["tags"]?.ToString(),
                        DurationSeconds = hit["duration"]?.Value<double>() ?? 0,
                        Width           = videos?["large"]?["width"]?.Value<int>() ?? 0,
                        Height          = videos?["large"]?["height"]?.Value<int>() ?? 0,
                        ProviderName    = Name,
                        IsVideo         = true,
                        Author          = hit["user"]?.ToString()
                    });
                }
                else
                {
                    string dlUrl = hit["largeImageURL"]?.ToString()
                                ?? hit["webformatURL"]?.ToString();
                    if (string.IsNullOrEmpty(dlUrl)) continue;

                    results.Add(new MediaSearchResult
                    {
                        DownloadUrl  = dlUrl,
                        PageUrl      = hit["pageURL"]?.ToString(),
                        Tags         = hit["tags"]?.ToString(),
                        Width        = hit["imageWidth"]?.Value<int>() ?? 0,
                        Height       = hit["imageHeight"]?.Value<int>() ?? 0,
                        ProviderName = Name,
                        IsVideo      = false,
                        Author       = hit["user"]?.ToString()
                    });
                }
            }
            return results;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PexelsProvider — Implementacija za Pexels API
    //  API Format: Authorization header (ne query parametar)
    //  Rate limit: 200 req/sat, 20000 req/mjesec
    // ══════════════════════════════════════════════════════════════════════════
    public class PexelsProvider : IMediaProvider
    {
        public string Name        => "Pexels";
        public string Description => "Free video and images. Excellent fallback for Pixabay. 200 req/hour.";
        public string ApiKeyUrl   => "https://www.pexels.com/api/new/";

        public bool IsConfigured
        {
            get
            {
                string k = MediaProviderSettings.LoadKey(Name);
                return !string.IsNullOrWhiteSpace(k);
            }
        }

        public async Task<List<MediaSearchResult>> SearchAsync(
            string query, bool searchVideo, int minWidth,
            double minDurationSec, double maxDurationSec,
            int maxResults, CancellationToken ct)
        {
            string key = MediaProviderSettings.LoadKey(Name);
            if (string.IsNullOrEmpty(key)) return new();

            // Pexels: Authorization header, ne query parametar
            string q = Uri.EscapeDataString(
                query.Length > 100 ? query.Substring(0, 97).TrimEnd() : query);

            // Pexels per_page max = 80
            int perPage = Math.Min(maxResults, 80);

            string url = searchVideo
                ? $"https://api.pexels.com/videos/search?query={q}&per_page={perPage}&min_duration={(int)minDurationSec}&max_duration={(int)maxDurationSec}&size=medium"
                : $"https://api.pexels.com/v1/search?query={q}&per_page={perPage}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // Pexels koristi Authorization header
            http.DefaultRequestHeaders.Add("Authorization", key);

            string json = await http.GetStringAsync(url, ct);
            var root = JObject.Parse(json);

            var results = new List<MediaSearchResult>();

            if (searchVideo)
            {
                var videos = root["videos"] as JArray;
                if (videos == null) return new();

                foreach (var v in videos)
                {
                    // Odaberi best quality video file
                    var files = v["video_files"] as JArray;
                    if (files == null) continue;

                    // Prefer HD (1920x1080) or largest available
                    var best = files
                        .OrderByDescending(f => f["width"]?.Value<int>() ?? 0)
                        .FirstOrDefault(f => (f["width"]?.Value<int>() ?? 0) >= minWidth);
                    best ??= files.OrderByDescending(f => f["width"]?.Value<int>() ?? 0).FirstOrDefault();

                    string dlUrl = best?["link"]?.ToString();
                    if (string.IsNullOrEmpty(dlUrl)) continue;

                    // Pexels nema "tags" — gradimo iz opisa/user_id
                    string tags = v["user"]?["name"]?.ToString() ?? "";

                    results.Add(new MediaSearchResult
                    {
                        DownloadUrl     = dlUrl,
                        PageUrl         = v["url"]?.ToString(),
                        Tags            = tags,
                        DurationSeconds = v["duration"]?.Value<double>() ?? 0,
                        Width           = best?["width"]?.Value<int>() ?? 0,
                        Height          = best?["height"]?.Value<int>() ?? 0,
                        ProviderName    = Name,
                        IsVideo         = true,
                        Author          = v["user"]?["name"]?.ToString()
                    });
                }
            }
            else
            {
                var photos = root["photos"] as JArray;
                if (photos == null) return new();

                foreach (var p in photos)
                {
                    string dlUrl = p["src"]?["large2x"]?.ToString()
                                ?? p["src"]?["large"]?.ToString()
                                ?? p["src"]?["original"]?.ToString();
                    if (string.IsNullOrEmpty(dlUrl)) continue;

                    results.Add(new MediaSearchResult
                    {
                        DownloadUrl  = dlUrl,
                        PageUrl      = p["url"]?.ToString(),
                        Tags         = p["alt"]?.ToString() ?? "",
                        Width        = p["width"]?.Value<int>() ?? 0,
                        Height       = p["height"]?.Value<int>() ?? 0,
                        ProviderName = Name,
                        IsVideo      = false,
                        Author       = p["photographer"]?.ToString()
                    });
                }
            }
            return results;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  CoverrProvider — Placeholder za Coverr.co
    //  Free, specialized for lifestyle/children's content
    //  API dokumentacija: https://coverr.co/api
    // ══════════════════════════════════════════════════════════════════════════
    public class CoverrProvider : IMediaProvider
    {
        public string Name        => "Coverr";
        public string Description => "Besplatni lifestyle video. Odlican za djeciji sadrzaj. Nema rate limita.";
        public string ApiKeyUrl   => "https://coverr.co/api";

        public bool IsConfigured
        {
            get
            {
                string k = MediaProviderSettings.LoadKey(Name);
                return !string.IsNullOrWhiteSpace(k);
            }
        }

        public async Task<List<MediaSearchResult>> SearchAsync(
            string query, bool searchVideo, int minWidth,
            double minDurationSec, double maxDurationSec,
            int maxResults, CancellationToken ct)
        {
            string key = MediaProviderSettings.LoadKey(Name);
            if (string.IsNullOrEmpty(key)) return new();

            // Coverr podrzava samo video (nema image API)
            if (!searchVideo) return new();

            // Coverr API: api_key kao query parametar ILI Authorization header
            // Dokumentacija: https://api.coverr.co/docs/videos/
            string q = Uri.EscapeDataString(
                query.Length > 100 ? query.Substring(0, 97).TrimEnd() : query);

            string url = $"https://api.coverr.co/videos?api_key={key}&query={q}&page_size={maxResults}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // Coverr podrzava i Authorization header kao alternativu
            http.DefaultRequestHeaders.Add("Authorization", key);

            string json = await http.GetStringAsync(url, ct);
            var root = JObject.Parse(json);
            var hits = root["hits"] as JArray;
            if (hits == null) return new();

            var results = new List<MediaSearchResult>();
            foreach (var v in hits)
            {
                // Coverr response format
                var mp4 = v["mp4"];
                string dlUrl = mp4?["url"]?.ToString()    // full HD
                            ?? mp4?["preview"]?.ToString(); // fallback preview
                if (string.IsNullOrEmpty(dlUrl)) continue;

                double dur = v["duration"]?.Value<double>() ?? 0;
                if (dur > 0 && dur < minDurationSec) continue;
                if (dur > 0 && dur > maxDurationSec) continue;

                // Coverr nema "tags" — koristimo title i description
                string tags = $"{v["title"]} {v["description"]}".Trim();

                results.Add(new MediaSearchResult
                {
                    DownloadUrl     = dlUrl,
                    PageUrl         = $"https://coverr.co/videos/{v["slug"]}",
                    Tags            = tags,
                    DurationSeconds = dur,
                    Width           = v["resolution"]?["width"]?.Value<int>() ?? 0,
                    Height          = v["resolution"]?["height"]?.Value<int>() ?? 0,
                    ProviderName    = Name,
                    IsVideo         = true,
                    Author          = "Coverr" // Coverr zahtijeva atribuciju
                });
            }
            return results;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MediaProviderMigrator — Migration of old Pixabay keys to the new system
    //  Poziva se jednom pri startu aplikacije
    // ══════════════════════════════════════════════════════════════════════════
    public static class MediaProviderMigrator
    {
        /// <summary>
        /// Reads old pixabay_key.bin/.txt and migrates to the new Provider system.
        /// Safe for multiple calls (does not migrate again if already exists).
        /// </summary>
        public static void MigrateIfNeeded()
        {
            // If Pixabay already has a key in the new system, no need
            if (!string.IsNullOrEmpty(MediaProviderSettings.LoadKey("Pixabay")))
                return;

            try
            {
                string oldDir  = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor");
                string binPath = Path.Combine(oldDir, "pixabay_key.bin");
                string txtPath = Path.Combine(oldDir, "pixabay_key.txt");

                string key = null;

                if (File.Exists(binPath))
                {
                    byte[] enc = File.ReadAllBytes(binPath);
                    byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                    key = Encoding.UTF8.GetString(dec).Trim();
                }
                else if (File.Exists(txtPath))
                {
                    key = File.ReadAllText(txtPath).Trim();
                }

                if (!string.IsNullOrEmpty(key))
                    MediaProviderSettings.SaveKey("Pixabay", key);
            }
            catch { }
        }
    }
    // ══════════════════════════════════════════════════════════════════════════
    //  JsonProviderDefinition — model za .iskraprovider fajlove
    // ══════════════════════════════════════════════════════════════════════════
    public class JsonProviderDefinition
    {
        public string Name            { get; set; }
        public string Description     { get; set; }
        public string ApiKeyUrl       { get; set; }
        public string SearchVideoUrl  { get; set; }
        public string SearchImageUrl  { get; set; }

        // Auth: "header" | "queryParam"
        public string AuthType        { get; set; } = "queryParam";
        public string AuthParamName   { get; set; } = "api_key";

        // Naziv query parametra za pretragu
        public string QueryParam      { get; set; } = "q";

        // Dodatni fiksni parametri (npr. safesearch=true)
        public Dictionary<string, string> ExtraParams { get; set; } = new();

        // Mapiranje JSON odgovora → MediaSearchResult polja
        // Format: "putanja.do.polja" (npr. "hits.0.url" ili "videos.large.url")
        public JsonResponseMap ResponseMap { get; set; } = new();
    }

    public class JsonResponseMap
    {
        /// <summary>Put do niza rezultata u JSON odgovoru. npr. "hits" ili "videos"</summary>
        public string HitsArray    { get; set; } = "hits";

        /// <summary>Put do URL-a za preuzimanje unutar jednog hita</summary>
        public string DownloadUrl  { get; set; } = "videos.large.url";

        /// <summary>Put do tagova/opisa</summary>
        public string Tags         { get; set; } = "tags";

        /// <summary>Put do trajanja u sekundama</summary>
        public string Duration     { get; set; } = "duration";

        /// <summary>Path to width</summary>
        public string Width        { get; set; } = "width";

        /// <summary>Put do visine</summary>
        public string Height       { get; set; } = "height";

        /// <summary>Put do URL-a stranice (za atribuciju)</summary>
        public string PageUrl      { get; set; } = "pageURL";

        /// <summary>Put do imena autora</summary>
        public string Author       { get; set; } = "user";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  JsonMediaProvider — Dynamic provider loaded from .iskraprovider file
    //
    //  Average user:
    //  1. Dobije/preuzme "NekiServis.iskraprovider" fajl
    //  2. Stavi ga u %AppData%\UltraVideoEditor\Providers\
    //  3. Restartuje Iskru
    //  4. Enter API key via Tools → Media Providers
    //  Nula koda, nula Visual Studio-a.
    // ══════════════════════════════════════════════════════════════════════════
    public class JsonMediaProvider : IMediaProvider
    {
        private readonly JsonProviderDefinition _def;

        public JsonMediaProvider(JsonProviderDefinition def) => _def = def;

        public string Name        => _def.Name;
        public string Description => _def.Description;
        public string ApiKeyUrl   => _def.ApiKeyUrl;
        public bool IsConfigured  => !string.IsNullOrEmpty(MediaProviderSettings.LoadKey(Name));

        public async Task<List<MediaSearchResult>> SearchAsync(
            string query, bool searchVideo, int minWidth,
            double minDurationSec, double maxDurationSec,
            int maxResults, CancellationToken ct)
        {
            string key = MediaProviderSettings.LoadKey(Name);
            if (string.IsNullOrEmpty(key)) return new();

            string baseUrl = searchVideo
                ? (_def.SearchVideoUrl ?? _def.SearchImageUrl)
                : (_def.SearchImageUrl ?? _def.SearchVideoUrl);

            if (string.IsNullOrEmpty(baseUrl)) return new();

            // Sastavi query string
            string q = Uri.EscapeDataString(
                query.Length > 100 ? query.Substring(0, 97).TrimEnd() : query);

            var urlParams = new System.Text.StringBuilder(baseUrl);
            urlParams.Append(baseUrl.Contains('?') ? '&' : '?');
            urlParams.Append($"{_def.QueryParam}={q}");
            urlParams.Append($"&per_page={maxResults}");

            // Dodaj ekstra parametre iz definicije
            foreach (var kv in _def.ExtraParams)
                urlParams.Append($"&{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");

            // Auth: queryParam mode
            if (_def.AuthType?.ToLower() == "queryparam")
                urlParams.Append($"&{_def.AuthParamName}={Uri.EscapeDataString(key)}");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // Auth: header mode
            if (_def.AuthType?.ToLower() == "header")
                http.DefaultRequestHeaders.Add(_def.AuthParamName, key);

            string json = await http.GetStringAsync(urlParams.ToString(), ct);
            var root    = JObject.Parse(json);

            // Navigiraj do niza rezultata
            var hitsToken = NavigatePath(root, _def.ResponseMap.HitsArray);
            var hits      = hitsToken as JArray ?? (hitsToken != null ? new JArray(hitsToken) : null);
            if (hits == null) return new();

            var results = new List<MediaSearchResult>();
            foreach (var hit in hits)
            {
                string dlUrl = NavigatePath(hit, _def.ResponseMap.DownloadUrl)?.ToString();
                if (string.IsNullOrEmpty(dlUrl)) continue;

                double dur = 0;
                double.TryParse(NavigatePath(hit, _def.ResponseMap.Duration)?.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out dur);

                if (dur > 0 && dur < minDurationSec) continue;
                if (dur > 0 && dur > maxDurationSec) continue;

                int.TryParse(NavigatePath(hit, _def.ResponseMap.Width)?.ToString(), out int w);
                int.TryParse(NavigatePath(hit, _def.ResponseMap.Height)?.ToString(), out int h);

                if (w > 0 && w < minWidth) continue;

                results.Add(new MediaSearchResult
                {
                    DownloadUrl     = dlUrl,
                    PageUrl         = NavigatePath(hit, _def.ResponseMap.PageUrl)?.ToString(),
                    Tags            = NavigatePath(hit, _def.ResponseMap.Tags)?.ToString() ?? "",
                    DurationSeconds = dur,
                    Width           = w,
                    Height          = h,
                    ProviderName    = Name,
                    IsVideo         = searchVideo,
                    Author          = NavigatePath(hit, _def.ResponseMap.Author)?.ToString()
                });
            }
            return results;
        }

        // Navigira JSON path "polje.podpolje.vrednost" ili "polje[0].url"
        private static JToken NavigatePath(JToken token, string path)
        {
            if (string.IsNullOrEmpty(path) || token == null) return null;
            JToken current = token;
            foreach (var part in path.Split('.'))
            {
                if (current == null) break;
                // Provjeri indeks npr. "hits[0]"
                var bracketIdx = part.IndexOf('[');
                if (bracketIdx > 0)
                {
                    string field = part.Substring(0, bracketIdx);
                    string idxStr = part.Substring(bracketIdx + 1, part.Length - bracketIdx - 2);
                    current = current[field];
                    if (int.TryParse(idxStr, out int idx)) current = current?[idx];
                }
                else
                {
                    current = current[part];
                }
            }
            return current;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  JsonProviderLoader — Scans the Providers folder and loads .iskraprovider files
    // ══════════════════════════════════════════════════════════════════════════
    public static class JsonProviderLoader
    {
        private static readonly string ProvidersDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraVideoEditor", "Providers");

        /// <summary>
        /// Loads all .iskraprovider files from the Providers folder.
        /// Poziva se iz MediaProviderRegistry.RegisterDefaults().
        /// </summary>
        public static List<JsonMediaProvider> LoadAll()
        {
            var result = new List<JsonMediaProvider>();
            if (!Directory.Exists(ProvidersDir)) return result;

            foreach (var file in Directory.GetFiles(ProvidersDir, "*.iskraprovider"))
            {
                try
                {
                    string json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    var def = Newtonsoft.Json.JsonConvert.DeserializeObject<JsonProviderDefinition>(json);
                    if (def != null && !string.IsNullOrWhiteSpace(def.Name))
                    {
                        result.Add(new JsonMediaProvider(def));
                    }
                }
                catch
                {
                    // Silently skip damaged files — do not crash the application
                }
            }
            return result;
        }

        /// <summary>Folder gdje korisnik stavlja .iskraprovider fajlove.</summary>
        public static string GetProvidersFolder()
        {
            Directory.CreateDirectory(ProvidersDir);
            return ProvidersDir;
        }
    }


}
