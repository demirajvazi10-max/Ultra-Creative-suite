using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace UltraVideoEditor
{
    public class RenderEngine
    {
        private string _ffmpegPath;

        private static string _LangCode => (WpfApp.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public RenderEngine(bool useHardwareAcceleration = true)
        {
            _ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");
        }

        public async Task RenderSimpleAsync(
            List<TimelineItem> items,
            string outputPath,
            string format,
            IProgress<int> progress,
            List<SubtitleItem> subtitles = null,
            ExportSettingsData exportSettings = null,
            CancellationToken cancellationToken = default,
            bool useGPU = true,
            string resolution = "1920x1080",
            bool fastRender = false,
            bool enableSubtitles = true,
            BeatInfo beatInfo = null)  // BEAT-SYNC: DownBeat snap za rezove
        {
            LogToMainWindow(L("re_starting"));

            bool nvencAvailable = false;
            if (useGPU)
            {
                try
                {
                    string testArgs = $"-f lavfi -i color=c=black:s=64x64:d=0.1 -c:v h264_nvenc -f null -";
                    string testOut = await RunFFmpegGetOutputAsync(testArgs, CancellationToken.None);
                    nvencAvailable = !testOut.Contains("No NVENC capable devices") &&
                                      !testOut.Contains("Cannot load") &&
                                      !testOut.Contains("Unknown encoder");
                }
                catch { nvencAvailable = false; }
            }

            // DODATO ZA WINDOWS MEDIA PLAYER: -pix_fmt yuv420p -profile:v high -level 4.1
            string vEncArgs = nvencAvailable
                ? "-c:v h264_nvenc -preset p2 -rc vbr -cq 23 -b:v 0 -profile:v high -level 4.1"
                : "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1";
            string pixFmt = "-pix_fmt yuv420p";

            const string TARGET_FPS = "30"; // ISKRA PATCH: uniformni 30fps (Pravilo 2)
            const string VSYNC_CFR = "-vsync cfr";
            string fpsSuffix = $",fps={TARGET_FPS}";
            LogToMainWindow($"RenderEngine: Enkoder: {(nvencAvailable ? "h264_nvenc (GPU)" : "libx264 (CPU)")} | FastRender={fastRender} | FPS={TARGET_FPS} CFR");
            vEncArgs_cached = vEncArgs;

            if (!File.Exists(_ffmpegPath))
            {
                LogToMainWindow(L("re_ffmpeg_missing"));
                throw new FileNotFoundException(LF("re_ffmpeg_path", _ffmpegPath));
            }

            var sortedItems = items.OrderBy(i => i.Start).ToList();

            LogToMainWindow($"RenderEngine: Ukupno klipova prije filtriranja: {sortedItems.Count}");

            foreach (var item in sortedItems)
            {
                LogToMainWindow($"  Klip: Type={item.Type}, Name={item.Name}, Path={(string.IsNullOrEmpty(item.Path) ? "EMPTY" : item.Path)}");
            }

            var allImageItems = sortedItems.Where(i => i.Type == "Image").ToList();
            LogToMainWindow($"RenderEngine: Ukupno Image klipova: {allImageItems.Count}");

            var images = allImageItems.Where(i =>
                !string.IsNullOrEmpty(i.Path) &&
                File.Exists(i.Path) &&
                !i.Name.Contains("Najavni") &&
                !i.Name.Contains("Odjavni")).ToList();

            var textImages = allImageItems.Where(i =>
                string.IsNullOrEmpty(i.Path) ||
                i.Name.Contains("Najavni") ||
                i.Name.Contains("Odjavni")).ToList();

            var audio = sortedItems
                .Where(i => (i.Type == "Audio" || i.IsAudio) &&
                            !i.Name.StartsWith("🔊"))
                .OrderByDescending(i => i.Duration)
                .FirstOrDefault()
                ?? sortedItems.FirstOrDefault(i => i.Type == "Audio" || i.IsAudio);
            var videos = sortedItems.Where(i => i.Type == "Video" || i.IsVideo).ToList();

            LogToMainWindow(LF("re_found_media", images.Count, textImages.Count, videos.Count));
            LogToMainWindow($"RenderEngine: Odabrana rezolucija: {resolution}");
            LogToMainWindow($"RenderEngine: Sortirano {sortedItems.Count} klipova po vremenskoj liniji");

            if (images.Count == 0 && videos.Count == 0 && textImages.Count == 0)
                throw new Exception("Nema slika, tekstova ili videa za render");

            string tempDir = Path.Combine(Path.GetTempPath(), "UVE_Render_") + Guid.NewGuid().ToString().Substring(0, 8);
            Directory.CreateDirectory(tempDir);
            LogToMainWindow($"RenderEngine: Privremeni folder: {tempDir}");

            string[] res = resolution.Split('x');
            int targetWidth = int.Parse(res[0]);
            int targetHeight = int.Parse(res[1]);

            try
            {
                var videoFiles = new List<string>();
                var itemToFile = new Dictionary<TimelineItem, string>();
                var fadeDurations = new List<double>(); // pacing-aware fade po klipu
                var transitionTypes = new List<string>(); // xfade tip po klipu (fade/wipeleft/slideleft/...)

                int total = sortedItems.Count(i => i.Type == "Image" || i.Type == "Video" || i.IsVideo || i.IsAudio == false);
                int current = 0;
                int fileIdx = 0;

                foreach (var item in sortedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isVideo = item.Type == "Video" || item.IsVideo;
                    bool isImage = item.Type == "Image";
                    bool isAudio = item.Type == "Audio" || item.IsAudio;
                    bool isTextItem = isImage && (string.IsNullOrEmpty(item.Path) ||
                                                  item.Name.Contains("Najavni") ||
                                                  item.Name.Contains("Odjavni") ||
                                                  (item.Path != null && !File.Exists(item.Path)));

                    if (isAudio) continue;

                    string tempVideo = Path.Combine(tempDir, $"clip_{fileIdx++:D4}.mp4");
                    // PATCH 11: Frame Freeze Fix — skraćujemo video klipove za 0.15s
                    // (0.15s umjesto 0.1s — agresivniji trim koji garantuje rez dok je subjekat u pokretu)
                    // (samo za video, ne za slike i tekst — slike nemaju EOF frame problem)
                    double effectiveDuration = item.Duration;
                    bool isVideoItem = item.Type == "Video";
                    if (isVideoItem && effectiveDuration > 0.5)
                    {
                        // BEAT-SYNC REZ: Snap kraj klipa na najbliži DownBeat
                        // Umjesto fiksnog -0.15s, nalazimo beat koji je najbliži kraju klipa
                        // i rezamo tamo (uz -80ms cut-advance da oko vidi promjenu = uho čuje beat).
                        if (beatInfo != null && beatInfo.IsValid)
                        {
                            double clipAbsEnd    = item.Start + item.Duration;
                            double snapRadius    = 0.200; // max 200ms odmaka od kraja klipa
                            const double CUT_ADV = 0.080; // 80ms ranije od downbeat-a

                            var downBeats = (beatInfo.DownBeats?.Count > 0)
                                ? beatInfo.DownBeats
                                : beatInfo.BeatTimes;

                            if (downBeats != null && downBeats.Count > 0)
                            {
                                double nearestBeat = downBeats
                                    .OrderBy(b => Math.Abs(b - clipAbsEnd))
                                    .First();
                                double diff = Math.Abs(nearestBeat - clipAbsEnd);

                                if (diff <= snapRadius)
                                {
                                    // Postoji beat blizu kraja klipa — snap na njega
                                    double snappedEnd = nearestBeat - CUT_ADV;
                                    double snappedDur = snappedEnd - item.Start;
                                    effectiveDuration = Math.Max(0.4, snappedDur);
                                    LogToMainWindow($"🥁 Beat-snap: '{item.Name.Substring(0, Math.Min(30, item.Name.Length))}' end {clipAbsEnd:F2}s → beat {nearestBeat:F2}s (diff={diff*1000:F0}ms) dur={effectiveDuration:F2}s");
                                }
                                else
                                {
                                    // Nema bita blizu — standardni -0.15s trim
                                    effectiveDuration = Math.Max(0.4, effectiveDuration - 0.15);
                                }
                            }
                            else
                            {
                                effectiveDuration = Math.Max(0.4, effectiveDuration - 0.15);
                            }
                        }
                        else
                        {
                            // Nema BeatInfo — standardni trim
                            effectiveDuration = Math.Max(0.4, effectiveDuration - 0.15);
                        }
                    }
                    string durationStr = effectiveDuration.ToString(CultureInfo.InvariantCulture);
                    bool success = false;

                    if (isTextItem)
                    {
                        LogToMainWindow($"RenderEngine: Tekstualni sloj '{item.Name}' (Start={item.Start:F1}s, trajanje: {item.Duration:F2}s)");

                        bool isOdjavniTekst = item.Name == "Odjavni tekst";

                        if (isOdjavniTekst)
                        {
                            // OUTRO FIX (isTextItem grana) — generišemo direktno crno platno.
                            // Outro uvijek ulazi ovdje jer isTextItem=true (Name.Contains("Odjavni")).
                            // Bypass Magick/pad pipeline koji može unijeti žuti padding artefakt.
                            string outroRaw = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path)
                                ? ExtractTextFromName(item.Name)
                                : item.Name;

                            var oWords = outroRaw.Split(' ');
                            var oLines = new System.Collections.Generic.List<string>();
                            var oCur = "";
                            foreach (var w in oWords)
                            {
                                string t = string.IsNullOrEmpty(oCur) ? w : oCur + " " + w;
                                if (t.Length > 40) { oLines.Add(oCur); oCur = w; }
                                else oCur = t;
                            }
                            if (!string.IsNullOrEmpty(oCur)) oLines.Add(oCur);

                            int oFontSize = 38;
                            int oLineH = 54;
                            double oTotalH = oLines.Count * oLineH;
                            double oStartY = Math.Max(80, (targetHeight - oTotalH) / 2.0);
                            var dtParts = new System.Collections.Generic.List<string>();
                            for (int oli = 0; oli < oLines.Count; oli++)
                            {
                                string el = EscapeText(oLines[oli]);
                                double ly = oStartY + oli * oLineH;
                                dtParts.Add(
                                    $"drawtext=text='{el}':fontcolor=white@0.95:fontsize={oFontSize}:" +
                                    $"x=(w-text_w)/2:y={ly:F0}:borderw=3:bordercolor=black@0.8:" +
                                    $"shadowx=2:shadowy=2:shadowcolor=black@0.6");
                            }
                            double oFadeOutSt = Math.Max(0.8, effectiveDuration - 2.0);
                            string outroVf =
                                string.Join(",", dtParts) +
                                $",fade=t=in:st=0:d=0.8" +
                                $",fade=t=out:st={oFadeOutSt:F2}:d=2.0" +
                                fpsSuffix;

                            string argsOutro =
                                $"-nostdin -f lavfi " +
                                $"-i color=c=black:s={targetWidth}x{targetHeight}:r={TARGET_FPS}:d={durationStr} " +
                                $"-vf \"{outroVf}\" " +
                                $"{vEncArgs} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                            success = await RunFFmpegAsync(argsOutro, cancellationToken);
                        }
                        else
                        {
                            // Najavni tekst i ostali tekstualni slojevi — originalni put
                            string displayText = !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path)
                                ? ExtractTextFromName(item.Name)
                                : item.Name;
                            string escapedText = EscapeText(displayText);
                            int fontSize = item.Name.Contains("Najavni") ? 60 : 34;

                            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                            {
                                string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                                if (preparedImage != null)
                                {
                                    string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black{fpsSuffix}";
                                    string argsImg = $"-nostdin -loop 1 -r {TARGET_FPS} -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                                    success = await RunFFmpegAsync(argsImg, cancellationToken);
                                }
                            }

                            if (!success)
                            {
                                string argsText = $"-nostdin -f lavfi -i color=c=black:s={targetWidth}x{targetHeight}:r={TARGET_FPS}:d={durationStr} " +
                                                  $"-vf \"drawtext=text='{escapedText}':fontcolor=white:fontsize={fontSize}:x=(w-text_w)/2:y=(h-text_h)/2:borderw=5:bordercolor=black:shadowx=3:shadowy=3:shadowcolor=black@0.8{fpsSuffix}\" " +
                                                  $"{vEncArgs} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                                success = await RunFFmpegAsync(argsText, cancellationToken);
                            }
                        }
                    }
                    else if (isImage)
                    {
                        if (!File.Exists(item.Path)) continue;

                        bool isNaslov = item.Name.StartsWith("Naslov:");
                        bool isOdjava = item.Name == "Odjavni tekst";
                        double dur = item.Duration;

                        if (isOdjava)
                        {
                            // OUTRO FIX: Direktno generišemo crno platno sa FFmpeg color source.
                            // Razlog: PrepareImageWithMagick + pad filter može unijeti žuti/default
                            // padding kada PNG dimenzije ne odgovaraju tačno ciljnoj rezoluciji.
                            // FFmpeg 'color=c=black' garantuje čistu crnu pozadinu bez artefakata.
                            //
                            // Tekst: multi-line drawtext centriran po visini platna.
                            // Fade-in : 0.8s — tekst se lagano pojavljuje
                            // Fade-out: 2.0s — profesionalan kraj, sinhronizan sa muzikom
                            //
                            // Crossfade 0.5s sa prethodnim klipom obavlja ApplyCrossfade jer
                            // outro uvijek dolazi kao zadnji element u videoFiles listi.

                            string outroRawText = ExtractTextFromName(item.Name);

                            // Prelomi tekst na linije od maks 40 znakova
                            var oWords = outroRawText.Split(' ');
                            var oLines = new System.Collections.Generic.List<string>();
                            var oCurrent = "";
                            foreach (var w in oWords)
                            {
                                string t = string.IsNullOrEmpty(oCurrent) ? w : oCurrent + " " + w;
                                if (t.Length > 40) { oLines.Add(oCurrent); oCurrent = w; }
                                else oCurrent = t;
                            }
                            if (!string.IsNullOrEmpty(oCurrent)) oLines.Add(oCurrent);

                            // Gradi drawtext filter za svaku liniju — sve centrirane vertikalno
                            int oFontSize = 38;
                            int oLineH = 54;
                            double oTotalH = oLines.Count * oLineH;
                            double oStartY = Math.Max(80, (targetHeight - oTotalH) / 2.0);
                            var dtParts = new System.Collections.Generic.List<string>();
                            for (int oli = 0; oli < oLines.Count; oli++)
                            {
                                string el = EscapeText(oLines[oli]);
                                double ly = oStartY + oli * oLineH;
                                dtParts.Add(
                                    $"drawtext=text='{el}':fontcolor=white@0.95:fontsize={oFontSize}:" +
                                    $"x=(w-text_w)/2:y={ly:F0}:borderw=3:bordercolor=black@0.8:" +
                                    $"shadowx=2:shadowy=2:shadowcolor=black@0.6");
                            }
                            double oFadeOutSt = Math.Max(0.8, dur - 2.0);
                            string outroVf =
                                string.Join(",", dtParts) +
                                $",fade=t=in:st=0:d=0.8" +
                                $",fade=t=out:st={oFadeOutSt:F2}:d=2.0" +
                                fpsSuffix;

                            string argsOutro =
                                $"-nostdin -f lavfi " +
                                $"-i color=c=black:s={targetWidth}x{targetHeight}:r={TARGET_FPS}:d={durationStr} " +
                                $"-vf \"{outroVf}\" " +
                                $"{vEncArgs} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                            success = await RunFFmpegAsync(argsOutro, cancellationToken);
                        }
                        else
                        {
                            // Sve ostale slike (foto, naslov) — originalni put
                            string preparedImage = await PrepareImageWithMagick(item.Path, tempDir);
                            if (preparedImage == null) continue;

                            string scaleF = $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=1," +
                                            $"pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:color=black";

                            string fadeFilter = "";
                            if (isNaslov)
                            {
                                fadeFilter = $",fade=t=in:st=0:d=0.8," +
                                             $"fade=t=out:st={Math.Max(0, dur - 0.5):F2}:d=0.5";
                            }

                            string imgVf = scaleF + fadeFilter + fpsSuffix;
                            string argsImg = $"-nostdin -loop 1 -r {TARGET_FPS} -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {VSYNC_CFR} {pixFmt} -vf \"{imgVf}\" -y \"{tempVideo}\"";
                            success = await RunFFmpegAsync(argsImg, cancellationToken);
                        }
                    }
                    else if (isVideo)
                    {
                        if (!File.Exists(item.Path)) continue;

                        // Aspect ratio: scale-to-fill + center crop — nema crnih rubova, nema stretch-a.
                        // Višak se odsjeca od ivica (uvijek centar kadra), što je standardni TV/cinema pristup.
                        string scaleFilter =
                            $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase:flags=lanczos," +
                            $"crop={targetWidth}:{targetHeight}:(iw-{targetWidth})/2:(ih-{targetHeight})/2";
                        string baseNormalize = "eq=brightness=0.04:saturation=1.1:contrast=1.02,format=yuv420p";

                        string moodTag = item.AudioDescription ?? "";
                        string moodFilter = ExtractTag(moodTag, "mood") != "" ? "eq=saturation=1.10:brightness=0.02:contrast=1.03,curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'" : "";

                        string videoVf = string.IsNullOrEmpty(moodFilter)
                            ? $"{scaleFilter},{baseNormalize}"
                            : $"{scaleFilter},{baseNormalize},{moodFilter}";

                        int sceneEnergy = 3;
                        string audioDesc2 = item.AudioDescription ?? "";
                        var energyMatch = System.Text.RegularExpressions.Regex.Match(audioDesc2, @"energy=(\d+)");
                        if (energyMatch.Success)
                        {
                            int parsed;
                            if (int.TryParse(energyMatch.Groups[1].Value, out parsed))
                                sceneEnergy = parsed;
                        }

                        bool isStaticClip = audioDesc2.Contains("static=1");
                        double anchorBrightness = -1;
                        var anchorMatch = System.Text.RegularExpressions.Regex.Match(audioDesc2, @"anchor_brightness=([\d.]+)");
                        if (anchorMatch.Success && double.TryParse(anchorMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double abv))
                            anchorBrightness = abv;

                        double overscanFactor = sceneEnergy >= 4 ? 1.20 : sceneEnergy <= 2 ? 1.10 : 1.15;

                        string warmthBoost;
                        if (sceneEnergy >= 4)
                            warmthBoost = "curves=r='0/0 0.5/0.56 1/1':g='0/0 0.5/0.54 1/1'";
                        else if (sceneEnergy <= 2)
                            warmthBoost = "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'";
                        else
                            warmthBoost = "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.525 1/1'";

                        // Hybrid Content Selector — Warm White Balance
                        bool hasWwb = audioDesc2.Contains("wwb=1");
                        string wwbFilter = hasWwb
                            ? ",curves=r='0 0 0.5 0.58 1 1':g='0 0 0.5 0.53 1 1':b='0 0 0.5 0.46 1 1'"
                            : "";

                        // FIX-SEASONAL: Per-kadar sezonski color grading na osnovu season taga iz stiha
                        // "Kad je zima nosi čizme" → hladni plavi toni na TOM kadru
                        // "Leti kupite sladoled"   → topli zlatni toni na TOM kadru
                        // season tag se upisuje u MoodTag od AIVideoCreator po stihu
                        string lyricSeasonTag = ExtractTag(audioDesc2, "season");
                        string seasonalGradeFilter = lyricSeasonTag switch
                        {
                            "winter" => // Hladni plavi toni — sneg, čizme, skafander
                                ",curves=r='0/0 0.5/0.46 1/0.92':g='0/0 0.5/0.50 1/0.96':b='0/0 0.5/0.57 1/1.08'" +
                                ",eq=saturation=0.88:contrast=1.05",
                            "summer" => // Topli zlatni toni — sladoled, sunce, leto
                                ",curves=r='0/0 0.5/0.57 1/1':g='0/0 0.5/0.54 1/1':b='0/0 0.5/0.44 1/0.88'" +
                                ",eq=saturation=1.18:contrast=1.02",
                            "spring" => // Svježi zeleno-žuti toni — proleće, cvetovi
                                ",curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.56 1/1.02':b='0/0 0.5/0.48 1/0.94'" +
                                ",eq=saturation=1.12:contrast=1.01",
                            "autumn" => // Topli narandžasto-smeđi toni — jesen, lišće
                                ",curves=r='0/0 0.5/0.58 1/1.02':g='0/0 0.5/0.52 1/0.97':b='0/0 0.5/0.43 1/0.86'" +
                                ",eq=saturation=1.10:contrast=1.03",
                            _ => "" // Bez sezonskog filtera ako sezona nije detektovana
                        };

                        bool needsSlowMoFix = audioDesc2.Contains("slowmo=1");
                        string fpsNormalize = needsSlowMoFix
                            ? ",minterpolate=fps=25:mi_mode=mci:mc_mode=aobmc:me=hexbs:vsbmc=1"
                            : "";

                        // ── GLOBALNA COLOR NORMALIZACIJA ─────────────────────────────────────
                        // Svaki klip se normalizuje na ISTI target (ne relativno na anchor klip)
                        // Target: brightness=0.54, contrast=1.04, saturation=1.12
                        // Ovo eliminira "TV kanal" skokove između klipova različitog osvetljenja
                        // Dodatno: LUT-style warm tint za dečiji video (lagana toplina)
                        // ─────────────────────────────────────────────────────────────────────
                        const double TARGET_BRIGHTNESS = 0.54;
                        string colorMatchFilter = "";
                        if (anchorBrightness > 0)
                        {
                            // Pomjeri klip ka globalnom targetu, ne ka anchor klipu
                            double delta = Math.Max(-0.10, Math.Min(0.10, TARGET_BRIGHTNESS - anchorBrightness));
                            if (Math.Abs(delta) > 0.02)
                                colorMatchFilter = $",eq=brightness={delta.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
                        }
                        // Globalni "dječiji video" LUT: topli kontrast, žive ali ne neon boje
                        // eq: contrast=1.04 (malo kontrasta), saturation=1.12 (žive boje)
                        // curves: blagi warm tint (R+, G+, B-)
                        string satClampFilter = ",eq=saturation=1.12:contrast=1.04" +
                                                ",curves=r='0/0 0.5/0.54 1/1':b='0/0 0.5/0.47 1/1'";
                        colorMatchFilter = colorMatchFilter + satClampFilter;

                        // FIX-DENOISE: Ujednačavanje teksture između klipova različite kamere/kvaliteta
                        // hqdn3d=1.5:1.5:6:6 — blagi temporal+spatial denoise (ne mrvi detalje, samo šum)
                        // unsharp=3:3:0.4:3:3:0.0 — blago oštri luminancu (Y), ne boje (ne dodaje artefakte)
                        // Rezultat: klipovi sa šumom (telefon, noć, kompresija) izgledaju kao ostatak videa
                        // Vrednosti su konzervativne — za dečiji video veće vrednosti izgledaju artificijelno
                        const string DENOISE_FILTER = ",hqdn3d=1.5:1.5:6:6,unsharp=3:3:0.4:3:3:0.0";

                        baseNormalize = $"{baseNormalize},{warmthBoost}";
                        videoVf = string.IsNullOrEmpty(moodFilter)
                            ? $"{scaleFilter},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                            : $"{scaleFilter},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}";

                        if (!fastRender)
                        {
                            try
                            {
                                if (isStaticClip && item.Duration >= 2.0)
                                {
                                    // Statički klip (foto) — Ken Burns sa 6 tipova pokreta
                                    int staticFrames = Math.Max(1, (int)(item.Duration * 30.0));
                                    int overWs = (int)(targetWidth * 1.15);  // malo agresivniji overscan za foto
                                    int overHs = (int)(targetHeight * 1.15);
                                    if (overWs % 2 != 0) overWs++;
                                    if (overHs % 2 != 0) overHs++;
                                    int maxXs = overWs - targetWidth;
                                    int maxYs = overHs - targetHeight;

                                    int kbTypeS = fileIdx % 6;
                                    string sxExpr, syExpr;
                                    switch (kbTypeS)
                                    {
                                        case 0: sxExpr = $"{maxXs}*n/{staticFrames}";         syExpr = $"{maxYs}*n/{staticFrames}"; break;
                                        case 1: sxExpr = $"{maxXs}*(1-n/{staticFrames})";     syExpr = $"{maxYs}*(1-n/{staticFrames})"; break;
                                        case 2: sxExpr = $"{maxXs}*n/{staticFrames}";         syExpr = $"{maxYs/2}"; break;
                                        case 3: sxExpr = $"{maxXs}*(1-n/{staticFrames})";     syExpr = $"{maxYs/2}"; break;
                                        case 4: sxExpr = $"{maxXs/2}*n/{staticFrames}";       syExpr = $"{maxYs}*n/{staticFrames}"; break;
                                        default: sxExpr = $"{maxXs/2}*(1-n/{staticFrames})";  syExpr = $"0"; break;
                                    }

                                    string staticKB = $"scale={overWs}:{overHs}:flags=lanczos," +
                                                      $"crop={targetWidth}:{targetHeight}:{sxExpr}:{syExpr}";
                                    // Ken Burns + color grading + fps normalizacija
                                    videoVf = string.IsNullOrEmpty(moodFilter)
                                        ? $"{staticKB},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                                        : $"{staticKB},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}";
                                }

                                if (item.Duration >= 2.0 && !isStaticClip)
                                {
                                    int overW = (int)(targetWidth * overscanFactor);
                                    int overH = (int)(targetHeight * overscanFactor);
                                    if (overW % 2 != 0) overW++;
                                    if (overH % 2 != 0) overH++;

                                    int frames = Math.Max(1, (int)(item.Duration * 30.0));
                                    int maxX = overW - targetWidth;
                                    int maxY = overH - targetHeight;

                                    // ── Ken Burns: 6 tipova pokreta, rotiramo po index-u ──────
                                    // Svaki klip dobija drugačiji smer — ne repetitivno
                                    // n = frame broj (0..frames), raste linearno
                                    int kbType = fileIdx % 6;
                                    string xExpr, yExpr;
                                    switch (kbType)
                                    {
                                        case 0: // zoom-in iz centra (klasični KB)
                                            xExpr = $"{maxX/2}*n/{frames}";
                                            yExpr = $"{maxY/2}*n/{frames}";
                                            break;
                                        case 1: // zoom-out ka centru (obrnuti KB)
                                            xExpr = $"{maxX/2}*(1-n/{frames})";
                                            yExpr = $"{maxY/2}*(1-n/{frames})";
                                            break;
                                        case 2: // pan desno, stabilan Y
                                            xExpr = $"{maxX}*n/{frames}";
                                            yExpr = $"{maxY/2}";
                                            break;
                                        case 3: // pan lijevo, stabilan Y
                                            xExpr = $"{maxX}*(1-n/{frames})";
                                            yExpr = $"{maxY/2}";
                                            break;
                                        case 4: // dijagonala: gore-lijevo → dolje-desno
                                            xExpr = $"{maxX}*n/{frames}";
                                            yExpr = $"{maxY}*n/{frames}";
                                            break;
                                        default: // dijagonala: dolje-desno → gore-lijevo
                                            xExpr = $"{maxX}*(1-n/{frames})";
                                            yExpr = $"{maxY}*(1-n/{frames})";
                                            break;
                                    }

                                    string kenBurnsGpu =
                                        $"scale={overW}:{overH}:flags=lanczos," +
                                        $"crop={targetWidth}:{targetHeight}:{xExpr}:{yExpr}";

                                    // Ken Burns + color grading + fps normalizacija
                                    videoVf = string.IsNullOrEmpty(moodFilter)
                                        ? $"{kenBurnsGpu},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                                        : $"{kenBurnsGpu},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{fpsNormalize}{DENOISE_FILTER}";
                                }
                            }
                            catch { }
                        }

                        bool isZoompan = videoVf.Contains("zoompan");
                        string argsVid;

                        if (isZoompan)
                        {
                            // ISKRA PATCH Pravilo 2: fps normalizacija i za zoompan putanju
                            string normVfZp = $"scale={targetWidth}:{targetHeight}:flags=lanczos,fps=fps={TARGET_FPS}:round=near,format=yuv420p";
                            argsVid = $"-nostdin -t {durationStr} -i \"{item.Path}\" -vf \"{normVfZp},{videoVf}\" {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }
                        else
                        {
                            // ISKRA PATCH — Pravilo 2: fps=round=near garantuje uniformni FPS
                            // bez preskakanja frejmova (za razliku od starih metoda).
                            // stream_loop + fade=in sakriva loop-point trganje.
                            // round=near: zaokrugljava PTS umjesto da preskače frejmove —
                            // eliminišo zelene/crne međufrejmove na spojevima klipova.
                            string loopFadeIn = $",fade=t=in:st=0:d=0.3";
                            // Pravilo 2: scale+fps normalizacija u jednom filtru
                            string normVf = $"scale={targetWidth}:{targetHeight}:flags=lanczos,fps=fps={TARGET_FPS}:round=near,format=yuv420p";
                            argsVid = $"-nostdin -stream_loop -1 -t {durationStr} -i \"{item.Path}\" -vf \"{normVf},{videoVf}{loopFadeIn}\" {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }

                        success = await RunFFmpegAsync(argsVid, cancellationToken);
                    }
                    else
                    {
                        continue;
                    }

                    if (success)
                    {
                        videoFiles.Add(tempVideo);
                        itemToFile[item] = tempVideo;
                        // ── Pacing-aware fade: fast→0.25s, slow→0.70s, standard→0.50s ──
                        // FIX-FADE: povećano na 0.50s za standard (Gemini: min 0.5s za fluidnost)
                        // fast ostaje kratak da ne ubije energiju refrena
                        string pacingTag = ExtractTag(item.AudioDescription ?? "", "pacing");
                        double clipFade = pacingTag switch
                        {
                            "fast" => 0.25,
                            "slow" => 0.70,
                            _      => 0.50
                        };
                        fadeDurations.Add(clipFade);

                        // Izvuci xfade tip iz AudioDescription (setovan od AIVideoCreator.AddCrossfadeWithEnergy)
                        string xfadeType = ExtractTag(item.AudioDescription ?? "", "xfade");

                        // FIX-BRIGHTNESS-TRANSITION: odaberi tip tranzicije na osnovu razlike u svjetlini
                        // Gemini preporuka: svjetlo→tamno = crossfade (blagi), tamno→tamno = hard cut (brzi fade)
                        // Dobivamo brightness iz anchor_brightness taga
                        if (string.IsNullOrEmpty(xfadeType) || xfadeType == "fade")
                        {
                            double clipBrightness   = 0;
                            double prevBrightness   = 0;
                            var bMatch = System.Text.RegularExpressions.Regex.Match(
                                item.AudioDescription ?? "", @"anchor_brightness=([\d.]+)");
                            if (bMatch.Success && double.TryParse(bMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double bv))
                                clipBrightness = bv;

                            int prevIdx = videoFiles.Count - 1; // index of previous added clip
                            if (prevIdx >= 0 && prevIdx < sortedItems.Count)
                            {
                                var prevItemDesc = sortedItems
                                    .Where(si => itemToFile.ContainsKey(si))
                                    .Skip(prevIdx).Take(1).FirstOrDefault()?.AudioDescription ?? "";
                                var pbMatch = System.Text.RegularExpressions.Regex.Match(
                                    prevItemDesc, @"anchor_brightness=([\d.]+)");
                                if (pbMatch.Success && double.TryParse(pbMatch.Groups[1].Value,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double pb))
                                    prevBrightness = pb;
                            }

                            double brightnessDelta = Math.Abs(clipBrightness - prevBrightness);
                            // Jako različita svjetlina (>0.20) → dissolve (blago, ne reže oštro)
                            // Slična svjetlina ili oba tamna → fade (standardni)
                            xfadeType = brightnessDelta > 0.20 ? "dissolve" : "fade";
                        }

                        transitionTypes.Add(xfadeType);
                    }

                    current++;
                    progress?.Report(Math.Min(85, current * 85 / Math.Max(1, total)));
                }

                if (videoFiles.Count == 0)
                    throw new Exception(L("re_no_media"));

                // ── ISKRA PATCH — Pravilo 1: Matematička sinhronizacija trajanja ─────────────
                // Mjeri ukupno video trajanje i produžava zadnji klip loopom ako postoji deficit.
                // Nikad crna pozadina — loop zadnjeg klipa je vizuelno nevidljiv.
                //
                // BUG-3 FIX: Crossfade oduzima (N-1)*avgFade sekundi od ukupnog trajanja.
                // IskraSync mora to uzeti u obzir — inače produžava premalo i video ostaje kraći od audija.
                // Formula: effectiveVideoDur = sum(clipDurs) - crossfadeOverlap
                // gdje crossfadeOverlap = (N-1) * avgFade (N = broj video klipova)
                if (audio != null && File.Exists(audio.Path))
                {
                    double audioDurForSync = await GetVideoDuration(audio.Path, cancellationToken);
                    // BUG-3 FIX: Oduzmi AudioStartSeconds jer -ss na audio skraćuje efektivno trajanje audija
                    double audioSsOffset = (beatInfo != null && beatInfo.AudioStartSeconds > 0.05)
                        ? beatInfo.AudioStartSeconds : 0.0;
                    audioDurForSync = Math.Max(0, audioDurForSync - audioSsOffset);

                    if (audioDurForSync > 0)
                    {
                        double totalVidDur = 0;
                        var clipDurations = new List<double>();
                        foreach (var vfp in videoFiles)
                        {
                            double vd = await GetVideoDuration(vfp, cancellationToken);
                            clipDurations.Add(vd);
                            totalVidDur += vd;
                        }

                        // BUG-3 FIX: Izračunaj crossfade overlap koji će se oduzeti od ukupnog trajanja
                        double avgFadeForSync = fadeDurations.Count > 0
                            ? fadeDurations.Average() : 0.35;
                        int nClips = videoFiles.Count;
                        double crossfadeOverlap = nClips > 1 ? (nClips - 1) * avgFadeForSync : 0.0;
                        double effectiveVideoDur = totalVidDur - crossfadeOverlap;

                        double deficit = audioDurForSync - effectiveVideoDur;
                        LogToMainWindow($"[IskraSync] Audio={audioDurForSync:F3}s (ss={audioSsOffset:F2}s) | VideoSum={totalVidDur:F3}s | XfadeOverlap={crossfadeOverlap:F2}s | EffectiveVideo={effectiveVideoDur:F3}s | Deficit={deficit:F3}s");

                        if (deficit > 0.15 && videoFiles.Count > 0)
                        {
                            // ── BUG-2 FIX: Robusna kompenzacija deficita ──────────────────────
                            // Stari kod: loop filter ponekad ne radi ako je klip kratak (<1s)
                            //            ili ima nekompatibilan pixel format.
                            // Novo:  1) Pokusaj stream_loop (brzi, bez re-enkodiranja)
                            //        2) Fallback: loop video filter (sa re-enkodiranjem)
                            //        3) Fallback: dodaj kopiju zadnjeg klipa segmentima
                            // Sve tri metode garantuju popunjavanje deficita.
                            // ─────────────────────────────────────────────────────────────────
                            string lastClipPath = videoFiles[videoFiles.Count - 1];
                            double lastDur = clipDurations[clipDurations.Count - 1];
                            // +1.0s buffer — fade-out trosi 2.5s, vise bolje nego manje
                            double newDur = lastDur + deficit + 1.0;
                            string newDurStr = newDur.ToString("F3", CultureInfo.InvariantCulture);
                            string loopedPath = Path.Combine(tempDir, $"looped_last_{Guid.NewGuid().ToString().Substring(0, 6)}.mp4");

                            LogToMainWindow($"[IskraSync] Produzujem zadnji klip: {lastDur:F2}s → {newDur:F2}s (deficit={deficit:F2}s)");

                            // Metoda 1: stream_loop — brz, bez re-enkoda, radi na vecini klipova
                            string streamLoopArgs = $"-nostdin -stream_loop -1 -t {newDurStr} -i \"{lastClipPath}\" " +
                                                   $"-vf \"fps=fps={TARGET_FPS}:round=near,format=yuv420p\" " +
                                                   $"{vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{loopedPath}\"";
                            bool loopOk = await RunFFmpegAsync(streamLoopArgs, cancellationToken);

                            // Metoda 2: video filter loop (ako stream_loop ne uspije)
                            if (!loopOk || !File.Exists(loopedPath) || new FileInfo(loopedPath).Length < 1000)
                            {
                                LogToMainWindow($"[IskraSync] stream_loop nije uspio — koristim loop filter...");
                                int loopCount = (int)Math.Ceiling(newDur / Math.Max(0.1, lastDur)) + 2;
                                string loopVf = $"loop=loop={loopCount}:size=32767:start=0," +
                                                $"fps=fps={TARGET_FPS}:round=near,format=yuv420p";
                                string loopFilterArgs = $"-nostdin -i \"{lastClipPath}\" " +
                                                        $"-vf \"{loopVf}\" -t {newDurStr} " +
                                                        $"{vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{loopedPath}\"";
                                loopOk = await RunFFmpegAsync(loopFilterArgs, cancellationToken);
                            }

                            if (loopOk && File.Exists(loopedPath) && new FileInfo(loopedPath).Length > 1000)
                            {
                                double actualLooped = await GetVideoDuration(loopedPath, cancellationToken);
                                videoFiles[videoFiles.Count - 1] = loopedPath;
                                LogToMainWindow($"[IskraSync] ✅ Zadnji klip produžen: {lastDur:F2}s → {actualLooped:F2}s (potrebno {newDur:F2}s)");
                            }
                            else
                            {
                                // Metoda 3: segmentna duplikacija — garancija za edge case-ove
                                LogToMainWindow($"[IskraSync] ⚠ Loop nije uspio — segmentna duplikacija ({deficit:F2}s)");
                                double remaining = deficit + 1.0;
                                int maxIter = 50; // sigurnosni limit
                                while (remaining > 0.1 && maxIter-- > 0)
                                {
                                    double segDur = Math.Min(remaining, Math.Max(1.0, lastDur));
                                    string segDurStr = segDur.ToString("F3", CultureInfo.InvariantCulture);
                                    string dupPath = Path.Combine(tempDir, $"dup_{videoFiles.Count:D3}.mp4");
                                    // -c:v copy je brz ali moze imati PTS problem; koristimo normalizaciju
                                    string dupArgs = $"-nostdin -i \"{lastClipPath}\" -t {segDurStr} " +
                                                    $"-vf \"fps=fps={TARGET_FPS}:round=near,format=yuv420p\" " +
                                                    $"{vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{dupPath}\"";
                                    bool dupOk = await RunFFmpegAsync(dupArgs, cancellationToken);
                                    if (dupOk && File.Exists(dupPath) && new FileInfo(dupPath).Length > 100)
                                        videoFiles.Add(dupPath);
                                    remaining -= segDur;
                                }
                                LogToMainWindow($"[IskraSync] ✅ Segmentna duplikacija završena: {videoFiles.Count} klipova ukupno");
                            }
                        }
                        else if (deficit <= 0)
                        {
                            LogToMainWindow($"[IskraSync] ✅ Trajanje OK (video duži od audija za {-deficit:F2}s — -shortest ce porezati)");
                        }
                    }
                }
                // ─────────────────────────────────────────────────────────────────────────────

                string concatFile = Path.Combine(tempDir, "concat.txt");
                using (var sw = new StreamWriter(concatFile, false, new UTF8Encoding(false)))
                {
                    foreach (var vf in videoFiles)
                        await sw.WriteLineAsync($"file '{vf.Replace("\\", "/")}'");
                }

                // ── CROSS-DISSOLVE: single-pass FFmpeg xfade ─────────────────────────
                // Pacing-aware: fast klipovi 0.15s, slow 0.60s, standard 0.35s fade.
                // ─────────────────────────────────────────────────────────────────────
                const double CROSSFADE_DURATION = 0.35;
                double avgFade = fadeDurations.Count > 0
                    ? Math.Round(fadeDurations.Average(), 3) : CROSSFADE_DURATION;
                string crossfadedVideo = null;
                if (videoFiles.Count >= 2)
                {
                    crossfadedVideo = await ApplyCrossfadeSinglePass(
                        videoFiles, tempDir, fadeDurations, CROSSFADE_DURATION, cancellationToken, transitionTypes);
                    if (crossfadedVideo != null)
                        LogToMainWindow($"✨ Cross-dissolve: {videoFiles.Count} klipova, avg fade {avgFade}s (pacing-aware)");
                    else
                    {
                        LogToMainWindow("⚠️ Cross-dissolve nije uspio — koristim pairwise");
                        if (videoFiles.Count <= 24)
                            crossfadedVideo = await ApplyCrossfadePairwise(
                                videoFiles, tempDir, fadeDurations, CROSSFADE_DURATION, cancellationToken);
                    }
                }

                string finalOutput = outputPath;
                string argsFinal;

                if (audio != null && File.Exists(audio.Path))
                {
                    string tempAudioPath = Path.Combine(tempDir, "audio" + Path.GetExtension(audio.Path));
                    File.Copy(audio.Path, tempAudioPath, true);

                    var ambientItem = items.FirstOrDefault(i =>
                        !string.IsNullOrEmpty(i.AmbientSoundPath) &&
                        File.Exists(i.AmbientSoundPath));

                    if (ambientItem != null)
                    {
                        string mixedAudio = Path.Combine(tempDir, "mixed_audio.aac");
                        string mixedPath = tempAudioPath;
                        tempAudioPath = mixedPath;
                    }

                    var secondaryAudioClips = sortedItems
                        .Where(i => i.Type == "Audio" &&
                                    i != audio &&
                                    !string.IsNullOrEmpty(i.Path) &&
                                    File.Exists(i.Path))
                        .OrderBy(i => i.Start)
                        .GroupBy(i => Math.Round(i.Start, 1))
                        .Select(g => g.First())
                        .Take(50)
                        .ToList();

                    if (secondaryAudioClips.Count > 0)
                    {
                        string mixedWithTransitions = Path.Combine(tempDir, "audio_with_transitions.aac");
                        string mixResult = await MixSecondaryAudioClips(
                            tempAudioPath,
                            secondaryAudioClips,
                            mixedWithTransitions,
                            cancellationToken);
                        if (mixResult != null)
                        {
                            tempAudioPath = mixResult;
                        }
                    }

                    // ISKRA PATCH — Pravilo 4: Titlovi se vise ne renderuju SRT/ASS putem.
                    // Drawtext hard-sync (between(t,s,e)) je implementiran u post-processing prolazu
                    // koji se izvrsava NAKON finalnog muxa. Razlog: SRT/subtitles filter ne podrzava
                    // Whisper hard-anchor logiku — titl ne moze biti ugasen u tocnom Whisper trenutku.
                    // concat + -c:v copy = najbrzi i najpouzdaniji put do finalnog fajla.
                    // Drawtext se dodaje u post-pass koji takodje radi fade-in/fade-out.
                    // ── BEAT-LOCK: AudioStartSeconds snap ────────────────────────────────
                    // Ako BeatDetection otkrije tišinu na početku audio fajla,
                    // audio se pomiče nazad za AudioStartSeconds pomoću -ss parametra.
                    // Ovo eliminira "mrtav hod" — zeleni/statični uvodni kadrovi koji
                    // nastaju jer audio kasni za videom (tišina na početku MP3/WAV).
                    //
                    // PRINCIP: audio -ss (input seeking) je lossless i milisekundno precizan.
                    // Video ostaje nepromijenjen — audio se samo "pomiče" da počne u beat 0.
                    //
                    // UVJET: AudioStartSeconds > 50ms (ispod toga je noise, ne tišina)
                    // ─────────────────────────────────────────────────────────────────────
                    string audioSsArg = "";
                    if (beatInfo != null && beatInfo.AudioStartSeconds > 0.05)
                    {
                        string ssVal = beatInfo.AudioStartSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                        audioSsArg = $"-ss {ssVal} ";
                        LogToMainWindow($"🥁 Beat-Lock AudioStart: audio -ss {ssVal}s → video i audio sada kreću zajedno");
                    }

                    if (crossfadedVideo != null && File.Exists(crossfadedVideo))
                        argsFinal = $"-nostdin -i \"{crossfadedVideo}\" {audioSsArg}-i \"{tempAudioPath}\" -c:v copy -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                    else
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" {audioSsArg}-i \"{tempAudioPath}\" -c:v copy -c:a aac -map 0:v -map 1:a -shortest -y \"{finalOutput}\"";
                }
                else
                {
                    if (crossfadedVideo != null && File.Exists(crossfadedVideo))
                        argsFinal = $"-nostdin -i \"{crossfadedVideo}\" -c:v copy -y \"{finalOutput}\"";
                    else
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" -c:v copy -y \"{finalOutput}\"";
                }

                await RunFFmpegAsync(argsFinal, cancellationToken);

                if (File.Exists(finalOutput))
                {
                    string postOutput = finalOutput.Replace(".mp4", "_post.mp4");
                    try
                    {
                        double finalDur = await GetVideoDuration(finalOutput, cancellationToken);
                        if (finalDur > 4.0)
                        {
                            // Fade-out: PATCH 11 — Final Polish, tačno 2.5s
                            // Slika I titlovi nestaju zajedno sa muzikom (sinhronizovano)
                            // (2.5s = mekan, siguran kraj za djecu 2-6g — nema naglog prekida)
                            double fadeOutDuration = 2.5;
                            double fadeStart = Math.Max(0, finalDur - fadeOutDuration);
                            string fadeStartStr = fadeStart.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                            string fadeOutDurStr = fadeOutDuration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                            // Fade-in: 1.0s na pocetku (Cold Open)
                            string fadeInFilter = "fade=t=in:st=0:d=1.2"; // PATCH 10: 1.2s profesionalni fade-in

                            string encArgsPost = vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 23 -profile:v high -level 4.1";

                            // ISKRA PATCH — Pravilo 4: Whisper Hard-Sync drawtext
                            // Titlovi se renderuju direktno u post-processing prolazu.
                            // Svaki titl je aktivan ISKLJUČIVO između svog Whisper start i end.
                            // enable='between(t,START,END)' — čist ekran u instrumentalnim pauzama.
                            // enableSubtitles=false: globalni prekidač — nema drawtext filtera.
                            string drawtextVf = "";
                            if (enableSubtitles && subtitles != null && subtitles.Count > 0)
                            {
                                var dtParts2 = new System.Collections.Generic.List<string>();
                                foreach (var sub in subtitles.OrderBy(s => s.Start))
                                {
                                    if (string.IsNullOrWhiteSpace(sub.Text)) continue;
                                    double subS = Math.Max(0, sub.Start);
                                    double subE = sub.End > subS + 0.1 ? sub.End : subS + 1.5;
                                    // Omotaj tekst na linije od max 42 znaka
                                    var subWords = sub.Text.Split(' ');
                                    var subLines = new System.Collections.Generic.List<string>();
                                    var subCur = "";
                                    foreach (var w in subWords) {
                                        string t2 = string.IsNullOrEmpty(subCur) ? w : subCur + " " + w;
                                        if (t2.Length > 42) { subLines.Add(subCur); subCur = w; } else subCur = t2;
                                    }
                                    if (!string.IsNullOrEmpty(subCur)) subLines.Add(subCur);
                                    int dtFontSize = targetHeight >= 1080 ? 56 : 44;
                                    int dtLineH = (int)(dtFontSize * 1.35);
                                    double dtBlockH = subLines.Count * dtLineH;
                                    double dtBaseY = targetHeight - dtBlockH - (targetHeight * 0.08);
                                    for (int sli = 0; sli < subLines.Count; sli++)
                                    {
                                        // Escape specijalni znakovi za drawtext
                                        string esc = subLines[sli]
                                            .Replace("\\", "\\\\")
                                            .Replace("'", "\\'")
                                            .Replace(":", "\\:")
                                            .Replace("%", "\\%");
                                        double ly2 = dtBaseY + sli * dtLineH;
                                        string sStr = subS.ToString("F3", CultureInfo.InvariantCulture);
                                        string eStr = subE.ToString("F3", CultureInfo.InvariantCulture);
                                        // PRAVILO 4 — KLJUČNA LINIJA:
                                        // between(t,START,END) = titl vidljiv SAMO u Whisper prozoru
                                        // Čim Whisper kaže "kraj" — tekst nestaje. Čist ekran u pauzama.
                                        dtParts2.Add(
                                            $"drawtext=enable='between(t,{sStr},{eStr})'" +
                                            $":text='{esc}':fontsize={dtFontSize}:fontcolor=white@0.95" +
                                            $":x=(w-text_w)/2:y={ly2:F0}" +
                                            $":borderw=4:bordercolor=black@0.85" +
                                            $":shadowx=2:shadowy=2:shadowcolor=black@0.70");
                                    }
                                }
                                if (dtParts2.Count > 0)
                                    drawtextVf = "," + string.Join(",", dtParts2);
                            }

                            string postArgs = "-nostdin -i \"" + finalOutput + "\" " +
                                "-vf \"" + fadeInFilter + "," +
                                "fade=t=out:st=" + fadeStartStr + ":d=" + fadeOutDurStr + "," +
                                "colorchannelmixer=rr=1:rb=-0.02:gr=0:gg=1:gb=-0.02:br=-0.03:bg=-0.03:bb=1.06" +
                                drawtextVf + "\" " +
                                "-af \"afade=t=in:st=0:d=0.8,afade=t=out:st=" + fadeStartStr + ":d=" + fadeOutDurStr + "\" " +
                                $"{encArgsPost} -pix_fmt yuv420p -c:a aac -y \"{postOutput}\"";

                            bool postOk = await RunFFmpegAsync(postArgs, cancellationToken);
                            if (postOk && File.Exists(postOutput) && new FileInfo(postOutput).Length > 100_000)
                            {
                                File.Delete(finalOutput);
                                File.Move(postOutput, finalOutput);
                            }
                        }
                    }
                    catch { }
                }

                progress?.Report(100);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private async Task<double> GetVideoDuration(string videoPath, CancellationToken ct)
        {
            try
            {
                string probePath = _ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
                if (File.Exists(probePath))
                {
                    // FIX BUG-2: Koristimo format-level duration (radi i za MP3/AAC audio fajlove).
                    // Stari -select_streams v:0 ne radi za audio-only fajlove — vraca prazan string.
                    // -show_entries format=duration cita container duration bez obzira na tip streama.
                    string dArgs = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"" + videoPath + "\"";
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = probePath,
                        Arguments = dArgs,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    var probeSo = proc.StandardOutput.ReadToEndAsync();
                    var probeSe = proc.StandardError.ReadToEndAsync();
                    await Task.WhenAll(probeSo, probeSe);
                    await proc.WaitForExitAsync(ct);
                    string output = probeSo.Result;
                    if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0)
                        return d;
                }

                var psi2 = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-nostdin -i \"" + videoPath + "\" -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc2 = System.Diagnostics.Process.Start(psi2);
                var _re1so = proc2.StandardOutput.ReadToEndAsync();
                var _re1se = proc2.StandardError.ReadToEndAsync();
                await Task.WhenAll(_re1so, _re1se);
                await proc2.WaitForExitAsync(ct);
                string stderr = _re1se.Result;
                var m = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (m.Success)
                    return int.Parse(m.Groups[1].Value) * 3600
                         + int.Parse(m.Groups[2].Value) * 60
                         + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return 0.0;
        }

        private async Task<string> MixSecondaryAudioClips(
            string mainAudioPath,
            List<TimelineItem> secondaryClips,
            string outputPath,
            CancellationToken ct)
        {
            try
            {
                string currentAudio = mainAudioPath;
                string tempDir = Path.GetDirectoryName(outputPath);
                const int batchSize = 8;
                int batchNum = 0;

                var batches = secondaryClips
                    .Select((clip, i) => new { clip, i })
                    .GroupBy(x => x.i / batchSize)
                    .Select(g => g.Select(x => x.clip).ToList())
                    .ToList();

                foreach (var batch in batches)
                {
                    batchNum++;
                    bool isLast = batchNum == batches.Count;
                    string batchOutput = isLast
                        ? outputPath
                        : Path.Combine(tempDir, $"mix_batch_{batchNum}_{Guid.NewGuid().ToString().Substring(0, 6)}.aac");

                    var inputs = new StringBuilder();
                    inputs.Append($"-i \"{currentAudio}\" ");

                    var filterParts = new List<string>();
                    int idx = 1;

                    foreach (var clip in batch)
                    {
                        inputs.Append($"-i \"{clip.Path}\" ");
                        long delayMs = (long)(clip.Start * 1000);
                        double vol = Math.Max(0.5, Math.Min(clip.Volume / 100.0 * 2.0, 4.0));
                        double clipDur = Math.Max(0.05, clip.Duration > 0 ? clip.Duration : 0.5);

                        double trimEnd = clip.Start + clipDur;
                        filterParts.Add(
                            $"[{idx}:a]" +
                            $"adelay={delayMs}|{delayMs}," +
                            $"atrim=end={trimEnd.ToString("F3", CultureInfo.InvariantCulture)}," +
                            $"volume={vol.ToString("F2", CultureInfo.InvariantCulture)}" +
                            $"[sa{idx}]");
                        idx++;
                    }

                    int numInputs = batch.Count + 1;
                    var mixInputs = "[0:a]" + string.Join("", Enumerable.Range(1, batch.Count).Select(i => $"[sa{i}]"));
                    filterParts.Add($"{mixInputs}amix=inputs={numInputs}:duration=first:normalize=0[aout]");

                    string filterComplex = string.Join("; ", filterParts);
                    string args = $"-nostdin {inputs}-filter_complex \"{filterComplex}\" " +
                                  $"-map \"[aout]\" -c:a aac -b:a 192k -y \"{batchOutput}\"";

                    bool ok = await RunFFmpegAsync(args, ct);

                    if (!ok || !File.Exists(batchOutput) || new FileInfo(batchOutput).Length < 1000)
                    {
                        continue;
                    }

                    if (currentAudio != mainAudioPath && File.Exists(currentAudio))
                        try { File.Delete(currentAudio); } catch { }

                    currentAudio = batchOutput;
                }

                if (currentAudio != outputPath && File.Exists(currentAudio))
                    File.Copy(currentAudio, outputPath, true);

                return File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000
                    ? outputPath : null;
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("re_mix_error", ex.Message));
                return null;
            }
        }

        private string ExtractTextFromName(string name)
        {
            if (name.Contains(":"))
            {
                return name.Substring(name.IndexOf(':') + 1).Trim();
            }
            if (name.StartsWith("Najavni tekst:"))
                return name.Substring("Najavni tekst:".Length).Trim();
            if (name.StartsWith("Odjavni tekst:"))
                return name.Substring("Odjavni tekst:".Length).Trim();
            return name;
        }

        private string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string result = text
                .Replace("\\", "\\\\\\\\")
                .Replace("'", "'\\\\\\''")
                .Replace("\"", "\\\"")
                .Replace(":", "\\:")
                .Replace("@", "\\@")
                .Replace("č", "c")
                .Replace("ć", "c")
                .Replace("š", "s")
                .Replace("đ", "dj")
                .Replace("ž", "z")
                .Replace("Č", "C")
                .Replace("Ć", "C")
                .Replace("Š", "S")
                .Replace("Đ", "Dj")
                .Replace("Ž", "Z");

            return result;
        }

        private async Task<string> CreateSubtitlesFile(List<SubtitleItem> subtitles, string tempDir, double subtitleOffsetSeconds = 0.0)
        {
            try
            {
                if (subtitles == null || subtitles.Count == 0) return null;

                // DRIFT KOMPENZACIJA:
                // subtitleOffsetSeconds kompenzira fade-in i svaki drugi globalni pomak
                // koji FFmpeg unosi između audio i video trake.
                // Ako video počinje s fade-in-om (npr. 1.2s), titlovi moraju biti
                // pomereni za istu vrednost da ostanu "hard-synced" sa glasom.
                //
                // Dodatno: seamless text flow — razmak između titlova max 50ms.
                // Ako titl završava više od 50ms pre nego što sledeći počinje,
                // produžimo End prethodnog do Start sledećeg (bez praznine).

                var sortedSubs = subtitles
                    .OrderBy(s => s.Start)
                    .Select(s => new SubtitleItem
                    {
                        Text  = s.Text,
                        Start = Math.Max(0.0, s.Start + subtitleOffsetSeconds),
                        End   = Math.Max(0.0, s.End   + subtitleOffsetSeconds)
                    })
                    .ToList();

                // FREEZE FIX: Ograniči maksimalno trajanje jednog titla.
                // Whisper ponekad vrati End=start+40s ako ne detektuje kraj segmenta.
                // Max = 8 sekundi po liniji (dovoljno i za najdužu strofu dječije pjesme).
                // Ako je sljedeći titl počinje ranije — koristimo taj granični marker.
                const double MAX_SUBTITLE_DURATION = 8.0;
                for (int i = 0; i < sortedSubs.Count; i++)
                {
                    double maxEnd = sortedSubs[i].Start + MAX_SUBTITLE_DURATION;
                    // Ako postoji sljedeći titl koji počinje ranije od maxEnd — to je prirodni kraj
                    if (i < sortedSubs.Count - 1)
                        maxEnd = Math.Min(maxEnd, sortedSubs[i + 1].Start - 0.050);
                    if (sortedSubs[i].End > maxEnd)
                        sortedSubs[i].End = maxEnd;
                }

                // WHISPER HARD-SYNC: Seamless-flow je ONEMOGUĆEN.
                // Svaki titl živi tačno između svog Whisper Start i End.
                // Tišina ili instrumentalni prijelaz = čist ekran (bez starog teksta).
                // Stari seamless-flow uzrokovao je "frozen text" problem:
                // tekst ostajao vidljiv dugo nakon što glas prestane,
                // a posebno između 01:21 i kraja videa u instrumentalnim dijelovima.

                string srtFile = Path.Combine(tempDir, "subtitles.srt");
                using (var sw = new StreamWriter(srtFile, false, Encoding.UTF8))
                {
                    int index = 1;
                    foreach (var sub in sortedSubs)
                    {
                        if (sub.Start >= sub.End) continue; // preskoci nevažeće
                        sw.WriteLine(index);
                        sw.WriteLine($"{FormatTime(sub.Start)} --> {FormatTime(sub.End)}");
                        sw.WriteLine(sub.Text);
                        sw.WriteLine();
                        index++;
                    }
                }
                return srtFile;
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("re_subtitle_error", ex.Message));
                return null;
            }
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
        }

        private async Task<string> PrepareImageWithMagick(string imagePath, string tempDir)
        {
            try
            {
                if (!File.Exists(imagePath)) return null;
                using (var image = new MagickImage(imagePath))
                {
                    image.Format = MagickFormat.Jpeg;
                    image.Quality = 95;
                    string tempJpg = Path.Combine(tempDir, Guid.NewGuid().ToString() + ".jpg");
                    await image.WriteAsync(tempJpg);
                    return tempJpg;
                }
            }
            catch (Exception ex)
            {
                LogToMainWindow(LF("re_magick_error", imagePath, ex.Message));
                return null;
            }
        }

        private async Task<bool> RunFFmpegAsync(string arguments, CancellationToken ct)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = Path.GetTempPath()
                }
            };

            process.Start();
            process.StandardInput.Close();

            var errorBuilder = new StringBuilder();
            var outputBuilder = new StringBuilder();

            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(ct);

                if (process.ExitCode != 0)
                {
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task<string> RunFFmpegGetOutputAsync(string arguments, CancellationToken ct)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    WorkingDirectory = Path.GetTempPath()
                }
            };

            process.Start();
            process.StandardInput.Close();

            var _re3so = process.StandardOutput.ReadToEndAsync();
            var _re3se = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(_re3so, _re3se);
            await process.WaitForExitAsync(ct);
            string stderr = _re3se.Result;
            string stdout = _re3so.Result;

            return stderr + stdout;
        }

        private static string ExtractTag(string text, string key)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return "";
            foreach (var part in text.Split('|'))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }
            return "";
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  ApplyCrossfadePairwise — robustni crossfade, par-po-par
        //
        //  Zašto je ovo bolje od starog ApplyCrossfade:
        //  Stari: jedan filter_complex za N klipova → offset akumulacija → zeleni frejmovi
        //  Novi: klip[0]+klip[1] → temp1, temp1+klip[2] → temp2, ...
        //         Svaki korak koristi izmjereno trajanje → nema kumulativne greške
        //
        //  Crossfade = overlap: kraj klipa[i] i početak klipa[i+1] se preklapaju.
        //  Efektivno trajanje klipa se smanjuje za fadeDuration (overlap).
        //  Ukupno trajanje videa = sum(dur) - (n-1)*fadeDuration
        // ══════════════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        //  ApplyCrossfadeSinglePass — cross-dissolve u jednom FFmpeg prolazu
        //
        //  Algoritam:
        //  1. Normalizuj sve klipove na isti FPS i format (brzo, bez re-enc kvaliteta)
        //  2. Izmjeri stvarna trajanja normalizovanih klipova
        //  3. Izgradi filter_complex sa xfade filterima i tačnim offset-ima
        //  4. Jedan FFmpeg poziv spaja sve klipove sa cross-dissolve tranzicijama
        //
        //  Zašto je brže od pairwise:
        //  pairwise: N-1 FFmpeg re-enkodiranja sekvencijalno
        //  single-pass: 1 normalizacija (copyts, brzo) + 1 xfade (re-enc jednom)
        //
        //  Limit: FFmpeg filter_complex ima ograničenje na ~60 ulaza.
        //  Ako ima >55 klipova, dijelimo u grupe i spajamo.
        private async Task<string> ApplyCrossfadeSinglePass(
    List<string> videoFiles,
    string tempDir,
    List<double> perClipFades,
    double defaultFade,
    CancellationToken ct,
    List<string> perClipTransitions = null)
        {
            // Helper: get fade for clip i→i+1 pair (use clip[i]'s fade, fallback to default)
            double FadeFor(int i) =>
                (perClipFades != null && i < perClipFades.Count) ? perClipFades[i] : defaultFade;

            // Helper: get transition type for clip i→i+1 pair
            string TransFor(int i) =>
                (perClipTransitions != null && i < perClipTransitions.Count && !string.IsNullOrEmpty(perClipTransitions[i]))
                    ? perClipTransitions[i] : "fade";

            double fadeDuration = defaultFade; // kept for compat in group-recursion

            if (videoFiles == null || videoFiles.Count < 2)
                return null;

            const string TARGET_FPS = "30";
            const string TARGET_RES = "1920:1080";
            const string ENC = "-c:v libx264 -preset veryfast -crf 22 -profile:v high -level 4.1 -pix_fmt yuv420p";

            // fadeStr is now computed per-pair inside the loop using FadeFor(i)

            try
            {
                ct.ThrowIfCancellationRequested();

                if (videoFiles.Count > 50)
                {
                    var groups = new List<List<string>>();
                    for (int i = 0; i < videoFiles.Count; i += 40)
                        groups.Add(videoFiles.Skip(i).Take(40).ToList());

                    var groupResults = new List<string>();
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Slice perClipFades for this group
                        int groupStart = gi * 40;
                        var groupFades = perClipFades?.Skip(groupStart).Take(groups[gi].Count).ToList();

                        string groupOut = await ApplyCrossfadeSinglePass(
                            groups[gi],
                            tempDir,
                            groupFades,
                            defaultFade,
                            ct);

                        if (!string.IsNullOrEmpty(groupOut) &&
                            File.Exists(groupOut) &&
                            new FileInfo(groupOut).Length > 1000)
                        {
                            groupResults.Add(groupOut);
                        }
                        else
                        {
                            groupResults.Add(groups[gi][0]);
                        }
                    }

                    return await ApplyCrossfadeSinglePass(groupResults, tempDir, null, defaultFade, ct);
                }

                var normalized = new List<string>();

                for (int i = 0; i < videoFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    string input = videoFiles[i];
                    if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                        continue;

                    string normPath = Path.Combine(tempDir, $"xfsp_n{i:D4}.mp4");
                    string vf =
                        $"fps=fps={TARGET_FPS}:round=near," +
                        $"scale={TARGET_RES}:force_original_aspect_ratio=decrease," +
                        $"pad={TARGET_RES}:(ow-iw)/2:(oh-ih)/2," +
                        $"setsar=1,format=yuv420p";

                    string normArgs =
                        $"-nostdin -i \"{input}\" " +
                        $"-vf \"{vf}\" " +
                        $"{ENC} -an -y \"{normPath}\"";

                    bool ok = await RunFFmpegAsync(normArgs, ct);

                    if (ok && File.Exists(normPath) && new FileInfo(normPath).Length > 500)
                        normalized.Add(normPath);
                    else
                        normalized.Add(input);
                }

                if (normalized.Count < 2)
                    return null;

                var durations = new List<double>();
                foreach (var file in normalized)
                {
                    ct.ThrowIfCancellationRequested();
                    double dur = await GetVideoDuration(file, ct);
                    durations.Add(dur);
                }

                if (durations.Any(d => d <= 0.05))
                {
                    LogToMainWindow("   ⚠️ CrossfadeSinglePass: neispravno trajanje jednog od klipova");
                    return null;
                }

                string inputs = string.Join(" ", normalized.Select(f => $"-i \"{f}\""));

                var fc = new StringBuilder();
                string prevLabel = "[0:v]";
                double timelinePos = 0.0;

                for (int i = 1; i < normalized.Count; i++)
                {
                    double prevDur = durations[i - 1];
                    double thisFade = FadeFor(i - 1); // fade za par [i-1]→[i]
                    string fadeStr = thisFade.ToString("F3", CultureInfo.InvariantCulture);

                    if (prevDur <= thisFade + 0.05)
                    {
                        LogToMainWindow($"   ⚠️ CrossfadeSinglePass: klip {i - 1} prekratak za fade ({prevDur:F2}s ≤ {thisFade:F2}s)");
                        return null;
                    }

                    double offset = timelinePos + (prevDur - thisFade);
                    string offsetStr = offset.ToString("F3", CultureInfo.InvariantCulture);
                    string nextLabel = i < normalized.Count - 1 ? $"[v{i:D3}]" : "[vfinal]";
                    string transType = TransFor(i - 1);

                    fc.Append(
                        $"{prevLabel}[{i}:v]xfade=transition={transType}:duration={fadeStr}:offset={offsetStr}{nextLabel};");

                    prevLabel = nextLabel;
                    timelinePos = offset;
                }

                string filterComplex = fc.ToString().TrimEnd(';');
                string outPath = Path.Combine(tempDir, $"xfsp_final_{Guid.NewGuid():N}.mp4");

                string xfArgs =
                    $"-nostdin {inputs} " +
                    $"-filter_complex \"{filterComplex}\" " +
                    $"-map \"[vfinal]\" " +
                    $"{ENC} -an -y \"{outPath}\"";

                bool xfOk = await RunFFmpegAsync(xfArgs, ct);

                if (xfOk && File.Exists(outPath) && new FileInfo(outPath).Length > 1000)
                    return outPath;

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogToMainWindow($"   ⚠️ CrossfadeSinglePass greška: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ApplyCrossfadePairwise(
            List<string> videoFiles,
            string tempDir,
            List<double> perClipFades,
            double defaultFade,
            CancellationToken ct)
        {
            double FadeFor(int i) =>
                (perClipFades != null && i < perClipFades.Count) ? perClipFades[i] : defaultFade;

            if (videoFiles == null || videoFiles.Count < 2)
                return null;

            const string TARGET_FPS = "30";
            const int TARGET_W = 1920;
            const int TARGET_H = 1080;

            string encArgs = vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 22 -profile:v high -level 4.1";
            string pixFmt = "-pix_fmt yuv420p";
            // fadeStr computed per-pair in loop via FadeFor(i)

            var normalized = new List<string>();

            for (int i = 0; i < videoFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                string input = videoFiles[i];
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                    continue;

                string normPath = Path.Combine(tempDir, $"xf_norm_{i:D4}.mp4");
                string vf =
                    $"fps=fps={TARGET_FPS}:round=near," +
                    $"scale={TARGET_W}:{TARGET_H}:force_original_aspect_ratio=decrease," +
                    $"pad={TARGET_W}:{TARGET_H}:(ow-iw)/2:(oh-ih)/2," +
                    $"setsar=1,format=yuv420p";

                string normArgs =
                    $"-nostdin -i \"{input}\" " +
                    $"-vf \"{vf}\" " +
                    $"{encArgs} {pixFmt} -an -y \"{normPath}\"";

                bool ok = await RunFFmpegAsync(normArgs, ct);

                if (!ok || !File.Exists(normPath) || new FileInfo(normPath).Length < 500)
                {
                    LogToMainWindow($"   ⚠️ Crossfade norm fail za klip {i} — koristim original");
                    normalized.Add(input);
                }
                else
                {
                    normalized.Add(normPath);
                }
            }

            if (normalized.Count < 2)
                return null;

            string current = normalized[0];
            bool atLeastOneTransitionSucceeded = false;

            for (int i = 1; i < normalized.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                string next = normalized[i];
                double currentDur = await GetVideoDuration(current, ct);
                double nextDur = await GetVideoDuration(next, ct);
                double thisFade = FadeFor(i - 1); // fade dla par [i-1]→[i]
                string fadeStr = thisFade.ToString("F3", CultureInfo.InvariantCulture);

                if (currentDur <= thisFade + 0.05 || nextDur <= thisFade + 0.05)
                {
                    string concatPath = Path.Combine(tempDir, $"xf_concat_{i:D4}.mp4");
                    string concatList = Path.Combine(tempDir, $"xf_list_{i:D4}.txt");

                    await File.WriteAllTextAsync(
                        concatList,
                        $"file '{current.Replace("\\", "/")}'{Environment.NewLine}" +
                        $"file '{next.Replace("\\", "/")}'",
                        Encoding.UTF8,
                        ct);

                    string concatArgs =
                        $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                        $"-c:v copy -an -y \"{concatPath}\"";

                    bool concatOk = await RunFFmpegAsync(concatArgs, ct);
                    if (concatOk && File.Exists(concatPath) && new FileInfo(concatPath).Length > 500)
                        current = concatPath;

                    continue;
                }

                double offset = Math.Max(0.1, currentDur - thisFade);
                string offsetStr = offset.ToString("F3", CultureInfo.InvariantCulture);
                string outPath = Path.Combine(tempDir, $"xf_pair_{i:D4}.mp4");

                string xfadeArgs =
                    $"-nostdin " +
                    $"-i \"{current}\" " +
                    $"-i \"{next}\" " +
                    $"-filter_complex " +
                    $"\"[0:v][1:v]xfade=transition=fade:duration={fadeStr}:offset={offsetStr}[vout]\" " +
                    $"-map \"[vout]\" " +
                    $"{encArgs} {pixFmt} -an -y \"{outPath}\"";

                bool xok = await RunFFmpegAsync(xfadeArgs, ct);

                if (xok && File.Exists(outPath) && new FileInfo(outPath).Length > 1000)
                {
                    current = outPath;
                    atLeastOneTransitionSucceeded = true;
                }
                else
                {
                    LogToMainWindow($"   ⚠️ xfade par {i - 1}→{i} nije uspio — hard cut");

                    string concatPath = Path.Combine(tempDir, $"xf_hc_{i:D4}.mp4");
                    string concatList = Path.Combine(tempDir, $"xf_hcl_{i:D4}.txt");

                    await File.WriteAllTextAsync(
                        concatList,
                        $"file '{current.Replace("\\", "/")}'{Environment.NewLine}" +
                        $"file '{next.Replace("\\", "/")}'",
                        Encoding.UTF8,
                        ct);

                    string hcArgs =
                        $"-nostdin -f concat -safe 0 -i \"{concatList}\" " +
                        $"-c:v copy -an -y \"{concatPath}\"";

                    bool hok = await RunFFmpegAsync(hcArgs, ct);
                    if (hok && File.Exists(concatPath) && new FileInfo(concatPath).Length > 500)
                        current = concatPath;
                }
            }

            if (!atLeastOneTransitionSucceeded && videoFiles.Count > 1)
                return null;

            return current;
        }

        private async Task<string> ApplyCrossfade(
            List<string> videoFiles,
            string outputPath,
            List<double> fadeDurations,
            CancellationToken ct)
        {
            if (videoFiles == null || videoFiles.Count < 2)
                return null;

            var durations = new List<double>();
            foreach (var vf in videoFiles)
            {
                ct.ThrowIfCancellationRequested();

                double dur = await GetVideoDuration(vf, ct);
                if (dur <= 0.05)
                    dur = 5.0;

                durations.Add(dur);
            }

            var inputs = new StringBuilder();
            var filterParts = new StringBuilder();
            double runningOffset = 0.0;

            for (int i = 0; i < videoFiles.Count; i++)
                inputs.Append($"-i \"{videoFiles[i]}\" ");

            const string FPS_NORM = "fps=fps=30:round=near";

            for (int i = 0; i < videoFiles.Count; i++)
                filterParts.Append($"[{i}:v]{FPS_NORM}[f{i}];");

            string lastLabel = "f0";

            for (int i = 1; i < videoFiles.Count; i++)
            {
                double localFadeDuration = (fadeDurations != null && i - 1 < fadeDurations.Count)
                    ? fadeDurations[i - 1]
                    : 0.6;

                localFadeDuration = Math.Max(0.1, localFadeDuration);

                double prevDur = durations[i - 1];
                runningOffset += Math.Max(0.1, prevDur - localFadeDuration);

                string outLabel = (i == videoFiles.Count - 1) ? "vout" : $"v{i}";
                string offsetStr = runningOffset.ToString("F3", CultureInfo.InvariantCulture);
                string fadeStr = localFadeDuration.ToString("F3", CultureInfo.InvariantCulture);

                filterParts.Append(
                    $"[{lastLabel}][f{i}]xfade=transition=fade:" +
                    $"duration={fadeStr}:offset={offsetStr}[{outLabel}];");

                lastLabel = outLabel;
            }

            string filterComplex = filterParts.ToString().TrimEnd(';');
            string encArgs = vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 20 -profile:v high -level 4.1";

            string args =
                $"-nostdin {inputs}" +
                $"-filter_complex \"{filterComplex}\" " +
                $"-map \"[vout]\" " +
                $"{encArgs} " +
                $"-pix_fmt yuv420p -an -y \"{outputPath}\"";

            bool ok = await RunFFmpegAsync(args, ct);
            return ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1000
                ? outputPath
                : null;
        }

        private string vEncArgs_cached = null;

        private void LogToMainWindow(string message)
        {
            try
            {
                if (WpfApp.Current != null && WpfApp.Current.Dispatcher != null)
                {
                    WpfApp.Current.Dispatcher.Invoke(() =>
                    {
                        if (WpfApp.Current.MainWindow is MainWindow main)
                            main.LogMessage(message, true);
                    });
                }
            }
            catch
            {
            }
        }
    }

    public static class RenderEngineBeatLock
    {
        private const string TARGET_FPS = "30";
        private const double CUT_ADVANCE_S = 0.080;

        public static double BeatSpeedFactor(
            BeatInfo beatInfo,
            double clipMotionMagnitude,
            double clipDurationSeconds,
            int sceneEnergy)
        {
            if (beatInfo == null || !beatInfo.IsValid) return 1.0;
            if (clipMotionMagnitude <= 0) return 1.0;
            if (clipDurationSeconds <= 0) return 1.0;
            if (beatInfo.BPM <= 0.01) return 1.0;

            double fps = 30.0;
            double beatsPerSec = beatInfo.BPM / 60.0;
            double framesPerBeat = fps / beatsPerSec;
            double motionFreq = fps / Math.Max(1.0, clipMotionMagnitude);

            double rawFactor = framesPerBeat / motionFreq;

            double energyBias = sceneEnergy >= 4
                ? 0.92
                : sceneEnergy <= 2
                    ? 1.08
                    : 1.0;

            rawFactor *= energyBias;

            return Math.Max(0.75, Math.Min(1.33, rawFactor));
        }

        public static List<double> GetCutPointsSnappedToDownBeats(
            BeatInfo beatInfo,
            double clipAbsoluteStart,
            double clipAbsoluteEnd)
        {
            if (beatInfo == null || !beatInfo.IsValid)
                return new List<double>();

            var downBeats = (beatInfo.DownBeats?.Count > 0)
                ? beatInfo.DownBeats
                : beatInfo.BeatTimes;

            if (downBeats == null || downBeats.Count == 0)
                return new List<double>();

            return downBeats
                .Where(b => b > clipAbsoluteStart + 0.3 && b < clipAbsoluteEnd - 0.3)
                .Select(b => Math.Round(b - CUT_ADVANCE_S, 3))
                .ToList();
        }

        public static string BuildBeatLockedVideoFilter(
            TimelineItem item,
            BeatInfo beatInfo,
            int targetWidth,
            int targetHeight,
            string fpsSetting,
            string moodFilter,
            string colorMatchFilter,
            string wwbFilter,
            bool isStaticClip,
            int sceneEnergy,
            double clipMotionMagnitude = 0.0)
        {
            double speedFactor = BeatSpeedFactor(
                beatInfo,
                clipMotionMagnitude,
                item.Duration,
                sceneEnergy);

            bool needsSetpts = Math.Abs(speedFactor - 1.0) > 0.03;
            string setptsFilter = needsSetpts
                ? $"setpts={speedFactor.ToString("F4", CultureInfo.InvariantCulture)}*PTS"
                : "";

            string scaleFilter = $"scale={targetWidth}:{targetHeight}:flags=lanczos";
            string baseNormalize = $"fps=fps={fpsSetting}:round=near,format=yuv420p";

            string motionFilter;
            if (isStaticClip && item.Duration >= 2.0)
            {
                int staticFrames = Math.Max(1, (int)(item.Duration * 30.0));
                int overWs = (int)(targetWidth * 1.12);
                int overHs = (int)(targetHeight * 1.12);

                if (overWs % 2 != 0) overWs++;
                if (overHs % 2 != 0) overHs++;

                int midXs = (overWs - targetWidth) / 2;
                int midYs = (overHs - targetHeight) / 2;

                motionFilter =
                    $"scale={overWs}:{overHs}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:{midXs}*n/{staticFrames}:{midYs}*n/{staticFrames}";
            }
            else if (!isStaticClip && item.Duration >= 3.0 && item.Duration <= 12.0)
            {
                double overscanFactor = sceneEnergy >= 4 ? 1.20 : sceneEnergy <= 2 ? 1.10 : 1.15;
                int overW = (int)(targetWidth * overscanFactor);
                int overH = (int)(targetHeight * overscanFactor);

                if (overW % 2 != 0) overW++;
                if (overH % 2 != 0) overH++;

                int maxX = overW - targetWidth;
                int maxY = overH - targetHeight;
                int midX = maxX / 2;
                int midY = maxY / 2;

                motionFilter =
                    $"scale={overW}:{overH}:flags=lanczos," +
                    $"crop={targetWidth}:{targetHeight}:{midX}:{midY}";
            }
            else
            {
                motionFilter = scaleFilter;
            }

            string warmthBoost = sceneEnergy >= 4
                ? "curves=r='0/0 0.5/0.56 1/1':g='0/0 0.5/0.54 1/1'"
                : sceneEnergy <= 2
                    ? "curves=r='0/0 0.5/0.52 1/1':g='0/0 0.5/0.515 1/1'"
                    : "curves=r='0/0 0.5/0.54 1/1':g='0/0 0.5/0.525 1/1'";

            var filterParts = new List<string>();

            if (!string.IsNullOrEmpty(setptsFilter))
                filterParts.Add(setptsFilter);

            filterParts.Add(motionFilter);
            filterParts.Add(warmthBoost);

            if (!string.IsNullOrEmpty(moodFilter))
                filterParts.Add(moodFilter);

            if (!string.IsNullOrEmpty(colorMatchFilter))
                filterParts.Add(colorMatchFilter.TrimStart(','));

            if (!string.IsNullOrEmpty(wwbFilter))
                filterParts.Add(wwbFilter.TrimStart(','));

            filterParts.Add(baseNormalize);
            filterParts.Add("fade=t=in:st=0:d=0.3");
            // FIX-DENOISE: isti filter kao i u glavnom render loopu — ujednačava teksturu klipova
            filterParts.Add("hqdn3d=1.5:1.5:6:6,unsharp=3:3:0.4:3:3:0.0");

            return string.Join(",", filterParts.Where(f => !string.IsNullOrWhiteSpace(f)));
        }

        public static string BuildFFmpegArgsForBeatLockedClip(
            TimelineItem item,
            BeatInfo beatInfo,
            int targetWidth,
            int targetHeight,
            string vEncArgs,
            string pixFmt,
            string vsyncCfr,
            string tempVideo,
            string moodFilter = "",
            string colorMatchFilter = "",
            string wwbFilter = "",
            bool isStaticClip = false,
            int sceneEnergy = 3,
            double clipMotionMag = 0.0)
        {
            string videoVf = BuildBeatLockedVideoFilter(
                item,
                beatInfo,
                targetWidth,
                targetHeight,
                TARGET_FPS,
                moodFilter,
                colorMatchFilter,
                wwbFilter,
                isStaticClip,
                sceneEnergy,
                clipMotionMag);

            string durationStr = item.Duration.ToString(CultureInfo.InvariantCulture);

            return $"-nostdin -stream_loop -1 -t {durationStr} " +
                   $"-i \"{item.Path}\" " +
                   $"-vf \"{videoVf}\" " +
                   $"{vEncArgs} {vsyncCfr} {pixFmt} -an -y \"{tempVideo}\"";
        }
    }
}