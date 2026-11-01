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

        // Ne menja ponašanje (i dalje se "gutaju" ove greške, namerno — nisu
        // dovoljno kritične da prekinu render), ali barem ostavljaju trag da
        // se sledeći put ne nagađa u mraku ako se nešto ponavlja.
        private static void LogSwallowedError(string context, Exception ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraVideoEditor");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "render_errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{context}] {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* ako i samo logovanje pukne, nema šta drugo da se uradi */ }
        }

        private static string _LangCode => LanguageManager.CurrentLanguage ?? "en";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public RenderEngine(bool useHardwareAcceleration = true)
        {
            _ffmpegPath = AppPaths.FFmpeg;
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
            // v1.4: a fresh error log for this render, and the GPU encoder gets a new chance
            try { if (File.Exists(LastErrorLogPath)) File.Delete(LastErrorLogPath); } catch { /* still open elsewhere — appended to */ }
            _useCpuEncoder = false;

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
            // v1.4: the quality chosen in Export options is used (it was ignored)
            string quality = exportSettings?.Quality ?? "Medium";
            // v2.0: "Maximum" — near-lossless intermediates for archive / ProRes / further editing
            int crf = quality == "Maximum" ? 14 : quality == "High" ? 18 : quality == "Low" ? 28 : 23;
            string x264Preset = quality == "Maximum" ? "slow" : quality == "High" ? "medium" : "veryfast";
            string nvPreset = quality == "Maximum" ? "p6" : quality == "High" ? "p4" : "p2";
            string vEncArgs = nvencAvailable
                ? $"-c:v h264_nvenc -preset {nvPreset} -rc vbr -cq {crf} -b:v 0 -profile:v high -level 5.1"
                : $"-c:v libx264 -preset {x264Preset} -crf {crf} -profile:v high -level 5.1";
            string pixFmt = "-pix_fmt yuv420p";
            _xfEnc = $"-c:v libx264 -preset {(quality == "Maximum" ? "medium" : "veryfast")} -crf {Math.Min(22, crf)} -profile:v high -level 5.1 -pix_fmt yuv420p";

            // v2.0: clips on the layer tracks (Video 2 …) are not part of the main picture —
            // they are drawn on top of it at the very end (LayerCompositor). Only when there is
            // no main picture at all are they rendered as the main track, as before.
            // v2.0: a hidden layer track is switched off — its picture and its clips' own sound
            items = items.Where(i => !TrackSet.IsHidden(i.TrackIndex)).ToList();
            var layerItems = items.Where(IsLayerClip).ToList();
            if (layerItems.Count > 0 && items.Any(i => !layerItems.Contains(i) && !(i.Type == "Audio" || i.IsAudio)))
                items = items.Where(i => !layerItems.Contains(i)).ToList();
            else
                layerItems.Clear();

            // v2.0: the frame rate follows the source video (or the one chosen in Export options);
            // it was always 30 fps. AI music videos keep 30.
            string TARGET_FPS = await ResolveFrameRateAsync(exportSettings, items, cancellationToken);
            _fps = TARGET_FPS;
            RenderEngineBeatLock.TargetFps = TARGET_FPS;
            // v2.0: H.264 level 5.1 allows 4K only up to 30 fps; NVENC refuses to start above it.
            // 5.2 covers 4K at 60 fps; above 60 fps the encoder chooses the level itself.
            double fpsValue = LayerCompositor.ParseRate(TARGET_FPS);
            if (fpsValue > 60.5)
            {
                vEncArgs = vEncArgs.Replace(" -level 5.1", "");
                _xfEnc = _xfEnc.Replace(" -level 5.1", "");
            }
            else if (fpsValue > 30.5)
            {
                vEncArgs = vEncArgs.Replace("-level 5.1", "-level 5.2");
                _xfEnc = _xfEnc.Replace("-level 5.1", "-level 5.2");
            }
            const string VSYNC_CFR = "-fps_mode cfr";
            string fpsSuffix = $",fps={TARGET_FPS}";
            LogToMainWindow(LanguageManager.F("lx_render_encoder_fast_render_fps_98c8e", (nvencAvailable ? "h264_nvenc (GPU)" : "libx264 (CPU)"), fastRender, TARGET_FPS));
            vEncArgs_cached = vEncArgs;

            if (!File.Exists(_ffmpegPath))
            {
                LogToMainWindow(L("re_ffmpeg_missing"));
                throw new FileNotFoundException(LF("re_ffmpeg_path", _ffmpegPath));
            }

            var sortedItems = items.OrderBy(i => i.Start).ToList();

            LogToMainWindow(LanguageManager.F("lx_render_clips_before_sorting_out_352b7", sortedItems.Count));

            foreach (var item in sortedItems)
            {
                LogToMainWindow(LanguageManager.F("lx_clip_type_name_file_441d6", item.Type, item.Name, (string.IsNullOrEmpty(item.Path) ? LanguageManager.T("lx_no_file") : item.Path)));
            }

            var allImageItems = sortedItems.Where(i => i.Type == "Image" || (i.IsImage && !i.IsAudio)).ToList();
            LogToMainWindow(LanguageManager.F("lx_render_picture_clips_in_total_db36e", allImageItems.Count));

            var images = allImageItems.Where(i =>
                !string.IsNullOrEmpty(i.Path) &&
                File.Exists(i.Path) &&
                !i.Name.Contains("Intro") &&
                !i.Name.Contains("Outro")).ToList();

            var textImages = allImageItems.Where(i =>
                string.IsNullOrEmpty(i.Path) ||
                i.Name.Contains("Intro") ||
                i.Name.Contains("Outro")).ToList();

            var audio = sortedItems
                .Where(i => (i.Type == "Audio" || i.IsAudio) &&
                            !i.Name.StartsWith("🔊"))
                .OrderByDescending(i => i.Duration)
                .FirstOrDefault()
                ?? sortedItems.FirstOrDefault(i => i.Type == "Audio" || i.IsAudio);
            var videos = sortedItems.Where(i => i.Type == "Video" || i.IsVideo).ToList();

            LogToMainWindow(LF("re_found_media", images.Count, textImages.Count, videos.Count));
            LogToMainWindow(LanguageManager.F("lx_render_resolution_4510b", resolution));
            LogToMainWindow(LanguageManager.F("lx_render_clips_put_in_timeline_da7fd", sortedItems.Count));

            if (images.Count == 0 && videos.Count == 0 && textImages.Count == 0)
                throw new Exception("No images, text or video to render");

            string tempDir = Path.Combine(Path.GetTempPath(), "UVE_Render_") + Guid.NewGuid().ToString().Substring(0, 8);
            Directory.CreateDirectory(tempDir);
            LogToMainWindow(LanguageManager.F("lx_render_temporary_folder_8fbe1", tempDir));

            string[] res = resolution.Split('x');
            int targetWidth = int.Parse(res[0]);
            int targetHeight = int.Parse(res[1]);
            _xfW = targetWidth % 2 == 0 ? targetWidth : targetWidth + 1;
            _xfH = targetHeight % 2 == 0 ? targetHeight : targetHeight + 1;

            try
            {
                var videoFiles = new List<string>();
                var itemToFile = new Dictionary<TimelineItem, string>();
                var fadeDurations = new List<double>(); // pacing-aware fade po klipu
                var transitionTypes = new List<string>(); // xfade tip po klipu (fade/wipeleft/slideleft/...)

                int total = sortedItems.Count(i => i.Type == "Image" || i.Type == "Video" || i.IsVideo || i.IsAudio == false);
                int current = 0;
                int fileIdx = 0;

                // ── v1.4: timeline slots ─────────────────────────────────────────────
                // Visual clips are played one after another, so each one is given the
                // time until the NEXT visual clip starts. That keeps gaps and overlaps on
                // the timeline exactly where they are (lyric-timed AI scenes, fixed
                // positions, auto-arranged slideshows) instead of packing clips together.
                var visualOrder = sortedItems.Where(i => !(i.Type == "Audio" || i.IsAudio)).ToList();
                var slotDurations = new Dictionary<TimelineItem, double>();
                var skipOverlapping = new HashSet<TimelineItem>();
                for (int vi = 0; vi < visualOrder.Count; vi++)
                {
                    var it = visualOrder[vi];
                    if (vi > 0 && Math.Abs(it.Start - visualOrder[vi - 1].Start) < 0.05 && !skipOverlapping.Contains(visualOrder[vi - 1]))
                    {
                        skipOverlapping.Add(it);
                        continue;
                    }
                    var next = visualOrder.Skip(vi + 1).FirstOrDefault(n => n.Start > it.Start + 0.05);
                    slotDurations[it] = next != null ? next.Start - it.Start : it.Duration;
                }

                foreach (var item in sortedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // v1.4: decide by file type too, not only by Type — "Animation", "Highlight",
                    // "AI image" etc. items used to be skipped silently by the render.
                    bool isAudio = item.Type == "Audio" || item.IsAudio;
                    bool isImage = !isAudio && (item.Type == "Image" || item.IsImage || string.IsNullOrEmpty(item.Path));
                    bool isVideo = !isAudio && !isImage && (item.Type == "Video" || item.IsVideo || item.Type == "Highlight");
                    bool isTextItem = isImage && (string.IsNullOrEmpty(item.Path) ||
                                                  item.Name.Contains("Intro") ||
                                                  item.Name.Contains("Outro") ||
                                                  (item.Path != null && !File.Exists(item.Path)));

                    if (isAudio) continue;

                    // v1.4: an item that starts at the same moment as an earlier one (overlap)
                    // cannot be shown in a single-track render — skip it instead of pushing
                    // everything after it later in time.
                    if (skipOverlapping.Contains(item))
                    {
                        LogToMainWindow(LanguageManager.F("lx_starts_at_the_same_time_88a28", item.Name));
                        continue;
                    }

                    string tempVideo = Path.Combine(tempDir, $"clip_{fileIdx++:D4}.mp4");

                    // v1.4 TIMELINE SYNC: every clip lasts exactly until the next clip starts
                    // on the timeline (its "slot"). Before, clips were rendered back to back
                    // with their own durations, minus a 0.15 s "freeze fix" trim, minus the
                    // crossfade overlap — gaps on the timeline (e.g. between lyric lines in the
                    // AI music video) were lost and the picture drifted ahead of the music by
                    // several seconds over a song. Crossfades are now compensated separately
                    // (ApplyCrossfade*: the outgoing clip is extended by the fade length).
                    double effectiveDuration = slotDurations.TryGetValue(item, out double slotDur)
                        ? slotDur : item.Duration;
                    effectiveDuration = Math.Max(0.2, effectiveDuration);
                    if (Math.Abs(effectiveDuration - item.Duration) > 0.05)
                        LogToMainWindow(LanguageManager.F("lx_f_s_f_s_until_4fcef", (item.Name ?? "").Substring(0, Math.Min(30, (item.Name ?? "").Length)), item.Duration, effectiveDuration));
                    // Where in the source file the clip begins (cut parts, highlights)
                    string sourceSeekArg = item.SourceOffset > 0.01
                        ? $"-ss {item.SourceOffset.ToString("F3", CultureInfo.InvariantCulture)} "
                        : "";
                    string durationStr = effectiveDuration.ToString("0.###", CultureInfo.InvariantCulture);
                    bool success = false;

                    if (isTextItem)
                    {
                        LogToMainWindow(LanguageManager.F("lx_render_text_layer_start_f_5af36", item.Name, item.Start, item.Duration));

                        bool isOdjavniTekst = item.Name.StartsWith("Outro text");

                        if (isOdjavniTekst)
                        {
                            // OUTRO FIX (isTextItem branch) — generate black canvas directly.
                            // Outro uvijek ulazi ovdje jer isTextItem=true (Name.Contains("Outro")).
                            // Bypass Magick/pad pipeline which can introduce a yellow padding artifact.
                            // v1.4: the outro text always comes from the name ("Outro text: ..."); before,
                            // an outro without a picture showed the literal words "Outro text".
                            string outroRaw = ExtractTextFromName(item.Name);

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
                                    $"drawtext={DtFont}text='{el}':fontcolor=white@0.95:fontsize={oFontSize}:" +
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
                            int fontSize = item.Name.Contains("Intro") ? 60 : 34;

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
                                                  $"-vf \"drawtext={DtFont}text='{escapedText}':fontcolor=white:fontsize={fontSize}:x=(w-text_w)/2:y=(h-text_h)/2:borderw=5:bordercolor=black:shadowx=3:shadowy=3:shadowcolor=black@0.8{fpsSuffix}\" " +
                                                  $"{vEncArgs} {VSYNC_CFR} {pixFmt} -y \"{tempVideo}\"";
                                success = await RunFFmpegAsync(argsText, cancellationToken);
                            }
                        }
                    }
                    else if (isImage)
                    {
                        if (!File.Exists(item.Path)) continue;

                        bool isNaslov = item.Name.StartsWith("Title:");
                        bool isOdjava = item.Name.StartsWith("Outro text");
                        double dur = item.Duration;

                        if (isOdjava)
                        {
                            // OUTRO FIX: Directly generate black canvas using FFmpeg color source.
                            // Reason: PrepareImageWithMagick + pad filter can introduce yellow/default
                            // padding when PNG dimensions do not exactly match the target resolution.
                            // FFmpeg 'color=c=black' guarantees a clean black background with no artifacts.
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
                                    $"drawtext={DtFont}text='{el}':fontcolor=white@0.95:fontsize={oFontSize}:" +
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

                            // v1.4: brightness, contrast and blur from the Effects tab reach photos too
                            string imgFx = VideoEffects.GetFFmpegFilter(item.VideoEffect, includeSpeed: false);
                            string imgVf = scaleF + (string.IsNullOrEmpty(imgFx) ? "" : "," + imgFx) + fadeFilter + fpsSuffix;
                            string argsImg = $"-nostdin -loop 1 -r {TARGET_FPS} -i \"{preparedImage}\" {vEncArgs} -t {durationStr} {VSYNC_CFR} {pixFmt} -vf \"{imgVf}\" -y \"{tempVideo}\"";
                            success = await RunFFmpegAsync(argsImg, cancellationToken);
                        }
                    }
                    else if (isVideo)
                    {
                        if (!File.Exists(item.Path)) continue;

                        // Aspect ratio: scale-to-fill + center crop — nema crnih rubova, nema stretch-a.
                        // Excess is cropped from the edges (always center frame), which is the standard TV/cinema approach.
                        string scaleFilter =
                            $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase:flags=lanczos," +
                            $"crop={targetWidth}:{targetHeight}:(iw-{targetWidth})/2:(ih-{targetHeight})/2";
                        // v1.4: ONE gentle grade per clip. Before, the same warm tint and saturation
                        // boost were stacked four times (base + mood + "LUT" + season): saturation
                        // ended around 1.5x and skin tones went orange/pink.
                        string baseNormalize = "eq=brightness=0.02:saturation=1.05:contrast=1.02,format=yuv420p";

                        string moodFilter = "";

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
                            ? ",curves=r='0/0 0.5/0.55 1/1':g='0/0 0.5/0.52 1/1':b='0/0 0.5/0.47 1/1'"
                            : "";

                        // FIX-SEASONAL: Per-kadar sezonski color grading na osnovu season taga iz stiha
                        // "When it's winter wear boots" → cool blue tones on THAT frame
                        // "Leti kupite sladoled"   → topli zlatni toni na TOM kadru
                        // season tag se upisuje u MoodTag od AIVideoCreator po stihu
                        string lyricSeasonTag = ExtractTag(audioDesc2, "season");
                        string seasonalGradeFilter = lyricSeasonTag switch
                        {
                            "winter" => // Cool blue tones — snow, boots, spacesuit
                                ",curves=r='0/0 0.5/0.46 1/0.92':g='0/0 0.5/0.50 1/0.96':b='0/0 0.5/0.57 1/1'" +
                                ",eq=saturation=0.88:contrast=1.05",
                            "summer" => // Topli zlatni toni — sladoled, sunce, leto
                                ",curves=r='0/0 0.5/0.57 1/1':g='0/0 0.5/0.54 1/1':b='0/0 0.5/0.44 1/0.88'" +
                                ",eq=saturation=1.18:contrast=1.02",
                            "spring" => // Fresh green-yellow tones — spring, blossoms
                                ",curves=r='0/0 0.5/0.53 1/1':g='0/0 0.5/0.56 1/1':b='0/0 0.5/0.48 1/0.94'" +
                                ",eq=saturation=1.12:contrast=1.01",
                            "autumn" => // Warm orange-brown tones — autumn, leaves
                                ",curves=r='0/0 0.5/0.58 1/1':g='0/0 0.5/0.52 1/0.97':b='0/0 0.5/0.43 1/0.86'" +
                                ",eq=saturation=1.10:contrast=1.03",
                            _ => "" // Bez sezonskog filtera ako sezona nije detektovana
                        };

                        bool needsSlowMoFix = audioDesc2.Contains("slowmo=1");
                        string fpsNormalize = needsSlowMoFix
                            ? $",minterpolate=fps={TARGET_FPS}:mi_mode=mci:mc_mode=aobmc:me=hexbs:vsbmc=1"
                            : "";

                        // ── GLOBALNA COLOR NORMALIZACIJA ─────────────────────────────────────
                        // Svaki klip se normalizuje na ISTI target (ne relativno na anchor klip)
                        // Target: brightness=0.54, contrast=1.04, saturation=1.12
                        // This eliminates "TV channel" jumps between clips with different exposure
                        // Additional: LUT-style warm tint for children's video (subtle warmth)
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
                        // Global "children's video" LUT: warm contrast, vivid but not neon colors
                        // eq: contrast=1.04 (slight contrast), saturation=1.12 (vivid colors)
                        // curves: blagi warm tint (R+, G+, B-)
                        string satClampFilter = ""; // v1.4: the warm tint is already in warmthBoost
                        colorMatchFilter = colorMatchFilter + satClampFilter;

                        // FIX-DENOISE: Texture normalization between clips from different cameras/quality
                        // hqdn3d=1.5:1.5:6:6 — gentle temporal+spatial denoise (preserves detail, removes noise only)
                        // unsharp=3:3:0.4:3:3:0.0 — slightly sharpens luminance (Y), not colors (no artifacts added)
                        // Result: clips with noise (phone, night, compression) look like the rest of the video
                        // Values are conservative — for children's video, larger values look artificial
                        const string DENOISE_FILTER = ",hqdn3d=1.5:1.5:6:6,unsharp=3:3:0.4:3:3:0.0";

                        // PATCH-HASCHILDREN: Contrast boost za playground scene sa djecom
                        // Gemini preporuka: eq=contrast=1.08 bolje izdvaja djecu od pozadine
                        bool hasChildrenClip = audioDesc2.Contains("haschildren=1");
                        string childrenContrastFilter = hasChildrenClip ? ",eq=contrast=1.08" : "";

                        baseNormalize = $"{baseNormalize},{warmthBoost}";

                        // COLOR GRADING ENGINE (Phase 4C): read [grade:...] tag from ContentTag
                        string gradeFilter = "";
                        {
                            string ctag = item.ContentTag ?? "";
                            var gm = System.Text.RegularExpressions.Regex.Match(
                                ctag, @"\[grade:([^\]]+)\]");
                            if (gm.Success && !string.IsNullOrWhiteSpace(gm.Groups[1].Value))
                                gradeFilter = "," + gm.Groups[1].Value;
                        }

                        videoVf = string.IsNullOrEmpty(moodFilter)
                            ? $"{scaleFilter},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                            : $"{scaleFilter},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}";

                        if (!fastRender)
                        {
                            try
                            {
                                if (isStaticClip && item.Duration >= 2.0)
                                {
                                    // Static clip (photo) — Ken Burns with 6 motion types
                                    int staticFrames = Math.Max(1, (int)(effectiveDuration * 30.0));
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
                                        ? $"{staticKB},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                                        : $"{staticKB},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}";
                                }

                                if (item.Duration >= 2.0 && !isStaticClip)
                                {
                                    int overW = (int)(targetWidth * overscanFactor);
                                    int overH = (int)(targetHeight * overscanFactor);
                                    if (overW % 2 != 0) overW++;
                                    if (overH % 2 != 0) overH++;

                                    int frames = Math.Max(1, (int)(effectiveDuration * 30.0));
                                    int maxX = overW - targetWidth;
                                    int maxY = overH - targetHeight;

                                    // ── Ken Burns: 6 tipova pokreta, rotiramo po index-u ──────
                                    // Each clip gets a different direction — not repetitive
                                    // n = frame broj (0..frames), raste linearno
                                    int kbType = fileIdx % 6;
                                    string xExpr, yExpr;
                                    switch (kbType)
                                    {
                                        case 0: // zoom-in from center (classic KB)
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
                                        ? $"{kenBurnsGpu},{baseNormalize}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}"
                                        : $"{kenBurnsGpu},{baseNormalize},{moodFilter}{colorMatchFilter}{wwbFilter}{seasonalGradeFilter}{childrenContrastFilter}{gradeFilter}{fpsNormalize}{DENOISE_FILTER}";
                                }
                            }
                            catch (Exception exKenBurns) { LogSwallowedError("KenBurns filter build", exKenBurns); }
                        }

                        // v1.4: the automatic "children's music video" look (warm grade, seasonal
                        // colours, denoise, Ken Burns movement) is for clips the AI creator made.
                        // Your own footage stays exactly as it was shot — before, every imported video
                        // was tinted, zoomed and panned without asking, which no editor should do.
                        if (!IsAiCreatorClip(item))
                            videoVf = scaleFilter;

                        // v1.4: the Effects tab (brightness, contrast, blur) and the clip speed now
                        // reach the render. Before, they were stored on the clip and never used.
                        string userFx = VideoEffects.GetFFmpegFilter(item.VideoEffect, includeSpeed: false);
                        if (!string.IsNullOrEmpty(userFx)) videoVf += "," + userFx;
                        double clipSpeed = Math.Clamp(item.VideoEffect?.Speed ?? 1.0, 0.25, 4.0);
                        string speedVf = Math.Abs(clipSpeed - 1.0) > 0.001
                            ? $"setpts=PTS/{clipSpeed.ToString("0.###", CultureInfo.InvariantCulture)},"
                            : "";
                        // at 2x speed, twice as much of the source fills the same slot
                        string inputDurStr = (effectiveDuration * clipSpeed).ToString("0.###", CultureInfo.InvariantCulture);
                        if (SpeedRamp.Has(item))
                        {
                            // v2.0: a speed ramp — the exact inverse of the speed curve for every frame
                            var sp = item.VideoEffect.SpeedPoints;
                            clipSpeed = SpeedRamp.Average(item);
                            speedVf = $"setpts='({SpeedRamp.InverseExpr(sp, effectiveDuration, 0, 0)})/TB',";
                            inputDurStr = (SpeedRamp.SourceAt(sp, effectiveDuration, effectiveDuration) + 0.1).ToString("0.###", CultureInfo.InvariantCulture);
                        }

                        // v2.0: smooth slow motion — new frames are calculated between the real ones
                        if (clipSpeed < 0.999 && item.VideoEffect?.SmoothSlowMotion == true && !PreviewMode)
                            speedVf += $"minterpolate=fps={TARGET_FPS}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1,";

                        // v2.0: stabilisation — pass 1 measures the shake, pass 2 (in the clip's
                        // filter below) steadies the picture. If pass 1 fails the clip is used as it is.
                        if (item.VideoEffect?.Stabilize == true)
                        {
                            string trf = Path.ChangeExtension(tempVideo, ".trf");
                            int shaky = (int)Math.Clamp(Math.Round(item.VideoEffect.StabilizeStrength), 1, 10);
                            LogToMainWindow(LF("re_stabilizing", item.Name ?? Path.GetFileName(item.Path)));
                            string detectArgs = $"-nostdin {sourceSeekArg}-t {inputDurStr} -i \"{item.Path}\" " +
                                                $"-vf \"vidstabdetect=shakiness={shaky}:accuracy=15:result='{EscapeFilterPath(trf)}'\" -an -f null -";
                            if (await RunFFmpegAsync(detectArgs, cancellationToken) && File.Exists(trf))
                                speedVf = $"vidstabtransform=input='{EscapeFilterPath(trf)}':smoothing={8 + shaky * 3}:zoom=0:optzoom=1:interpol=bicubic," + speedVf;
                            else
                                LogToMainWindow(LF("re_stabilize_failed", item.Name ?? Path.GetFileName(item.Path)));
                        }

                        bool isZoompan = videoVf.Contains("zoompan");
                        string argsVid;

                        if (isZoompan)
                        {
                            // ISKRA PATCH Pravilo 2: fps normalizacija i za zoompan putanju
                            string normVfZp = $"scale={targetWidth}:{targetHeight}:flags=lanczos,fps=fps={TARGET_FPS}:round=near,format=yuv420p";
                            argsVid = $"-nostdin {sourceSeekArg}-t {inputDurStr} -i \"{item.Path}\" -vf \"{speedVf}{normVfZp},{videoVf}\" -t {durationStr} {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }
                        else
                        {
                            // ISKRA PATCH — Pravilo 2: fps=round=near garantuje uniformni FPS
                            // bez preskakanja frejmova (za razliku od starih metoda).
                            // stream_loop + fade=in sakriva loop-point trganje.
                            // round=near: rounds PTS instead of skipping frames —
                            // eliminates green/black inter-frames at clip junctions.
                            string loopFadeIn = $",fade=t=in:st=0:d=0.3";
                            // OUTRO-FIX: Dodaj fade=t=out za zadnje kadrove outro sekcije
                            // Tag fadeout=1 se upisuje u Description od AIVideoCreator za zadnja 3 kadra.
                            bool hasFadeOutTag = (item.AudioDescription ?? "").Contains("fadeout=1") ||
                                                 (item.Name ?? "").Contains("fadeout=1");
                            string loopFadeOut = hasFadeOutTag
                                ? $",fade=t=out:st={Math.Max(0.3, effectiveDuration - 1.5):F2}:d=1.5"
                                : "";
                            // Pravilo 2: scale+fps normalizacija u jednom filtru
                            string normVf = $"scale={targetWidth}:{targetHeight}:flags=lanczos,fps=fps={TARGET_FPS}:round=near,format=yuv420p";
                            argsVid = $"-nostdin -stream_loop -1 {sourceSeekArg}-t {inputDurStr} -i \"{item.Path}\" -vf \"{speedVf}{normVf},{videoVf}{loopFadeIn}{loopFadeOut}\" -t {durationStr} {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
                        }

                        // v2.0: an .mp4/.mov that holds only sound (no picture) used to stop the
                        // whole render ("Output file does not contain any stream"). Its slot is now
                        // a black picture; its sound is used as usual.
                        if (!await HasPictureAsync(item.Path, cancellationToken))
                        {
                            LogToMainWindow(LF("re_no_picture", item.Name ?? Path.GetFileName(item.Path)));
                            argsVid = $"-nostdin -f lavfi -i color=c=black:s={targetWidth}x{targetHeight}:r={TARGET_FPS}:d={durationStr} " +
                                      $"-t {durationStr} {vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{tempVideo}\"";
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
                        // FIX-FADE: increased to 0.50s for standard (Gemini: min 0.5s for fluidity)
                        // fast ostaje kratak da ne ubije energiju refrena
                        // v2.0: the same rules as live playback (TransitionAfter)
                        int prevIdx = videoFiles.Count - 2;   // the clip rendered before this one
                        var prevItem = prevIdx >= 0
                            ? sortedItems.Where(si => itemToFile.ContainsKey(si)).Skip(prevIdx).Take(1).FirstOrDefault()
                            : null;
                        var (xfadeType, clipFade) = TransitionAfter(item, prevItem, effectiveDuration);
                        fadeDurations.Add(clipFade);
                        transitionTypes.Add(xfadeType);
                    }

                    current++;
                    progress?.Report(Math.Min(85, current * 85 / Math.Max(1, total)));
                }

                if (videoFiles.Count == 0)
                    throw new Exception(L("re_no_media"));

                // ── ISKRA PATCH — Rule 1: Mathematical duration synchronization ─────────────
                // Measures total video duration and extends the last clip by looping if there is a deficit.
                // Nikad crna pozadina — loop zadnjeg klipa je vizuelno nevidljiv.
                //
                // BUG-3 FIX: Crossfade oduzima (N-1)*avgFade sekundi od ukupnog trajanja.
                // IskraSync must account for this — otherwise it extends too little and video stays shorter than audio.
                // Formula: effectiveVideoDur = sum(clipDurs) - crossfadeOverlap
                // gdje crossfadeOverlap = (N-1) * avgFade (N = broj video klipova)
                // ══════════════════════════════════════════════════════════════════════════
                // ISKRASYNC v3 — Kompletan rewrite svih poznatih bugova
                //
                // Fix 1: timelineSpan kao primarna mjera (ne videoSum)
                //         videoSum does not account for gaps between lyric lines (lyric-sync layout).
                //         timelineSpan = max(End) - cursorOffset = stvarni vremenski raspon videa.
                //
                // Fix 2: Beat-Lock offset se oduzima od audioDurForSync
                //         -ss AudioStartSeconds trims the effective audio stream by that value.
                //         Video treba trajati rawAudioDur - ssOffset, ne rawAudioDur.
                //
                // Fix 3: Cap na ponavljanje u SmartOutroPool
                //         If deficit > poolTotal, the pool would loop infinitely.
                //         Capped at max 1 loop (each clip from pool used max 1x).
                //         If that still does not cover the deficit, SmartOutroPool ends — -shortest trims the excess.
                //
                // Fix 4: outroSegPath naming uses a dedicated counter, not videoFiles.Count
                //         videoFiles.Count changes inside the loop — potential naming conflicts.
                //
                // Fix 5: Buffer smanjen sa +1.0s na +0.5s (preciznije poklapanje s crossfade-om)
                //
                // Fix 6: Log warning when timelineSpan > audioDur (outro segments will be trimmed)
                //
                // Fix 7: Outro crossfade overlap se dodaje u efectiveVideoDur za preciznost
                // ══════════════════════════════════════════════════════════════════════════
                // v1.4: the sound of your own video clips. Before, every video was rendered with
                // "-an" and only separate audio clips were heard — a recording of the children
                // talking or singing came out silent. AI creator clips (stock footage) stay silent,
                // as do muted clips (volume 0) and clips with a changed speed.
                var ownSoundClips = new List<TimelineItem>();
                foreach (var v in sortedItems.Where(i => (i.Type == "Video" || i.IsVideo) && !i.IsImage &&
                                                         !IsAiCreatorClip(i) && i.Volume > 0 &&
                                                         Math.Abs((i.VideoEffect?.Speed ?? 1.0) - 1.0) < 0.001 && !SpeedRamp.Has(i) &&
                                                         !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path)))
                {
                    if (await ExportHelpers.HasAudioAsync(v.Path, cancellationToken))
                        ownSoundClips.Add(v);
                }
                if (ownSoundClips.Count > 0)
                    LogToMainWindow(LanguageManager.F("lx_keeping_the_original_sound_of_049a8", ownSoundClips.Count));

                // No music track, but the clips have their own sound: a silent base track the
                // length of the timeline carries them.
                if ((audio == null || !File.Exists(audio.Path)) && ownSoundClips.Count > 0)
                {
                    double span = sortedItems.Where(i => i.IsVideo || i.IsImage || i.Type == "Video" || i.Type == "Image")
                                             .Select(i => i.Start + i.Duration).DefaultIfEmpty(0).Max();
                    string silent = Path.Combine(tempDir, "silent_base.wav");
                    string silentArgs = $"-nostdin -f lavfi -i anullsrc=r=48000:cl=stereo -t {span.ToString("0.###", CultureInfo.InvariantCulture)} -c:a pcm_f32le -y \"{silent}\"";
                    if (span > 0.5 && await RunFFmpegAsync(silentArgs, cancellationToken) && File.Exists(silent))
                        audio = new TimelineItem { Path = silent, Name = "Silent base", Type = "Audio", Start = 0, Duration = span, Volume = 100 };
                }

                double audioDurForSync = 0; // Outer scope — dostupno i u mux bloku ispod
                if (audio != null && File.Exists(audio.Path))
                {
                    double rawAudioDur = await GetVideoDuration(audio.Path, cancellationToken);

                    // v1.4: the music starts where its clip sits on the timeline (audio.Start),
                    // e.g. after the logo and title card of the AI music video. The old
                    // "-ss AudioStartSeconds" shift is gone — it assumed a timeline the
                    // creator never built (and was in practice never applied, because the
                    // beat info never reached the render).
                    double audioSsOffset = 0.0;
                    double audioTimelineStart = Math.Max(0, audio.Start);
                    audioDurForSync = Math.Max(0, rawAudioDur - Math.Max(0, audio.SourceOffset) + audioTimelineStart);

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

                        // Crossfade overlap — koristi se i za effectiveVideoDur i za dijagnostiku
                        double avgFadeForSync = fadeDurations.Count > 0
                            ? fadeDurations.Average() : 0.35;
                        int nClips = videoFiles.Count;
                        // v1.4: crossfades no longer shorten the video (each outgoing clip is
                        // extended by its fade in ApplyCrossfade*), so nothing is subtracted.
                        double crossfadeOverlap = 0.0;

                        // FIX 1: effectiveVideoDur = timelineSpan (ne videoSum).
                        // timelineSpan = max(End) - cursorOffset reflects the actual time span
                        // including gaps between lyric lines in the lyric-sync layout.
                        // cursorOffset = min(Start) svih timeline stavki = Naslov + Logo trajanje.
                        // Ako itemToFile nije dostupan, fallback na videoSum - crossfadeOverlap.
                        double effectiveVideoDur;
                        double timelineSpan = 0;
                        if (itemToFile.Count > 0)
                        {
                            double timelineEnd    = itemToFile.Keys.Max(it => it.End);
                            double timelineStart  = itemToFile.Keys.Min(it => it.Start);
                            timelineSpan          = timelineEnd - timelineStart;
                            // Use timelineSpan as the primary measure if it is greater than videoSum-crossfade.
                            // If videoSum-crossfade is greater (sequential video without gaps), use that.
                            effectiveVideoDur = Math.Max(timelineSpan, Math.Max(totalVidDur - crossfadeOverlap, 0));
                        }
                        else
                        {
                            effectiveVideoDur = Math.Max(totalVidDur - crossfadeOverlap, 0);
                        }

                        double deficit = audioDurForSync - effectiveVideoDur;
                        LogToMainWindow(LanguageManager.F("lx_length_check_sound_f_s_81c44", audioDurForSync, audioSsOffset, totalVidDur, timelineSpan, crossfadeOverlap, effectiveVideoDur, deficit));

                        // FIX 6: Upozorenje kad timeline prelazi audio trajanje
                        if (timelineSpan > audioDurForSync + 2.0)
                            LogToMainWindow(LanguageManager.F("lx_length_check_the_timeline_f_6b405", timelineSpan, audioDurForSync, timelineSpan - audioDurForSync));

                        if (deficit > 0.15 && videoFiles.Count > 0)
                        {
                            // ── ISKRASYNC v3 — SmartOutroPool ────────────────────────────────────────
                            // Puni deficit klipovima iz druge polovine videa (vizuelno raznovrsno).
                            // FIX 3: Each clip from pool used max 1x (no looping).
                            // FIX 4: outroSegIdx is a dedicated counter, independent of videoFiles.Count.
                            // ─────────────────────────────────────────────────────────────────────────    

                            // Pool: clips from the second half of videoFiles (not intro/start)
                            int halfPoint = videoFiles.Count / 2;
                            int poolSize  = Math.Min(12, videoFiles.Count - halfPoint);
                            if (poolSize < 3) poolSize = Math.Min(12, videoFiles.Count);
                            int poolStart = Math.Max(halfPoint, videoFiles.Count - poolSize);
                            var outroPool     = videoFiles   .Skip(poolStart).Take(poolSize).ToList();
                            var outroPoolDurs = clipDurations.Skip(poolStart).Take(poolSize).ToList();

                            // FIX 3: Ukupni kapacitet poola bez ponavljanja
                            double poolTotalCapacity = outroPoolDurs.Sum();
                            LogToMainWindow(LanguageManager.F("lx_length_check_clips_to_fill_bdf49", outroPool.Count, poolTotalCapacity, deficit));

                            // FIX 5: Buffer +0.5s (umjesto +1.0s) — preciznije poklapanje s xfade-om
                            double remaining    = deficit + 0.5;
                            int    outroSegIdx  = 0;  // FIX 4: dedicated counter for naming
                            int    poolIdx      = 0;
                            int    maxIter      = outroPool.Count + 2; // FIX 3: max 1 prolaz kroz pool
                            bool   anySegOk     = false;

                            while (remaining > 0.1 && poolIdx < outroPool.Count && maxIter-- > 0)
                            {
                                string srcClip = outroPool[poolIdx];
                                double srcDur  = outroPoolDurs[poolIdx];
                                poolIdx++;
                                outroSegIdx++;

                                // Segment traje min(srcDur, remaining) — ne loopujemo dulje od originala
                                double segDur    = Math.Min(remaining, Math.Max(srcDur, 2.5));
                                string segDurStr = segDur.ToString("F3", CultureInfo.InvariantCulture);

                                // FIX 4: Naziv koristi outroSegIdx, ne videoFiles.Count
                                string outroSegPath = Path.Combine(tempDir, $"outro_seg_{outroSegIdx:D3}.mp4");

                                bool isLastSeg    = (remaining - segDur) <= 0.15 || poolIdx >= outroPool.Count;
                                string fadeOutFilter = isLastSeg
                                    ? $",fade=t=out:st={Math.Max(0.3, segDur - 2.0):F2}:d=2.0"
                                    : "";

                                string outroVf = $"fps=fps={TARGET_FPS}:round=near,format=yuv420p,fade=t=in:st=0:d=0.4{fadeOutFilter}";
                                string outroSegArgs = $"-nostdin -stream_loop -1 -t {segDurStr} -i \"{srcClip}\" " +
                                                     $"-vf \"{outroVf}\" " +
                                                     $"{vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{outroSegPath}\"";
                                bool segOk = await RunFFmpegAsync(outroSegArgs, cancellationToken);

                                if (segOk && File.Exists(outroSegPath) && new FileInfo(outroSegPath).Length > 100)
                                {
                                    videoFiles.Add(outroSegPath);
                                    fadeDurations.Add(0.50);
                                    transitionTypes.Add("fade"); // FIX-DISSOLVE: outro segmenti koriste fade
                                    double actualSeg = await GetVideoDuration(outroSegPath, cancellationToken);
                                    remaining -= actualSeg;
                                    anySegOk = true;
                                    LogToMainWindow(LanguageManager.F("lx_length_check_ending_part_f_6d722", outroSegIdx, actualSeg, poolIdx - 1, remaining));
                                }
                                else
                                {
                                    // Fallback: kopija bez filtera
                                    string fbPath = Path.Combine(tempDir, $"outro_fb_{outroSegIdx:D3}.mp4");
                                    string fbArgs = $"-nostdin -i \"{srcClip}\" -t {segDurStr} " +
                                                   $"-vf \"fps=fps={TARGET_FPS}:round=near,format=yuv420p\" " +
                                                   $"{vEncArgs} {VSYNC_CFR} {pixFmt} -an -y \"{fbPath}\"";
                                    bool fbOk = await RunFFmpegAsync(fbArgs, cancellationToken);
                                    if (fbOk && File.Exists(fbPath) && new FileInfo(fbPath).Length > 100)
                                    {
                                        videoFiles.Add(fbPath);
                                        fadeDurations.Add(0.50);
                                        transitionTypes.Add("fade"); // FIX-DISSOLVE: outro fallback koristi fade
                                        remaining -= segDur;
                                        anySegOk = true;
                                    }
                                }
                            }

                            if (anySegOk)
                                LogToMainWindow(LanguageManager.F("lx_length_check_the_end_is_c8acb", videoFiles.Count, remaining));
                            else
                                LogToMainWindow(LanguageManager.T("lx_length_check_filling_the_end_a10db"));

                            if (remaining > 1.0)
                                LogToMainWindow(LanguageManager.F("lx_length_check_all_clips_used_06fcd", outroPool.Count, remaining));
                        }
                        else if (deficit <= 0)
                        {
                            LogToMainWindow(LanguageManager.F("lx_length_check_the_length_is_8fcc1", -deficit));
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
                const double CROSSFADE_DURATION = 0.45; // FIX-FADE: increased 0.35→0.45 for smoother blend
                double avgFade = fadeDurations.Count > 0
                    ? Math.Round(fadeDurations.Average(), 3) : CROSSFADE_DURATION;
                string crossfadedVideo = null;
                if (videoFiles.Count >= 2)
                {
                    crossfadedVideo = await ApplyCrossfadeSinglePass(
                        videoFiles, tempDir, fadeDurations, CROSSFADE_DURATION, cancellationToken, transitionTypes);
                    if (crossfadedVideo != null)
                        LogToMainWindow(LanguageManager.F("lx_crossfades_clips_s_on_average_3f7f7", videoFiles.Count, avgFade));
                    else
                    {
                        LogToMainWindow(LanguageManager.T("lx_the_crossfades_failed_they_are_3ab89"));
                        if (videoFiles.Count <= 24)
                            crossfadedVideo = await ApplyCrossfadePairwise(
                                videoFiles, tempDir, fadeDurations, CROSSFADE_DURATION, cancellationToken);
                    }
                }

                string finalOutput = outputPath;
                string argsFinal;
                // v1.4: the finished (lossless) sound track. The post pass takes the sound from here,
                // so the song is encoded to AAC only once for the file that is kept.
                string masterAudio = null;

                if (audio != null && File.Exists(audio.Path))
                {
                    string tempAudioPath = Path.Combine(tempDir, "audio" + Path.GetExtension(audio.Path));
                    File.Copy(audio.Path, tempAudioPath, true);

                    // v1.4: place the main audio at its timeline position (and its in-point),
                    // so picture, subtitles and music share one clock.
                    double mainAudioDelay = Math.Max(0, audio.Start);
                    double mainAudioIn = Math.Max(0, audio.SourceOffset);
                    // v1.4: the volume set on the music clip (Ctrl+Alt+V) is honoured too; it was ignored
                    double mainVol = Math.Clamp(audio.Volume / 100.0, 0.0, 4.0);
                    // v1.4 (R5): a volume curve (louder here, quieter there; "lower the music under
                    // speech") and the clip's audio effects (bass, treble, reverb, compressor)
                    bool mainCurve = AudioAutomation.HasCurve(audio);
                    bool mainVolChanged = Math.Abs(mainVol - 1.0) > 0.005 || mainCurve;
                    string mainFx = AudioEffects.GetFFmpegFilter(audio.AudioFx, audio.Duration);
                    string mainTrack = TrackMixer.Filter(audio.TrackIndex);   // v2.0: the track mixer
                    if (mainAudioDelay > 0.01 || mainAudioIn > 0.01 || mainVolChanged || mainFx.Length > 0 || mainTrack.Length > 0)
                    {
                        string placedAudio = Path.Combine(tempDir, "audio_placed.wav");
                        long delayMsMain = (long)Math.Round(mainAudioDelay * 1000);
                        string inArg = mainAudioIn > 0.01 ? $"-ss {mainAudioIn.ToString("F3", CultureInfo.InvariantCulture)} " : "";
                        // v2.0: the effects (fades included) work on the clip itself, before it is
                        // moved to its place on the timeline
                        string mainChain = (mainFx.Length > 0 ? mainFx + "," : "") +
                                           $"adelay={delayMsMain}:all=1" +
                                           (mainVolChanged ? "," + AudioAutomation.VolumeFilter(audio, mainAudioDelay) : "") +
                                           (mainTrack.Length > 0 ? "," + mainTrack : "");
                        string placeArgs = $"-nostdin {inArg}-i \"{tempAudioPath}\" -af \"{mainChain}\" -c:a pcm_f32le -y \"{placedAudio}\"";
                        if (await RunFFmpegAsync(placeArgs, cancellationToken) && File.Exists(placedAudio) && new FileInfo(placedAudio).Length > 1000)
                        {
                            tempAudioPath = placedAudio;
                            LogToMainWindow(LanguageManager.F("lx_music_placed_at_f_s_49ff8", mainAudioDelay, (mainAudioIn > 0.01 ? LanguageManager.F("lx_music_from_in_file", mainAudioIn) : "")));
                        }
                    }

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
                    // v1.4: plus the original sound of the user's own video clips (see above)
                    secondaryAudioClips.AddRange(ownSoundClips.Where(v => !secondaryAudioClips.Contains(v)));

                    if (secondaryAudioClips.Count > 0)
                    {
                        string mixedWithTransitions = Path.Combine(tempDir, "audio_with_transitions.wav");
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
                    // If BeatDetection detects silence at the beginning of the audio file,
                    // audio is shifted back by AudioStartSeconds using the -ss parameter.
                    // This eliminates "dead time" — green/static intro frames that
                    // occur because audio lags behind video (silence at the start of MP3/WAV).
                    //
                    // PRINCIP: audio -ss (input seeking) je lossless i milisekundno precizan.
                    // Video stays unchanged — audio is simply "shifted" to start at beat 0.
                    //
                    // CONDITION: AudioStartSeconds > 50ms (below that it is noise, not silence)
                    // ─────────────────────────────────────────────────────────────────────
                    // v1.4: no -ss on the audio any more — it is already placed on the timeline above.
                    string audioSsArg = "";
                    masterAudio = tempAudioPath;

                    // DURATION FIX: Koristimo -shortest ali SAMO ako je video dovoljno dug.
                    // Ako video nije popunjen (SmartOutroPool nije pokrio deficit), dodajemo
                    // explicit -t on audio stream to prevent premature cutting.
                    // Primary solution remains SmartOutroPool — -t is a backup.
                    string shortestArg = "-shortest ";

                    if (crossfadedVideo != null && File.Exists(crossfadedVideo))
                    {
                        double crossfadedDur = await GetVideoDuration(crossfadedVideo, cancellationToken);
                        if (crossfadedDur < audioDurForSync - 2.0)
                        {
                            // v1.4: the song is never cut any more. Before, a video that came out
                            // shorter (some clips failed) cut the AUDIO to the video's length —
                            // a 3:30 song became a 36-second video without any warning. Now the
                            // last picture is held until the music ends, and the user is told.
                            double missing = audioDurForSync - crossfadedDur;
                            string padded = Path.Combine(Path.GetDirectoryName(crossfadedVideo) ?? Path.GetTempPath(), "video_padded.mp4");
                            string padArgs = $"-nostdin -i \"{crossfadedVideo}\" -vf \"tpad=stop_mode=clone:stop_duration={missing.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}\" " +
                                             $"{vEncArgs_cached ?? "-c:v libx264 -preset veryfast -crf 23"} -pix_fmt yuv420p -an -y \"{padded}\"";
                            if (await RunFFmpegAsync(padArgs, cancellationToken) && File.Exists(padded))
                            {
                                crossfadedVideo = padded;
                                LogToMainWindow(LanguageManager.F("lx_length_the_video_f_s_9c446", crossfadedDur, audioDurForSync, missing));
                            }
                            else
                            {
                                string tVal = crossfadedDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                                shortestArg = $"-t {tVal} ";
                                LogToMainWindow(LanguageManager.F("lx_length_the_video_f_s_255d0", crossfadedDur, audioDurForSync, tVal));
                            }
                            AnnounceShortVideo(crossfadedDur, audioDurForSync);
                        }
                        argsFinal = $"-nostdin -i \"{crossfadedVideo}\" {audioSsArg}-i \"{tempAudioPath}\" -c:v copy -c:a aac -b:a 256k -map 0:v -map 1:a {shortestArg}-y \"{finalOutput}\"";
                    }
                    else
                    {
                        argsFinal = $"-nostdin -f concat -safe 0 -i \"{concatFile}\" {audioSsArg}-i \"{tempAudioPath}\" -c:v copy -c:a aac -b:a 256k -map 0:v -map 1:a {shortestArg}-y \"{finalOutput}\"";
                    }
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
                            // Fade-out: PATCH 11 — Final Polish, exactly 2.5s
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
                            // Each subtitle is active EXCLUSIVELY between its Whisper start and end.
                            // enable='between(t,START,END)' — clean screen during instrumental breaks.
                            // enableSubtitles=false: global switch — no drawtext filters.
                            // v1.4 KARAOKE: subtitles that carry word timings (from the AI video
                            // creator) are drawn with an ASS karaoke file — each word fills with
                            // colour exactly while it is sung. Everything else keeps the drawtext path.
                            // If the karaoke pass fails (e.g. an FFmpeg build without libass), the
                            // post-pass is retried with plain drawtext for ALL subtitles, so the user
                            // never loses the fade/subtitle pass because of karaoke.
                            string karaokeAssPath = null;
                            if (enableSubtitles && subtitles != null && subtitles.Any(s => s.Words != null && s.Words.Count > 0))
                                karaokeAssPath = WriteKaraokeAss(subtitles, tempDir, targetWidth, targetHeight);

                            string BuildDrawtext(bool skipKaraokeLines)
                            {
                            string drawtextVf = "";
                            if (enableSubtitles && subtitles != null && subtitles.Count > 0)
                            {
                                var dtParts2 = new System.Collections.Generic.List<string>();
                                foreach (var sub in subtitles.OrderBy(s => s.Start))
                                {
                                    if (string.IsNullOrWhiteSpace(sub.Text)) continue;
                                    if (skipKaraokeLines && sub.Words != null && sub.Words.Count > 0) continue;
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
                                            .Replace("'", "\u2019") // v1.4: a plain ' broke the filter
                                            .Replace(":", "\\:")
                                            .Replace("%", "\\\\%");
                                        double ly2 = dtBaseY + sli * dtLineH;
                                        string sStr = subS.ToString("F3", CultureInfo.InvariantCulture);
                                        string eStr = subE.ToString("F3", CultureInfo.InvariantCulture);
                                        // RULE 4 — KEY LINE:
                                        // between(t,START,END) = titl vidljiv SAMO u Whisper prozoru
                                        // As soon as Whisper says "end" — text disappears. Clean screen during breaks.
                                        dtParts2.Add(
                                            $"drawtext={DtFont}enable='between(t,{sStr},{eStr})'" +
                                            $":text='{esc}':fontsize={dtFontSize}:fontcolor=white@0.95" +
                                            $":x=(w-text_w)/2:y={ly2:F0}" +
                                            $":borderw=4:bordercolor=black@0.85" +
                                            $":shadowx=2:shadowy=2:shadowcolor=black@0.70");
                                    }
                                }
                                if (dtParts2.Count > 0)
                                    drawtextVf = "," + string.Join(",", dtParts2);
                            }
                            return drawtextVf;
                            }

                            // v1.4: the sound comes straight from the lossless master (when there is one),
                            // cut to the picture's length, with a soft peak limiter instead of hard clipping.
                            bool useMaster = masterAudio != null && File.Exists(masterAudio);
                            string finalDurStr = finalDur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                            string postInputs = useMaster
                                ? "-nostdin -i \"" + finalOutput + "\" -i \"" + masterAudio + "\" -map 0:v -map 1:a -t " + finalDurStr + " "
                                : "-nostdin -i \"" + finalOutput + "\" ";
                            // v2.0: the fade at the start and end and the warm colour are options now
                            // (Export options). They were forced on every video — right for the AI
                            // children's songs, wrong for someone's own footage.
                            bool doFades = exportSettings?.FadeInOut ?? true;
                            bool doWarm = exportSettings?.WarmLook ?? sortedItems.Any(IsAiCreatorClip);
                            string BuildPostArgs(string subtitleFilters)
                            {
                                var vf = new List<string>();
                                if (doFades) { vf.Add(fadeInFilter); vf.Add("fade=t=out:st=" + fadeStartStr + ":d=" + fadeOutDurStr); }
                                if (doWarm) vf.Add("colorchannelmixer=rr=1:rb=-0.02:gr=0:gg=1:gb=-0.02:br=-0.03:bg=-0.03:bb=1.06");
                                string vfStr = string.Join(",", vf) + (vf.Count == 0 ? (subtitleFilters ?? "").TrimStart(',') : subtitleFilters);
                                string videoPart = vfStr.Length > 0
                                    ? "-vf \"" + vfStr + "\" " + encArgsPost + " -pix_fmt yuv420p "
                                    : "-c:v copy ";
                                string af = (doFades
                                    ? "afade=t=in:st=0:d=0.8,afade=t=out:st=" + fadeStartStr + ":d=" + fadeOutDurStr + ","
                                    : "") + "alimiter=limit=0.98:attack=5:release=60:level=disabled";
                                return postInputs + videoPart + "-af \"" + af + "\" " +
                                       $"-c:a aac -b:a 256k -ar 48000 -y \"{postOutput}\"";
                            }

                            bool postOk = false;
                            if (karaokeAssPath != null)
                            {
                                string assFilter = ",ass='" + EscapeFilterPath(karaokeAssPath) + "'" + AssFontsDir;
                                postOk = await RunFFmpegAsync(BuildPostArgs(BuildDrawtext(skipKaraokeLines: true) + assFilter), cancellationToken)
                                         && File.Exists(postOutput) && new FileInfo(postOutput).Length > 100_000;
                                if (postOk)
                                    LogToMainWindow(LanguageManager.T("lx_karaoke_subtitles_drawn_every_word_2a6bf"));
                                else
                                {
                                    LogToMainWindow(LanguageManager.T("lx_karaoke_subtitles_failed_plain_subtitles_9fc50"));
                                    try { if (File.Exists(postOutput)) File.Delete(postOutput); } catch { }
                                }
                            }
                            if (!postOk)
                                postOk = await RunFFmpegAsync(BuildPostArgs(BuildDrawtext(skipKaraokeLines: false)), cancellationToken);
                            if (postOk && File.Exists(postOutput) && new FileInfo(postOutput).Length > 100_000)
                            {
                                File.Delete(finalOutput);
                                File.Move(postOutput, finalOutput);
                            }
                        }
                    }
                    catch (Exception exPost) { LogSwallowedError("Post-processing file swap", exPost); }

                    // v2.0: layers (Video 2 …), keyframe animation and colour LUTs, drawn over the
                    // finished picture in one last pass
                    try
                    {
                        var layerClips = layerItems.Select(ToComposite).ToList();
                        var mainComposite = sortedItems.Where(i => !(i.Type == "Audio" || i.IsAudio)).Select(ToComposite).ToList();
                        if (LayerCompositor.IsNeeded(layerClips, mainComposite))
                        {
                            progress?.Report(97);
                            var compositor = new LayerCompositor(AppPaths.FFprobe, RunFFmpegAsync, LogToMainWindow);
                            LayerCompositor.FontSpec = DtFont;
                            if (await compositor.ApplyAsync(finalOutput, layerClips, mainComposite, _xfW, _xfH, TARGET_FPS,
                                                            vEncArgs_cached ?? vEncArgs, cancellationToken))
                                LogToMainWindow(LanguageManager.F("lx_layers_drawn_layer_clip_s_12517", layerClips.Count));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exLayers) { LogSwallowedError("Drawing the layers", exLayers); }
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
                catch (Exception exCleanup) { LogSwallowedError("Temp dir cleanup", exCleanup); }
            }
        }

        // ── v1.4 Karaoke subtitles (ASS) ──────────────────────────────────────
        /// <summary>
        /// Writes an ASS file for all subtitles that carry word timings. Each word uses
        /// {\kf} so it fills from white to gold while it is sung. Same font size and bottom
        /// margin as the drawtext subtitles, so karaoke and plain lines look alike.
        /// Returns null when there is nothing to write or writing fails.
        /// </summary>
        private string WriteKaraokeAss(List<SubtitleItem> subtitles, string tempDir, int width, int height)
        {
            try
            {
                var lines = subtitles
                    .Where(s => s.Words != null && s.Words.Count > 0 && !string.IsNullOrWhiteSpace(s.Text))
                    .OrderBy(s => s.Start)
                    .ToList();
                if (lines.Count == 0) return null;

                int fontSize = height >= 1080 ? 56 : 44;
                int marginV = (int)(height * 0.08);

                string AssTime(double t)
                {
                    if (t < 0) t = 0;
                    int cs = (int)Math.Round(t * 100);
                    int h = cs / 360000; cs %= 360000;
                    int m = cs / 6000; cs %= 6000;
                    int sec = cs / 100; cs %= 100;
                    return $"{h}:{m:00}:{sec:00}.{cs:00}";
                }
                string Clean(string text) => (text ?? "").Replace("{", "(").Replace("}", ")").Replace("\\", "/").Trim();

                var sb = new StringBuilder();
                sb.AppendLine("[Script Info]");
                sb.AppendLine("ScriptType: v4.00+");
                sb.AppendLine($"PlayResX: {width}");
                sb.AppendLine($"PlayResY: {height}");
                sb.AppendLine("WrapStyle: 0");
                sb.AppendLine("ScaledBorderAndShadow: yes");
                sb.AppendLine();
                sb.AppendLine("[V4+ Styles]");
                sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
                // Primary = sung (gold), Secondary = not yet sung (white)
                sb.AppendLine($"Style: Karaoke,Arial,{fontSize},&H0000D7FF,&H00FFFFFF,&H00000000,&H80000000,-1,0,0,0,100,100,0,0,1,4,2,2,60,60,{marginV},1");
                sb.AppendLine();
                sb.AppendLine("[Events]");
                sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

                foreach (var sub in lines)
                {
                    double subS = Math.Max(0, sub.Start);
                    double subE = sub.End > subS + 0.1 ? sub.End : subS + 1.5;
                    var words = sub.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Offset).ToList();
                    if (words.Count == 0) continue;

                    var text = new StringBuilder();
                    int cursorCs = 0;
                    for (int i = 0; i < words.Count; i++)
                    {
                        int startCs = Math.Max(0, (int)Math.Round(words[i].Offset * 100));
                        if (startCs > cursorCs)
                        {
                            text.Append($"{{\\k{startCs - cursorCs}}}");   // wait before this word (nothing highlighted)
                            cursorCs = startCs;
                        }
                        // The word stays "being sung" until the next word starts (last word: its own length)
                        int durCs = i + 1 < words.Count
                            ? Math.Max(5, (int)Math.Round(words[i + 1].Offset * 100) - cursorCs)
                            : Math.Max(20, (int)Math.Round(words[i].Duration * 100));
                        text.Append($"{{\\kf{durCs}}}{Clean(words[i].Text)}");
                        if (i + 1 < words.Count) text.Append(' ');
                        cursorCs += durCs;
                    }

                    sb.AppendLine($"Dialogue: 0,{AssTime(subS)},{AssTime(subE)},Karaoke,,0,0,0,,{text}");
                }

                string path = Path.Combine(tempDir, "karaoke.ass");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                LogSwallowedError("Karaoke ASS write", ex);
                return null;
            }
        }

        /// <summary>Path for use inside an FFmpeg filter option: C:\a\b.ass → C\:/a/b.ass</summary>
        private static string EscapeFilterPath(string path)
            => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

        /// <summary>
        /// v1.4 — true for clips the AI music video creator made (they carry its mood/context tags
        /// or live in its temporary folder). Only those get the automatic children's-video look.
        /// </summary>
        internal static bool IsAiCreatorClip(TimelineItem item)
        {
            string tags = item?.AudioDescription ?? "";
            return tags.Contains("context:") || tags.Contains("mood:") ||
                   (item?.Path ?? "").Contains("UVE_Story_", StringComparison.OrdinalIgnoreCase);
        }

        private static string _dtFont;

        /// <summary>
        /// v1.4 — an explicit font file for every drawtext. Without it FFmpeg asks fontconfig for
        /// a font, and the Windows builds of FFmpeg 8 crashed there (exit -1073741819), so the
        /// intro, the outro and all subtitles were lost. Arial (or Segoe UI / Tahoma) has all the
        /// Serbian letters (č ć š ž đ). Returns "fontfile='...':" or "" when no font is found.
        /// </summary>
        private static string DtFont
        {
            get
            {
                if (_dtFont != null) return _dtFont;
                _dtFont = "";
                try
                {
                    string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                    foreach (string f in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
                    {
                        string p = Path.Combine(fonts, f);
                        if (File.Exists(p)) { _dtFont = "fontfile='" + EscapeFilterPath(p) + "':"; break; }
                    }
                }
                catch { /* fontconfig stays as the fallback */ }
                return _dtFont;
            }
        }

        /// <summary>v1.4 — the Windows font folder for the karaoke (ass) filter, same reason as <see cref="DtFont"/>.</summary>
        private static string AssFontsDir
        {
            get
            {
                try
                {
                    string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                    return Directory.Exists(fonts) ? ":fontsdir='" + EscapeFilterPath(fonts) + "'" : "";
                }
                catch { return ""; }
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
            catch (Exception exDuration) { LogSwallowedError("FFmpeg duration parse", exDuration); }
            return 0.0;
        }

        private async Task<string> MixSecondaryAudioClips(
            string mainAudioPath,
            List<TimelineItem> secondaryClips,
            string outputPath,
            CancellationToken ct)
        {
            // CLEANUP FIX: Koristimo dedicirani temp folder umjesto Path.GetDirectoryName(outputPath).
            // Stari kod je pisao mix_batch_*.aac direktno u output folder (D:\Iskra kompozicije\)
            // and never deleted them — caused gigabytes of garbage with each render.
            string mixTempDir = Path.Combine(Path.GetTempPath(), $"UVE_MixAudio_{Guid.NewGuid().ToString("N").Substring(0, 8)}");
            Directory.CreateDirectory(mixTempDir);
            try
            {
                string currentAudio = mainAudioPath;
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
                    // CLEANUP FIX: All intermediate stages go into mixTempDir (temp), final output into outputPath
                    string batchOutput = isLast
                        ? outputPath
                        : Path.Combine(mixTempDir, $"mix_batch_{batchNum}_{Guid.NewGuid().ToString().Substring(0, 6)}.wav");

                    var inputs = new StringBuilder();
                    inputs.Append($"-i \"{currentAudio}\" ");

                    var filterParts = new List<string>();
                    int idx = 1;

                    foreach (var clip in batch)
                    {
                        // v1.4: honour the clip's in-point and its real volume (the old formula
                        // clamped everything to at least 50 %, so a 15 % ambience played at 50 %)
                        string clipIn = clip.SourceOffset > 0.01 ? $"-ss {clip.SourceOffset.ToString("F3", CultureInfo.InvariantCulture)} " : "";
                        inputs.Append($"{clipIn}-i \"{clip.Path}\" ");
                        long delayMs = (long)(clip.Start * 1000);
                        double vol = Math.Max(0.0, Math.Min(clip.Volume / 100.0, 4.0));
                        double clipDur = Math.Max(0.05, clip.Duration > 0 ? clip.Duration : 0.5);

                        double trimEnd = clip.Start + clipDur;
                        // v1.4 (R5): the clip's audio effects and its volume curve (t = timeline time here)
                        string clipFx = AudioEffects.GetFFmpegFilter(clip.AudioFx, clip.Duration);
                        string clipVol = AudioAutomation.HasCurve(clip)
                            ? AudioAutomation.VolumeFilter(clip, clip.Start)
                            : $"volume={vol.ToString("F2", CultureInfo.InvariantCulture)}";
                        filterParts.Add(
                            $"[{idx}:a]" +
                            (clipFx.Length > 0 ? clipFx + "," : "") +
                            $"adelay={delayMs}|{delayMs}," +
                            $"atrim=end={trimEnd.ToString("F3", CultureInfo.InvariantCulture)}," +
                            clipVol +
                            (TrackMixer.Filter(clip.TrackIndex) is string tf && tf.Length > 0 ? "," + tf : "") +   // v2.0: track mixer
                            $"[sa{idx}]");
                        idx++;
                    }

                    int numInputs = batch.Count + 1;
                    var mixInputs = "[0:a]" + string.Join("", Enumerable.Range(1, batch.Count).Select(i => $"[sa{i}]"));
                    filterParts.Add($"{mixInputs}amix=inputs={numInputs}:duration=first:normalize=0[aout]");

                    string filterComplex = string.Join("; ", filterParts);
                    string args = $"-nostdin {inputs}-filter_complex \"{filterComplex}\" " +
                                  // v1.4: lossless 32-bit float between the steps. Before, every batch
                                  // was encoded to AAC and then encoded again at the end (and once more in
                                  // the post pass) — that is what made the sound dull and "washed".
                                  $"-map \"[aout]\" -c:a pcm_f32le -y \"{batchOutput}\"";

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
            finally
            {
                // CLEANUP FIX: Delete temp folder with batch files
                try { if (Directory.Exists(mixTempDir)) Directory.Delete(mixTempDir, true); } catch { }
            }
        }

        private string ExtractTextFromName(string name)
        {
            if (name.Contains(":"))
            {
                return name.Substring(name.IndexOf(':') + 1).Trim();
            }
            if (name.StartsWith("Intro text:"))
                return name.Substring("Intro text:".Length).Trim();
            if (name.StartsWith("Outro text:"))
                return name.Substring("Outro text:".Length).Trim();
            return name;
        }

        private string EscapeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string result = text
                .Replace("\\", "\\\\\\\\")
                // v1.4: an apostrophe ends the quoted drawtext value ("nek' divan" broke the
                // whole filter), so it is drawn as the typographic ’ instead.
                .Replace("'", "\u2019")
                .Replace("\"", "\\\"")
                .Replace(":", "\\:")
                .Replace("%", "\\\\%")
                .Replace("@", "\\@");

            // v1.4: with an explicit font file (Arial) the Serbian letters are drawn as they are;
            // only without one are they simplified (č -> c), as before.
            if (DtFont.Length > 0) return result;

            result = result
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
                // that FFmpeg introduces between the audio and video tracks.
                // If video starts with a fade-in (e.g. 1.2s), subtitles must be
                // pomereni za istu vrednost da ostanu "hard-synced" sa glasom.
                //
                // Additional: seamless text flow — gap between subtitles max 50ms.
                // If a subtitle ends more than 50ms before the next one starts,
                // extend End of the previous one to Start of the next (no gap).

                var sortedSubs = subtitles
                    .OrderBy(s => s.Start)
                    .Select(s => new SubtitleItem
                    {
                        Text  = s.Text,
                        Start = Math.Max(0.0, s.Start + subtitleOffsetSeconds),
                        End   = Math.Max(0.0, s.End   + subtitleOffsetSeconds)
                    })
                    .ToList();

                // FREEZE FIX: Cap the maximum duration of a single subtitle.
                // Whisper ponekad vrati End=start+40s ako ne detektuje kraj segmenta.
                // Max = 8 seconds per line (enough for the longest children's song verse).
                // If the next subtitle starts earlier — use that as the boundary marker.
                const double MAX_SUBTITLE_DURATION = 8.0;
                for (int i = 0; i < sortedSubs.Count; i++)
                {
                    double maxEnd = sortedSubs[i].Start + MAX_SUBTITLE_DURATION;
                    // If there is a next subtitle that starts before maxEnd — that is the natural end
                    if (i < sortedSubs.Count - 1)
                        maxEnd = Math.Min(maxEnd, sortedSubs[i + 1].Start - 0.050);
                    if (sortedSubs[i].End > maxEnd)
                        sortedSubs[i].End = maxEnd;
                }

                // WHISPER HARD-SYNC: Seamless-flow is DISABLED.
                // Each subtitle lives exactly between its Whisper Start and End.
                // Silence or instrumental transition = clean screen (no old text).
                // Stari seamless-flow uzrokovao je "frozen text" problem:
                // text remained visible long after the voice stopped,
                // especially between 01:21 and the end of the video in instrumental sections.

                string srtFile = Path.Combine(tempDir, "subtitles.srt");
                using (var sw = new StreamWriter(srtFile, false, Encoding.UTF8))
                {
                    int index = 1;
                    foreach (var sub in sortedSubs)
                    {
                        if (sub.Start >= sub.End) continue; // skip invalid entries
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
                    // v1.4: phone photos store "rotate me" in EXIF; without this they came out sideways
                    image.AutoOrient();
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

        // v1.4 — GPU encoder fallback. NVENC can fail in the middle of a render (for example
        // when the graphics card's memory is taken by the Ollama AI models, or the card allows
        // only a few encoder sessions). Each failed GPU step is retried with the CPU encoder
        // (libx264), and once that helped, the rest of the render stays on the CPU.
        private bool _useCpuEncoder;

        private static string NvencToX264(string args) =>
            System.Text.RegularExpressions.Regex.Replace(args ?? "",
                @"-c:v h264_nvenc -preset \w+ -rc vbr -cq (\d+) -b:v 0",
                "-c:v libx264 -preset veryfast -crf $1");

        private async Task<bool> RunFFmpegAsync(string arguments, CancellationToken ct)
        {
            if (_useCpuEncoder && arguments.Contains("h264_nvenc"))
                arguments = NvencToX264(arguments);

            bool ok = await RunFFmpegOnceAsync(arguments, ct);
            if (!ok && !ct.IsCancellationRequested && arguments.Contains("h264_nvenc"))
            {
                string cpuArgs = NvencToX264(arguments);
                if (cpuArgs != arguments && await RunFFmpegOnceAsync(cpuArgs, ct))
                {
                    if (!_useCpuEncoder)
                        LogToMainWindow(LanguageManager.T("lx_render_the_graphics_card_encoder_a04ca"));
                    _useCpuEncoder = true;
                    return true;
                }
            }
            return ok;
        }

        private async Task<bool> RunFFmpegOnceAsync(string arguments, CancellationToken ct)
        {
            using var process = new Process
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
                    WriteFfmpegFailure(arguments, process.ExitCode, errorBuilder.ToString());
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("Render: running FFmpeg", ex);
                return false;
            }
        }

        /// <summary>
        /// v1.4 — when an FFmpeg step fails, its command and the end of its error output go to
        /// Logs\render_last_error.log (Help → Open the log folder). Before, the output was thrown
        /// away and a failed render could not be diagnosed.
        /// </summary>
        /// <summary>v1.4: spoken warning when clips failed and the picture had to be stretched.</summary>
        private void AnnounceShortVideo(double videoSeconds, double audioSeconds)
        {
            string msg = string.Format(L("re_video_short"), videoSeconds, audioSeconds, LastErrorLogPath);
            AppLog.Warn(msg);
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                        mw.LogMessage(msg, true);
                }));
            }
            catch { /* the window may be closing */ }
        }

        /// <summary>v2.0 — does the file have a real picture (not only sound, not only cover art)?</summary>
        private async Task<bool> HasPictureAsync(string path, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || string.IsNullOrEmpty(AppPaths.FFprobe) || !File.Exists(AppPaths.FFprobe))
                    return true;   // can't tell: try as before
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo(AppPaths.FFprobe,
                        $"-v error -select_streams v -show_entries stream=codec_name:stream_disposition=attached_pic -of csv=p=0 \"{path}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
                };
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                await errTask;
                string o = (await outTask).Trim();
                if (p.ExitCode != 0) return true;
                // one line per video stream: "h264,0" — the last field is 1 for cover art
                return o.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.EndsWith(",1"));
            }
            catch (OperationCanceledException) { throw; }
            catch { return true; }
        }

        public static string LastErrorLogPath => Path.Combine(AppPaths.LogsDir, "render_last_error.log");

        private static void WriteFfmpegFailure(string arguments, int exitCode, string stderr)
        {
            try
            {
                var lines = (stderr ?? "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                string tail = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 60)));
                // v1.4: appended, so every failed step of a render is kept (not only the last one)
                File.AppendAllText(LastErrorLogPath,
                    $"══════════════════════════════════════════{Environment.NewLine}" +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  FFmpeg exit code {exitCode}{Environment.NewLine}{Environment.NewLine}" +
                    $"Command:{Environment.NewLine}ffmpeg {arguments}{Environment.NewLine}{Environment.NewLine}" +
                    $"Error output (last lines):{Environment.NewLine}{tail}{Environment.NewLine}");
                AppLog.Warn($"Render: an FFmpeg step failed (exit {exitCode}): {lines.LastOrDefault()}");
            }
            catch (Exception ex)
            {
                AppLog.Error("Writing the render error log", ex);
            }
        }

        private async Task<string> RunFFmpegGetOutputAsync(string arguments, CancellationToken ct)
        {
            using var process = new Process
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

        /// <summary>
        /// v2.0 — the transition from <paramref name="item"/> into the next picture clip, as the
        /// export makes it (and live playback shows it): the type and length chosen in the
        /// Transitions tab, or the AI creator's choice, or otherwise a soft dissolve / fade of
        /// 0.25 s (fast pacing), 0.7 s (slow) or 0.5 s. <paramref name="previous"/> is the clip
        /// before it (for the brightness rule); <paramref name="slot"/> its time on the timeline.
        /// </summary>
        public static (string Type, double Duration) TransitionAfter(TimelineItem item, TimelineItem previous, double slot)
        {
            string desc = item.AudioDescription ?? "";
            double fade = ExtractTag(desc, "pacing") switch { "fast" => 0.25, "slow" => 0.70, _ => 0.50 };
            if (item.TransitionOutDuration > 0.05)
                fade = Math.Min(item.TransitionOutDuration, Math.Max(0.1, slot - 0.1));
            string type = !string.IsNullOrEmpty(item.TransitionOut) ? item.TransitionOut : ExtractTag(desc, "xfade");
            if (string.IsNullOrEmpty(type) || type == "fade")
            {
                // similar brightness → dissolve, different → fade (anchor_brightness from the AI creator; 0 otherwise)
                static double Bright(string d)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(d ?? "", @"anchor_brightness=([\d.]+)");
                    return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
                }
                type = Math.Abs(Bright(desc) - Bright(previous?.AudioDescription)) < 0.03 ? "dissolve" : "fade";
            }
            if (!ValidXfadeNames.Contains(type ?? "")) type = "fade";
            return (type, fade);
        }

        private static string ExtractTag(string text, string key)
        {
            // v1.4: tags are written both as "key:value" (mood, pacing, season) and as
            // "key=value" (xfade, energy...). The old version only understood ':', so the
            // AI's per-clip transitions ("xfade=slideleft") were never used.
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key)) return "";
            foreach (var part in text.Split('|'))
            {
                int sep = part.IndexOfAny(new[] { ':', '=' });
                if (sep <= 0) continue;
                if (part.Substring(0, sep).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return part.Substring(sep + 1).Trim();
            }
            return "";
        }

        /// <summary>Transition names FFmpeg's xfade filter accepts (FFmpeg 5.1+).</summary>
        private static readonly HashSet<string> ValidXfadeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "fade", "wipeleft", "wiperight", "wipeup", "wipedown", "slideleft", "slideright", "slideup", "slidedown",
            "circlecrop", "rectcrop", "distance", "fadeblack", "fadewhite", "radial", "smoothleft", "smoothright",
            "smoothup", "smoothdown", "circleopen", "circleclose", "vertopen", "vertclose", "horzopen", "horzclose",
            "dissolve", "pixelize", "diagtl", "diagtr", "diagbl", "diagbr", "hlslice", "hrslice", "vuslice", "vdslice",
            "hblur", "fadegrays", "wipetl", "wipetr", "wipebl", "wipebr", "squeezeh", "squeezev", "zoomin"
        };

        // ══════════════════════════════════════════════════════════════════════════
        //  ApplyCrossfadePairwise — robustni crossfade, par-po-par
        //
        //  Why this is better than the old ApplyCrossfade:
        //  Stari: jedan filter_complex za N klipova → offset akumulacija → zeleni frejmovi
        //  Novi: klip[0]+klip[1] → temp1, temp1+klip[2] → temp2, ...
        //         Each step uses measured duration → no cumulative error
        //
        //  Crossfade = overlap: end of clip[i] and start of clip[i+1] overlap.
        //  Efektivno trajanje klipa se smanjuje za fadeDuration (overlap).
        //  Ukupno trajanje videa = sum(dur) - (n-1)*fadeDuration
        // ══════════════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════════
        //  ApplyCrossfadeSinglePass — cross-dissolve u jednom FFmpeg prolazu
        //
        //  Algoritam:
        //  1. Normalizuj sve klipove na isti FPS i format (brzo, bez re-enc kvaliteta)
        //  2. Izmjeri stvarna trajanja normalizovanih klipova
        //  3. Build filter_complex with xfade filters and precise offsets
        //  4. Jedan FFmpeg poziv spaja sve klipove sa cross-dissolve tranzicijama
        //
        //  Why it is faster than pairwise:
        //  pairwise: N-1 FFmpeg re-enkodiranja sekvencijalno
        //  single-pass: 1 normalizacija (copyts, brzo) + 1 xfade (re-enc jednom)
        //
        //  Limit: FFmpeg filter_complex has a limit of ~60 inputs.
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

            string TARGET_FPS = _fps;   // v2.0: the render's frame rate (was always 30)
            // v1.4: the resolution the user chose (was hard-coded 1920:1080, so 9:16 / 720p
            // renders were letterboxed back into 16:9 whenever crossfades were used)
            string TARGET_RES = $"{_xfW}:{_xfH}";
            string ENC = _xfEnc;   // v2.0: follows the export quality

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
                        $"setsar=1,format=yuv420p" +
                        CrossfadeCompensation(i, videoFiles.Count, FadeFor(i));

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
                    LogToMainWindow(LanguageManager.T("lx_crossfades_one_of_the_clips_f1335"));
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
                        LogToMainWindow(LanguageManager.F("lx_crossfades_clip_is_too_short_d0d36", i - 1, prevDur, thisFade));
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
                LogToMainWindow(LanguageManager.F("lx_crossfade_error_b0002", ex.Message));
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

            string TARGET_FPS = _fps;   // v2.0
            int TARGET_W = _xfW;   // v1.4: chosen resolution (was hard-coded 1920x1080)
            int TARGET_H = _xfH;

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
                    $"setsar=1,format=yuv420p" +
                    CrossfadeCompensation(i, videoFiles.Count, FadeFor(i));

                string normArgs =
                    $"-nostdin -i \"{input}\" " +
                    $"-vf \"{vf}\" " +
                    $"{encArgs} {pixFmt} -an -y \"{normPath}\"";

                bool ok = await RunFFmpegAsync(normArgs, ct);

                if (!ok || !File.Exists(normPath) || new FileInfo(normPath).Length < 500)
                {
                    LogToMainWindow(LanguageManager.F("lx_preparing_clip_for_the_crossfade_9737b", i));
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
                    LogToMainWindow(LanguageManager.F("lx_the_crossfade_failed_a_hard_997ac", i - 1, i));

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

            string FPS_NORM = $"fps=fps={_fps}:round=near";   // v2.0

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

        // v2.0: frame rate and intermediate encoder of the current render
        private string _fps = "30";
        private string _xfEnc = "-c:v libx264 -preset veryfast -crf 22 -profile:v high -level 4.1 -pix_fmt yuv420p";

        /// <summary>v2.0 — a picture clip on a layer track (Video 2 and up) that is not hidden.</summary>
        internal static bool IsLayerClip(TimelineItem i) =>
            i != null && TrackSet.IsLayer(i.TrackIndex) && !TrackSet.IsHidden(i.TrackIndex) &&
            !(i.Type == "Audio" || i.IsAudio) && (i.IsVideo || i.IsImage) &&
            !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path);

        /// <summary>
        /// v2.0 — the output frame rate: the one chosen in Export options, or (Auto) the frame
        /// rate of the first video clip. Projects of the AI music video creator stay at 30.
        /// </summary>
        private async Task<string> ResolveFrameRateAsync(ExportSettingsData settings, List<TimelineItem> items, CancellationToken ct)
        {
            string chosen = settings?.FrameRate;
            if (!string.IsNullOrWhiteSpace(chosen) && chosen != "Auto" && LayerCompositor.ParseRate(chosen) >= 10)
                return chosen;
            if (items.Any(IsAiCreatorClip)) return "30";
            var firstVideo = items.Where(i => (i.Type == "Video" || i.IsVideo) && !i.IsImage && !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path))
                                  .OrderBy(i => i.TrackIndex).ThenBy(i => i.Start).FirstOrDefault();
            if (firstVideo == null) return "30";
            try
            {
                var probe = new LayerCompositor(AppPaths.FFprobe, RunFFmpegAsync, LogToMainWindow);
                string r = await probe.ProbeFrameRateAsync(firstVideo.Path, ct);
                if (!string.IsNullOrEmpty(r))
                {
                    LogToMainWindow(LanguageManager.F("lx_frame_rate_taken_from_fps_9eeed", Path.GetFileName(firstVideo.Path), r));
                    return r;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogSwallowedError("Reading the frame rate", ex); }
            return "30";
        }

        /// <summary>v2.0 — a timeline clip as the compositor sees it.</summary>
        /// <summary>v2.0 — a quick preview of the whole video: the slow smooth slow motion is left out.</summary>
        public bool PreviewMode { get; set; }

        internal static CompositeClip ToComposite(TimelineItem i) => new CompositeClip
        {
            Name = i.Name ?? "",
            Path = i.Path,
            Start = i.Start,
            Duration = i.Duration,
            SourceOffset = i.SourceOffset,
            IsImage = i.IsImage || i.Type == "Image",
            Volume = i.Volume,
            TrackFx = TrackMixer.Filter(i.TrackIndex),   // v2.0: the track mixer (volume, automation, mute, solo, EQ, effects)
            Track = i.TrackIndex,
            Layer = i.Layer,
            LutPath = i.LutPath,
            Title = i.TextOverlay,   // v2.0: text on a clip is drawn (it was stored but never rendered)
            Tracking = i.Tracking,   // v2.0: a followed object (blur, pixelate, black box, spotlight; a mask that follows it)
            // v2.0: main-track clips play at their speed; layers always at normal speed
            Speed = TrackSet.IsLayer(i.TrackIndex) ? 1.0 : Math.Clamp(SpeedRamp.Has(i) ? SpeedRamp.Average(i) : i.VideoEffect?.Speed is double sp && sp > 0.01 ? sp : 1.0, 0.1, 10),
            Keyframes = (i.Keyframes ?? new List<AnimationKeyframe>()).Select(k => new CompositeKeyframe
            {
                Time = k.Time, Zoom = k.Zoom, Rotation = k.Rotation, X = k.X, Y = k.Y, Opacity = k.Opacity, Easing = k.Easing ?? "Linear"
            }).ToList()
        };

        // v1.4: output size for the crossfade passes (set per render from the chosen resolution)
        private int _xfW = 1920, _xfH = 1080;

        /// <summary>
        /// v1.4 — a crossfade overlaps two clips by the fade length, which used to make the
        /// whole video shorter by (number of cuts × fade) and pull every later clip earlier
        /// than its place on the timeline. Each clip except the last is therefore extended
        /// by its fade (last frame held), so after the overlap the next clip starts exactly
        /// where the timeline says.
        /// </summary>
        private static string CrossfadeCompensation(int index, int count, double fade)
        {
            if (index >= count - 1 || fade <= 0.01) return "";
            return $",tpad=stop_mode=clone:stop_duration={fade.ToString("F3", CultureInfo.InvariantCulture)}";
        }

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
        /// <summary>v2.0: the render's frame rate (was a fixed 30).</summary>
        internal static string TargetFps = "30";
        private static string TARGET_FPS => TargetFps;
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
            // FIX-DENOISE: same filter as in main render loop — normalizes clip texture
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

            string durationStr = item.Duration.ToString("0.###", CultureInfo.InvariantCulture);

            return $"-nostdin -stream_loop -1 -t {durationStr} " +
                   $"-i \"{item.Path}\" " +
                   $"-vf \"{videoVf}\" " +
                   $"{vEncArgs} {vsyncCfr} {pixFmt} -an -y \"{tempVideo}\"";
        }
    }
}