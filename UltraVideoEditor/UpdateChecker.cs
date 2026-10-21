using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// Proverava GitHub Releases monorepo-a (Ultra-Creative-suite) za noviju verziju Video
    /// Editora. Releasi za razlicite aplikacije iz istog monorepo-a se razlikuju po prefiksu
    /// taga (npr. "video-v1.2.0" za Video Editor, drugi prefiksi za ostale Ultra aplikacije),
    /// pa se ovde eksplicitno filtrira po prefiksu - GitHub-ov "/releases/latest" endpoint bi
    /// mogao da vrati poslednji release BILO KOJE aplikacije iz repo-a, sto nije ono sto zelimo.
    /// </summary>
    public static class UpdateChecker
    {
        private const string RepoOwner = "demirajvazi10-max";
        private const string RepoName = "Ultra-Creative-suite";
        private const string TagPrefix = "video-v";

        public class UpdateInfo
        {
            public Version Version { get; set; }
            public string TagName { get; set; }
            public string HtmlUrl { get; set; }
            public string DownloadUrl { get; set; }
            /// <summary>v1.4 — expected SHA-256 of the installer (lower-case hex), or null when GitHub gave none.</summary>
            public string Sha256 { get; set; }
            /// <summary>v1.4 — address of a "&lt;installer&gt;.sha256" file published next to it, if any.</summary>
            public string Sha256Url { get; set; }
        }

        /// <summary>
        /// Vraca info o najnovijem "video-v*" release-u ako je noviji od trenutno pokrenute
        /// verzije, ili null ako je trenutna verzija vec najnovija (ili nijedan "video-v*"
        /// release nije nadjen). Baca exception ako mrezni poziv ne uspe (npr. nema interneta,
        /// ili GitHub API rate-limit) - poziva se hvata gresku na mestu poziva.
        /// </summary>
        public static async Task<UpdateInfo> CheckForUpdateAsync(Version currentVersion)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UltraVideoEditor", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.Timeout = TimeSpan.FromSeconds(15);

            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
            string json = await http.GetStringAsync(url);
            var releases = JArray.Parse(json);

            var normalizedCurrent = NormalizeForCompare(currentVersion);

            foreach (var release in releases)
            {
                string tag = release["tag_name"]?.Value<string>() ?? "";
                if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                // Preskoci draft/prerelease - korisnicima automatski saljemo samo stabilna izdanja.
                if (release["draft"]?.Value<bool>() == true) continue;
                if (release["prerelease"]?.Value<bool>() == true) continue;

                string versionPart = tag.Substring(TagPrefix.Length);
                if (!Version.TryParse(versionPart, out var releaseVersion)) continue;

                // GitHub vraca release-e po datumu opadajuce, pa je prvi "video-v*" tag na koji
                // naidjemo automatski najnoviji - ako ni on nije noviji od trenutne verzije,
                // nema potrebe gledati dalje kroz listu (stariji ionako ne mogu biti noviji).
                if (releaseVersion.CompareTo(normalizedCurrent) <= 0)
                    return null;

                string downloadUrl = null, sha = null, shaUrl = null, exeName = null;
                if (release["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        string name = asset["name"]?.Value<string>() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exeName = name;
                            downloadUrl = asset["browser_download_url"]?.Value<string>();
                            // GitHub publishes "digest": "sha256:<hex>" for every release asset
                            string digest = asset["digest"]?.Value<string>() ?? "";
                            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                sha = digest.Substring(7).Trim().ToLowerInvariant();
                            break;
                        }
                    }
                    if (exeName != null)
                        foreach (var asset in assets)
                            if (string.Equals(asset["name"]?.Value<string>(), exeName + ".sha256", StringComparison.OrdinalIgnoreCase))
                                shaUrl = asset["browser_download_url"]?.Value<string>();
                }

                return new UpdateInfo
                {
                    Version = releaseVersion,
                    TagName = tag,
                    HtmlUrl = release["html_url"]?.Value<string>(),
                    DownloadUrl = downloadUrl,
                    Sha256 = sha,
                    Sha256Url = shaUrl
                };
            }

            return null;
        }

        /// <summary>
        /// v1.4 — the downloaded installer is started only when its SHA-256 matches the one GitHub
        /// published for that release (asset digest, or the .sha256 file next to the installer).
        /// Before, any file that came down the wire was run with administrator rights.
        /// Returns null when it matches, otherwise the reason it must not be run.
        /// </summary>
        public static async Task<string> VerifyAsync(UpdateInfo update, string filePath)
        {
            string expected = update?.Sha256;
            if (string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(update?.Sha256Url))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("UltraVideoEditor");
                string text = await http.GetStringAsync(update.Sha256Url);
                expected = (text ?? "").Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            }
            if (string.IsNullOrEmpty(expected) || expected.Length != 64)
                return "no checksum was published for this release";

            using var stream = System.IO.File.OpenRead(filePath);
            using var sha = System.Security.Cryptography.SHA256.Create();
            string actual = Convert.ToHexString(await sha.ComputeHashAsync(stream)).ToLowerInvariant();
            return actual == expected ? null : "the downloaded file does not match the published checksum";
        }

        // System.Version poredi Major.Minor.Build.Revision - AssemblyVersion izveden iz
        // <Version>1.0.0</Version> u csproj-u postaje 1.0.0.0; svodimo oba na Major.Minor.Build
        // da bi poredjenje bilo tacno bez obzira na broj delova koje svaka strana ima.
        private static Version NormalizeForCompare(Version v) =>
            v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
