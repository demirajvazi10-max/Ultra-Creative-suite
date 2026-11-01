using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — how a clip on a layer track (Video 2) sits on top of the picture below it.
    /// Saved with the project as part of the clip.
    /// </summary>
    public class LayerSettings
    {
        /// <summary>Full, Center, TopLeft, TopRight, BottomLeft, BottomRight or Custom.</summary>
        public string Position { get; set; } = "Full";

        /// <summary>Custom position: the layer's centre, in percent of the frame width / height.</summary>
        public double X { get; set; } = 50;
        public double Y { get; set; } = 50;

        /// <summary>Width of the layer in percent of the frame width (Full ignores it and fits the frame).</summary>
        public double Size { get; set; } = 100;

        /// <summary>0 = invisible, 1 = solid.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>"" (off), "green" or "blue" — that colour becomes transparent (green / blue screen).</summary>
        public string ChromaKey { get; set; } = "";

        /// <summary>How close to the key colour a pixel must be to disappear (0.05 – 0.6).</summary>
        public double KeySimilarity { get; set; } = 0.3;

        /// <summary>The layer's own sound is mixed in (video clips only).</summary>
        public bool KeepAudio { get; set; } = true;

        /// <summary>v2.0 — how the layer mixes with the picture below: Normal, Screen, Multiply, Overlay, SoftLight, Lighten, Darken, Add, Difference.</summary>
        public string BlendMode { get; set; } = "Normal";

        /// <summary>v2.0 — a shape that cuts the layer out: None, Ellipse or Rectangle (with soft edges).</summary>
        public string Mask { get; set; } = "None";

        /// <summary>v2.0 — how soft the mask's edge is, in percent of the layer size (0 = sharp).</summary>
        public double MaskFeather { get; set; } = 10;

        // v2.0 — masks of any shape: the shape's box (centre and size in percent of the layer),
        // turned inside out, own points, moving from one box to another, or following an object.

        /// <summary>Centre of the mask box, in percent of the layer width / height.</summary>
        public double MaskX { get; set; } = 50;
        public double MaskY { get; set; } = 50;
        /// <summary>Size of the mask box, in percent of the layer width / height.</summary>
        public double MaskW { get; set; } = 100;
        public double MaskH { get; set; } = 100;
        /// <summary>Inside out: the shape is cut away and the rest of the layer shows.</summary>
        public bool MaskInvert { get; set; }
        /// <summary>The points of a custom shape: "x y; x y; …" in percent of the mask box.</summary>
        public string MaskPoints { get; set; } = "";
        /// <summary>The mask moves during the clip from its box to the end box.</summary>
        public bool MaskMoves { get; set; }
        public double MaskEndX { get; set; } = 50;
        public double MaskEndY { get; set; } = 50;
        public double MaskEndW { get; set; } = 100;
        public double MaskEndH { get; set; } = 100;
        /// <summary>The mask follows the object tracked in this clip (Follow an object, Ctrl+Alt+O).</summary>
        public bool MaskFollows { get; set; }

        public LayerSettings Clone() => (LayerSettings)MemberwiseClone();

        public static readonly string[] BlendModes = { "Normal", "Screen", "Multiply", "Overlay", "SoftLight", "Lighten", "Darken", "Add", "Difference" };

        public static readonly string[] Presets = { "Full", "Center", "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Custom" };

        /// <summary>Picture-in-picture presets get a sensible size when chosen.</summary>
        public void ApplyPreset(string preset)
        {
            Position = Presets.Contains(preset) ? preset : "Full";
            if (Position == "Full") Size = 100;
            else if (Position != "Custom" && Size >= 99) Size = 30;
        }
    }

    /// <summary>v2.0 — a keyframe as the compositor sees it (time in seconds from the clip's start).</summary>
    public class CompositeKeyframe
    {
        public double Time, Zoom = 1, Rotation, X, Y, Opacity = 1;
        /// <summary>v2.0 — Linear, EaseIn, EaseOut, EaseInOut or Hold (the way to the next keyframe).</summary>
        public string Easing = "Linear";
    }

    /// <summary>v2.0 — one clip handed to the compositor.</summary>
    public class CompositeClip
    {
        public string Name = "";
        public string Path = "";
        public double Start, Duration, SourceOffset;
        public bool IsImage;
        public double Volume = 100;
        public int Track;
        public LayerSettings Layer;
        public string LutPath;
        public List<CompositeKeyframe> Keyframes = new List<CompositeKeyframe>();
        /// <summary>v2.0 — the clip's title (text drawn over it), or null.</summary>
        public TextOverlayData Title;
        internal string TitleFile, SubtitleFile;
        /// <summary>v2.0 — the mask as a ready-made grey picture (white = visible), made once before drawing.</summary>
        internal string MaskFile;
        /// <summary>v2.0 — the mask file holds only the moving box (not the whole frame).</summary>
        internal bool MaskIsBox;
        /// <summary>v2.0 — seconds cut off the clip's start (live playback starting in its middle).</summary>
        internal double Cut;
        /// <summary>v2.0 — playback speed of the clip (maps file time to timeline time).</summary>
        public double Speed = 1;
        /// <summary>v2.0 — the object followed through the clip, and its effect.</summary>
        public TrackingData Tracking;
        /// <summary>v2.0 — the track mixer's filters for this clip's sound ("" = none); t is timeline time there.</summary>
        public string TrackFx = "";
    }

    /// <summary>
    /// v2.0 — the last stage of a render. On top of the finished picture it
    ///  • draws the clips of the layer tracks (Video 2 …): picture in picture, logos,
    ///    green/blue screen, with position, size and opacity, and mixes in their sound;
    ///  • animates clips that have keyframes (zoom, position, rotation, opacity);
    ///  • applies a colour LUT (.cube) to the clips that have one.
    /// Everything happens in one FFmpeg pass. If the animated version fails, it is tried
    /// once more without animation (each clip keeps its first keyframe), so the layers are
    /// never lost because of one expression.
    /// </summary>
    public class LayerCompositor
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly string _ffprobe;
        private readonly Func<string, CancellationToken, Task<bool>> _run;
        private readonly Action<string> _log;

        public LayerCompositor(string ffprobePath, Func<string, CancellationToken, Task<bool>> runFfmpeg, Action<string> log)
        {
            _ffprobe = ffprobePath;
            _run = runFfmpeg;
            _log = log ?? (_ => { });
        }

        private static string N(double v) => v.ToString("0.####", Inv);

        /// <summary>True when there is anything for the compositor to do.</summary>
        public static bool IsNeeded(IEnumerable<CompositeClip> layers, IEnumerable<CompositeClip> mainClips) =>
            (layers?.Any() ?? false) ||
            (mainClips?.Any(c => HasAnimation(c) || HasLut(c) || HasTitle(c) || HasRegion(c)) ?? false);

        /// <summary>v2.0 — the clip has a followed object with an effect (blur, pixelate, black box, spotlight).</summary>
        public static bool HasRegion(CompositeClip c) => c?.Tracking != null && c.Tracking.HasEffect;

        /// <summary>v2.0 — the clip has a title to draw.</summary>
        public static bool HasTitle(CompositeClip c) =>
            c.Title != null && c.Title.Enabled && !string.IsNullOrWhiteSpace(c.Title.Text);

        /// <summary>v2.0 — the font for titles, as FFmpeg's drawtext wants it ("fontfile='…':" or empty).</summary>
        public static string FontSpec = "";

        /// <summary>v2.0 — a Windows font for drawtext (Arial, Segoe UI, Tahoma or Verdana), or "" for FFmpeg's default.</summary>
        public static string DefaultFontSpec()
        {
            try
            {
                string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                foreach (string f in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
                {
                    string p = Path.Combine(fonts, f);
                    if (File.Exists(p)) return "fontfile='" + EscapeFilterPath(p) + "':";
                }
            }
            catch { }
            return "";
        }

        /// <summary>v2.0 — writes the title texts of the clips to small UTF-8 files in <paramref name="dir"/>; returns the files.</summary>
        internal static List<string> WriteTitleFiles(IEnumerable<CompositeClip> clips, string dir, string tag)
        {
            var files = new List<string>();
            int tn = 0;
            foreach (var c in clips.Where(HasTitle))
            {
                c.TitleFile = Path.Combine(dir, $"title_{tag}{tn}.txt");
                File.WriteAllText(c.TitleFile, c.Title.Text.Trim(), new UTF8Encoding(false));
                files.Add(c.TitleFile);
                if (!string.IsNullOrWhiteSpace(c.Title.Subtitle))
                {
                    c.SubtitleFile = Path.Combine(dir, $"title_{tag}{tn}_2.txt");
                    File.WriteAllText(c.SubtitleFile, c.Title.Subtitle.Trim(), new UTF8Encoding(false));
                    files.Add(c.SubtitleFile);
                }
                tn++;
            }
            return files;
        }

        public static bool HasLut(CompositeClip c) => !string.IsNullOrEmpty(c.LutPath) && File.Exists(c.LutPath);

        public static bool HasAnimation(CompositeClip c) =>
            c.Keyframes != null && c.Keyframes.Any(k =>
                Math.Abs(k.Zoom - 1) > 0.001 || Math.Abs(k.Rotation) > 0.01 ||
                Math.Abs(k.X) > 0.5 || Math.Abs(k.Y) > 0.5 || Math.Abs(k.Opacity - 1) > 0.001);

        /// <summary>
        /// Composites onto <paramref name="videoPath"/> and replaces it. Returns true when the
        /// file was changed; false when nothing was needed or every attempt failed (the
        /// original is then left as it was).
        /// </summary>
        public async Task<bool> ApplyAsync(string videoPath, List<CompositeClip> layers, List<CompositeClip> mainClips,
                                           int width, int height, string fps, string videoEncArgs, CancellationToken ct)
        {
            layers = (layers ?? new List<CompositeClip>()).Where(l => !string.IsNullOrEmpty(l.Path) && File.Exists(l.Path) && l.Duration > 0.05).ToList();
            mainClips = (mainClips ?? new List<CompositeClip>()).Where(c => HasAnimation(c) || HasLut(c) || HasTitle(c) || HasRegion(c)).ToList();
            if (layers.Count == 0 && mainClips.Count == 0) return false;
            if (!File.Exists(videoPath)) return false;

            double baseDur = await ProbeDurationAsync(videoPath, ct);
            // without a readable length or frame rate the command can't be built safely: keep the video as it is
            if (baseDur <= 0.05) { _log("⚠️ Layers: the length of the rendered video could not be read — the video is kept without layers."); return false; }
            if (ParseRate(fps) < 10) fps = "30";
            bool baseHasAudio = await ProbeHasAudioAsync(videoPath, ct);
            var sizes = new Dictionary<CompositeClip, (int w, int h)>();
            var hasAudio = new Dictionary<CompositeClip, bool>();
            foreach (var l in layers)
            {
                sizes[l] = await ProbeSizeAsync(l.Path, ct);
                hasAudio[l] = !l.IsImage && (l.Layer?.KeepAudio ?? true) && l.Volume > 0 && await ProbeHasAudioAsync(l.Path, ct);
            }
            foreach (var c in mainClips.Where(HasRegion))   // v2.0: where the followed object is depends on the clip's own size
                sizes[c] = await ProbeSizeAsync(c.Path, ct);

            string tmp = Path.Combine(Path.GetDirectoryName(videoPath) ?? Path.GetTempPath(),
                                      Path.GetFileNameWithoutExtension(videoPath) + "_layers" + Path.GetExtension(videoPath));

            // v2.0: title texts go through small UTF-8 files (no character needs escaping),
            // and the layers' masks are made once as pictures
            string workDir = Path.GetDirectoryName(videoPath) ?? Path.GetTempPath();
            var titleFiles = WriteTitleFiles(layers.Concat(mainClips), workDir, "");
            titleFiles.AddRange(await PrepareMasksAsync(layers, sizes, workDir, _run, ct));
            try
            {
            foreach (bool animate in new[] { true, false })
            {
                if (!animate && !layers.Concat(mainClips).Any(HasAnimation)) break;
                string args = BuildArgs(videoPath, tmp, layers, mainClips, sizes, hasAudio, baseHasAudio, baseDur,
                                        width, height, fps, videoEncArgs, animate);
                _log(animate
                    ? $"🧩 Layers: {layers.Count} layer clip(s), {mainClips.Count(HasAnimation)} animated and {mainClips.Count(HasLut) + layers.Count(HasLut)} colour-graded clip(s), {layers.Concat(mainClips).Count(HasTitle)} title(s)"
                    : "🧩 Layers: trying again without animation");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (await _run(args, ct) && File.Exists(tmp) && new FileInfo(tmp).Length > 10_000)
                {
                    File.Delete(videoPath);
                    File.Move(tmp, videoPath);
                    return true;
                }
            }
            }
            finally
            {
                foreach (var f in titleFiles) try { File.Delete(f); } catch { }
            }
            _log("⚠️ Layers could not be drawn — the video is kept without them. Details are in the render log.");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }

        // ── the FFmpeg command ───────────────────────────────────────────────────────

        internal static string BuildArgs(string input, string output, List<CompositeClip> layers, List<CompositeClip> mainClips,
                                         Dictionary<CompositeClip, (int w, int h)> sizes, Dictionary<CompositeClip, bool> hasAudio,
                                         bool baseHasAudio, double baseDur, int W, int H, string fps, string videoEncArgs, bool animate)
        {
            var inputs = new StringBuilder($"-nostdin -i \"{input}\" ");
            var g = new List<string>();
            int inputIndex = 1;
            string cur = "[0:v]";
            var audioLabels = ComposeVideo(inputs, ref inputIndex, ref cur, g, layers, mainClips, sizes, hasAudio, baseDur, W, H, fps, animate);
            return FinishArgs(inputs, inputIndex, cur, g, audioLabels, baseHasAudio, baseDur, fps, videoEncArgs, output);
        }

        /// <summary>
        /// v2.0 — draws everything over the picture <paramref name="cur"/>: colour LUTs and
        /// animation of the main clips, the layers (with blend modes and masks) and the titles.
        /// Shared by the export (over the rendered video) and the live timeline player (over the
        /// main track it assembles). Returns the labels of the layers' sound.
        /// </summary>
        internal static List<string> ComposeVideo(StringBuilder inputs, ref int inputIndex, ref string cur, List<string> g,
                                                  List<CompositeClip> layers, List<CompositeClip> mainClips,
                                                  Dictionary<CompositeClip, (int w, int h)> sizes, Dictionary<CompositeClip, bool> hasAudio,
                                                  double baseDur, int W, int H, string fps, bool animate)
        {
            int step = 0;
            string prefix = $"c{g.Count}_";
            string Next() => $"[{prefix}v{++step}]";

            // 1. colour LUTs of the clips on the main track, each only during its clip
            foreach (var c in mainClips.Where(HasLut))
            {
                string o = Next();
                g.Add($"{cur}lut3d=file='{EscapeFilterPath(c.LutPath)}':enable='between(t,{N(c.Start)},{N(c.Start + c.Duration)})'{o}");
                cur = o;
            }

            // 2. animated clips of the main track: the clip's part of the picture is cut out,
            //    transformed and put back over a black box (zoomed out = black around it)
            foreach (var c in mainClips.Where(HasAnimation))
            {
                double s = c.Start, e = c.Start + c.Duration;
                var kf = animate ? c.Keyframes.OrderBy(k => k.Time).ToList() : c.Keyframes.OrderBy(k => k.Time).Take(1).ToList();
                string keep = $"[m{step}k]", cut = $"[m{step}c]", seg = $"[m{step}s]", boxed = $"[m{step}b]";
                g.Add($"{cur}split=2{keep}{cut}");
                var chain = new List<string>
                {
                    $"trim=start={N(s)}:end={N(e)}", "setpts=PTS-STARTPTS", "format=rgba"
                };
                // opacity first: FFmpeg's geq crashes on frames whose size changes (animated zoom)
                AddOpacity(chain, kf, 1.0);
                if (kf.Any(k => Math.Abs(k.Rotation) > 0.01))
                    chain.Add($"rotate=a='({Kf(kf, k => k.Rotation, "t")})*PI/180':c=black:ow=iw:oh=ih");
                string z = Kf(kf, k => Math.Max(0.05, k.Zoom), "t");
                chain.Add($"scale=w='trunc({W}*({z})/2)*2':h='trunc({H}*({z})/2)*2':eval=frame");
                chain.Add($"setpts=PTS+{N(s)}/TB");
                g.Add($"{cut}{string.Join(",", chain)}{seg}");
                g.Add($"{keep}drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='between(t,{N(s)},{N(e)})'{boxed}");
                double sx = W / 1920.0;
                string o = Next();
                g.Add($"{boxed}{seg}overlay=x='{N(W / 2.0)}+({Kf(kf, k => k.X * sx, $"(t-{N(s)})")})-w/2':" +
                      $"y='{N(H / 2.0)}+({Kf(kf, k => k.Y * sx, $"(t-{N(s)})")})-h/2':eval=frame:eof_action=pass:" +
                      $"enable='between(t,{N(s)},{N(e)})'{o}");
                cur = o;
            }

            // 2b. v2.0: followed objects on the main track — blurred, pixelated, covered or lit,
            //     only while their clip is on screen
            int rn = 0;
            foreach (var c in mainClips.Where(HasRegion))
            {
                var (sw0, sh0) = sizes.TryGetValue(c, out var csz) && csz.w > 0 && csz.h > 0 ? csz : (W, H);
                double k = Math.Min((double)W / sw0, (double)H / sh0);
                double ox = (W - sw0 * k) / 2, oy = (H - sh0 * k) / 2;
                double sp = c.Speed > 0.01 ? c.Speed : 1;
                string o = RegionGraph(g, cur, $"m{prefix}{rn++}", c.Tracking, W, H, sw0 * k, sh0 * k, ox, oy,
                                       ft => c.Start + (ft - c.SourceOffset) / sp, "t", c.Start, c.Start + c.Duration);
                if (o != null) cur = o;
            }

            // 3. layers, lowest track first, then by time
            var audioLabels = new List<string>();
            int li = 0;
            foreach (var l in layers.OrderBy(x => x.Track).ThenBy(x => x.Start))
            {
                var ls = l.Layer ?? new LayerSettings();
                int idx = inputIndex++;
                double s = l.Start, d = l.Duration, e = s + d;
                if (l.IsImage)
                    inputs.Append($"-loop 1 -framerate {fps} -t {N(d)} -i \"{l.Path}\" ");
                else
                    inputs.Append((l.SourceOffset > 0.01 ? $"-ss {N(l.SourceOffset)} " : "") + $"-t {N(d)} -i \"{l.Path}\" ");

                var kf = (l.Keyframes ?? new List<CompositeKeyframe>()).OrderBy(k => k.Time).ToList();
                if (!animate && kf.Count > 1) kf = kf.Take(1).ToList();
                bool rotates = kf.Any(k => Math.Abs(k.Rotation) > 0.01);

                var (sw, sh) = sizes.TryGetValue(l, out var sz) && sz.w > 0 && sz.h > 0 ? sz : (1920, 1080);
                double aspect = (double)sw / sh;

                // width of the layer at zoom 1
                double w0 = ls.Position == "Full"
                    ? Math.Min(W, H * aspect)
                    : W * Math.Clamp(ls.Size, 2, 400) / 100.0;
                double h0 = w0 / aspect;

                // centre of the layer at zoom 1
                double margin = Math.Round(0.03 * Math.Min(W, H));
                (double cx, double cy) = ls.Position switch
                {
                    "TopLeft" => (margin + w0 / 2, margin + h0 / 2),
                    "TopRight" => (W - margin - w0 / 2, margin + h0 / 2),
                    "BottomLeft" => (margin + w0 / 2, H - margin - h0 / 2),
                    "BottomRight" => (W - margin - w0 / 2, H - margin - h0 / 2),
                    "Custom" => (W * ls.X / 100.0, H * ls.Y / 100.0),
                    _ => (W / 2.0, H / 2.0)
                };

                var chain = new List<string>();
                chain.Add(l.IsImage ? $"fps={fps}" : $"setpts=PTS-STARTPTS,fps={fps}");

                // v2.0: a followed object inside the layer (in the layer's own picture and time)
                string layerSrc = $"[{idx}:v]";
                if (HasRegion(l))
                {
                    string raw = $"[lraw{li}]";
                    g.Add($"{layerSrc}{string.Join(",", chain)}{raw}");
                    chain = new List<string>();
                    string ro = RegionGraph(g, raw, $"l{prefix}{li}", l.Tracking, sw, sh, sw, sh, 0, 0,
                                            ft => ft - l.SourceOffset, "t", 0, d);
                    layerSrc = ro ?? raw;
                    if (ro == null) chain.Add("null");
                }
                if (HasLut(l)) chain.Add($"lut3d=file='{EscapeFilterPath(l.LutPath)}'");
                chain.Add("format=rgba");
                if (ls.ChromaKey == "green")
                    chain.Add($"colorkey=color=0x00B140:similarity={N(Math.Clamp(ls.KeySimilarity, 0.01, 0.9))}:blend=0.08");
                else if (ls.ChromaKey == "blue")
                    chain.Add($"colorkey=color=0x0047BB:similarity={N(Math.Clamp(ls.KeySimilarity, 0.01, 0.9))}:blend=0.08");

                // v2.0: a mask shape (oval or rectangle with soft edges) cuts the layer out.
                // A ready-made mask picture is merged into the alpha channel (fast); the per-pixel
                // formula is only the fallback (it is very slow on video).
                string maskExpr = MaskAlpha(ls);
                string layerIn = layerSrc;
                if (HasMask(ls) && !string.IsNullOrEmpty(l.MaskFile) && File.Exists(l.MaskFile))
                {
                    int mi = inputIndex++;
                    string pre = $"[lpre{li}]", lm = $"[lm{li}]", lmk = $"[lmk{li}]";
                    if (chain.Count == 0) chain.Add("null");
                    g.Add($"{layerIn}{string.Join(",", chain)}{pre}");
                    if (l.MaskIsBox)
                    {
                        // v2.0: a moving mask — the shape's box is scaled and moved over a plain picture
                        inputs.Append($"-loop 1 -framerate {fps} -t {N(d + 0.5)} -i \"{l.MaskFile}\" ");
                        var path = MaskPath(l, sw, sh);
                        string bx = $"[lbx{li}]", cv = $"[lcv{li}]";
                        g.Add($"color=c={(ls.MaskInvert ? "white" : "black")}:s={sw}x{sh}:r={fps}:d={N(d + 0.5)},format=gray{cv}");
                        g.Add($"[{mi}:v]format=gray,scale=w='max(2,trunc({Pw(path, p => p.W, "t")}))':h='max(2,trunc({Pw(path, p => p.H, "t")}))':eval=frame{bx}");
                        g.Add($"{cv}{bx}overlay=x='{Pw(path, p => p.X, "t")}-w/2':y='{Pw(path, p => p.Y, "t")}-h/2':eval=frame:shortest=1,format=gray{lm}");
                    }
                    else
                    {
                        inputs.Append($"-loop 1 -i \"{l.MaskFile}\" ");
                        g.Add($"[{mi}:v]format=gray,scale={sw}:{sh}{lm}");
                    }
                    if (!string.IsNullOrEmpty(ls.ChromaKey))
                    {
                        // keep the green-screen transparency: multiply the key's alpha by the mask
                        g.Add($"{pre}split[lp1_{li}][lp2_{li}]");
                        g.Add($"[lp1_{li}]alphaextract[la{li}]");
                        g.Add($"[la{li}]{lm}blend=all_mode=multiply:shortest=1[lam{li}]");
                        g.Add($"[lp2_{li}][lam{li}]alphamerge=shortest=1{lmk}");
                    }
                    else g.Add($"{pre}{lm}alphamerge=shortest=1{lmk}");
                    layerIn = lmk;
                    chain = new List<string>();
                }
                else if (maskExpr != null && ls.Mask is "Ellipse" or "Rectangle" && !ls.MaskInvert && !ls.MaskMoves && !ls.MaskFollows)
                    chain.Add($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{maskExpr}'");

                // opacity before rotation and zoom (geq must see frames of one size)
                AddOpacity(chain, kf, Math.Clamp(ls.Opacity, 0, 1));

                // rotation turns the picture inside a square canvas big enough for any angle
                double canvas = 1.0, canvasAspect = aspect;
                if (rotates)
                {
                    chain.Add($"rotate=a='({Kf(kf, k => k.Rotation, "t")})*PI/180':c=none:ow='hypot(iw,ih)':oh='hypot(iw,ih)'");
                    canvas = Math.Sqrt(sw * (double)sw + sh * (double)sh) / sw;
                    canvasAspect = 1.0;
                }
                string z = kf.Count > 0 ? Kf(kf, k => Math.Max(0.05, k.Zoom), "t") : "1";
                string wExpr = $"max(2,trunc({N(w0 * canvas)}*({z})/2)*2)";
                string hExpr = $"max(2,trunc({N(w0 * canvas / canvasAspect)}*({z})/2)*2)";
                chain.Add(kf.Count > 1
                    ? $"scale=w='{wExpr}':h='{hExpr}':eval=frame"
                    : $"scale=w='{wExpr}':h='{hExpr}'");
                chain.Add($"setpts=PTS+{N(s)}/TB");
                string lay = $"[l{li}]";
                g.Add($"{layerIn}{string.Join(",", chain)}{lay}");

                double sx = W / 1920.0;
                string tv = $"(t-{N(s)})";
                string xExpr = kf.Count > 0 ? $"{N(cx)}+({Kf(kf, k => k.X * sx, tv)})-w/2" : $"{N(cx)}-w/2";
                string yExpr = kf.Count > 0 ? $"{N(cy)}+({Kf(kf, k => k.Y * sx, tv)})-h/2" : $"{N(cy)}-h/2";
                string o = Next();
                string mode = BlendFilterMode(ls.BlendMode);
                if (mode == null)
                {
                    g.Add($"{cur}{lay}overlay=x='{xExpr}':y='{yExpr}':eval={(kf.Count > 1 ? "frame" : "init")}:eof_action=pass:" +
                          $"enable='between(t,{N(s)},{N(e)})'{o}");
                }
                else
                {
                    // v2.0: blend modes — the layer is first put on a full-size canvas of the mode's
                    // neutral colour (black for screen/add/lighten/difference, white for multiply/darken,
                    // grey for overlay/soft light), then blended with the picture while it lasts
                    string neutral = mode is "multiply" or "darken" ? "white" : mode is "overlay" or "softlight" ? "0x808080" : "black";
                    string cv = $"[cv{li}]", lc = $"[lc{li}]", b1 = $"[bb{li}]", b2 = $"[bl{li}]";
                    g.Add($"color=c={neutral}:s={W}x{H}:r={fps}:d={N(Math.Max(0.1, baseDur))},format=rgba{cv}");
                    g.Add($"{cv}{lay}overlay=x='{xExpr}':y='{yExpr}':eval={(kf.Count > 1 ? "frame" : "init")}:eof_action=pass{lc}");
                    g.Add($"{cur}format=gbrp{b1}");
                    g.Add($"{lc}format=gbrp{b2}");
                    g.Add($"{b1}{b2}blend=all_mode={mode}:shortest=0:repeatlast=1:enable='between(t,{N(s)},{N(e)})'{o}");
                }
                cur = o;

                if (hasAudio.TryGetValue(l, out bool ha) && ha)
                {
                    long ms = (long)Math.Round(s * 1000);
                    string al = $"[a{li}]";
                    g.Add($"[{idx}:a]asetpts=PTS-STARTPTS,aresample=48000,volume={N(Math.Clamp(l.Volume / 100.0, 0, 4))},adelay={ms}:all=1" +
                          (string.IsNullOrEmpty(l.TrackFx) ? "" : "," + l.TrackFx) + al);   // v2.0: track mixer
                    audioLabels.Add(al);
                }
                li++;
            }

            // 4. v2.0: titles, over everything, each only during its time
            foreach (var c in layers.Concat(mainClips).Where(c => HasTitle(c) && c.TitleFile != null).OrderBy(c => c.Start))
            {
                string o = Next();
                g.Add($"{cur}{string.Join(",", TitleFilters(c, W, H))}{o}");
                cur = o;
            }

            return audioLabels;
        }

        private static string FinishArgs(StringBuilder inputs, int inputIndex, string cur, List<string> g, List<string> audioLabels,
                                         bool baseHasAudio, double baseDur, string fps, string videoEncArgs, string output)
        {
            g.Add($"{cur}format=yuv420p[vout]");

            string audioMap;
            if (audioLabels.Count > 0)
            {
                string baseA;
                if (baseHasAudio) baseA = "[0:a]";
                else
                {
                    int ai = inputIndex++;
                    inputs.Append($"-f lavfi -t {N(Math.Max(0.1, baseDur))} -i anullsrc=r=48000:cl=stereo ");
                    baseA = $"[{ai}:a]";
                }
                g.Add($"{baseA}aresample=48000[abase]");
                g.Add($"[abase]{string.Join("", audioLabels)}amix=inputs={audioLabels.Count + 1}:duration=first:normalize=0:dropout_transition=0[aout]");
                audioMap = "-map \"[aout]\" -c:a aac -b:a 256k -ar 48000";
            }
            else
            {
                audioMap = baseHasAudio ? "-map 0:a -c:a copy" : "";
            }

            return inputs +
                   $"-filter_complex \"{string.Join(";", g)}\" -map \"[vout]\" {audioMap} " +
                   $"{videoEncArgs} -pix_fmt yuv420p -r {fps} -t {N(baseDur)} -movflags +faststart -y \"{output}\"";
        }

        /// <summary>
        /// v2.0 — the drawtext filter(s) for a clip's title: position, size, colour, the dark band
        /// behind it, and the animation in and out (fade, slide up, slide from the right, zoom).
        /// </summary>
        internal static List<string> TitleFilters(CompositeClip c, int W, int H)
        {
            var t = c.Title;
            double t0 = c.Start + Math.Max(0, t.StartIn);
            double t1 = t.Length > 0.05 ? Math.Min(c.Start + c.Duration, t0 + t.Length) : c.Start + c.Duration;
            if (t1 - t0 < 0.2) t1 = t0 + 0.2;
            double fd = Math.Min(0.5, (t1 - t0) / 3);
            string T0 = N(t0), T1 = N(t1), FD = N(fd);
            string anim = t.Animation ?? "Fade";
            // 0 → 1 over the first fade length (how far the entrance has come)
            string inP = $"min(1,max(0,(t-{T0})/{FD}))";
            string alpha = anim == "None" ? "1" : $"max(0,min(1,min((t-{T0})/{FD},({T1}-t)/{FD})))";

            double scale = H / 1080.0;
            double fs = Math.Clamp(t.SelectedFontSize > 0 ? t.SelectedFontSize : 48, 8, 400) * scale;
            string fsExpr = anim == "Zoom" ? $"{N(fs)}*(0.6+0.4*{inP})" : N(fs);
            double margin = Math.Round(0.05 * H);

            string pos = (t.Position ?? "").ToLowerInvariant();
            bool top = pos.Contains("top") || pos.Contains("gore") || pos.Contains("vrh");
            bool bottom = pos.Contains("bottom") || pos.Contains("dole") || pos.Contains("dno");
            bool lower = pos.Contains("lower") || pos.Contains("donja trećina") || pos.Contains("third");
            bool left = pos.Contains("left") || pos.Contains("lev");
            bool right = pos.Contains("right") || pos.Contains("desn");
            double subGap = string.IsNullOrEmpty(c.SubtitleFile) ? 0 : fs * 0.9;

            string x = left ? N(margin) : right ? $"w-text_w-{N(margin)}" : "(w-text_w)/2";
            string y = top ? N(margin)
                     : lower ? N(H * 0.70)
                     : bottom ? $"h-text_h-{N(margin + subGap)}"
                     : $"(h-text_h)/2-{N(subGap / 2)}";
            if (anim == "SlideUp") y = $"{y}+{N(0.06 * H)}*(1-{inP})";
            if (anim == "SlideLeft") x = $"{x}+{N(0.25 * W)}*(1-{inP})";

            string color = "0x" + ((t.Color ?? "#FFFFFF").TrimStart('#') is string hx && hx.Length >= 6 ? hx.Substring(0, 6) : "FFFFFF");
            string box = t.Box ? $":box=1:boxcolor=black@0.55:boxborderw={N(Math.Round(fs * 0.35))}" : "";
            string shadow = t.Box ? "" : $":borderw={N(Math.Max(1, Math.Round(fs / 16)))}:bordercolor=black@0.85:shadowx=2:shadowy=2:shadowcolor=black@0.6";
            string enable = $":enable='between(t,{T0},{T1})'";

            var list = new List<string>
            {
                $"drawtext={FontSpec}textfile='{EscapeFilterPath(c.TitleFile)}':fontsize='{fsExpr}':fontcolor={color}" +
                $":alpha='{alpha}':x='{x}':y='{y}'{box}{shadow}{enable}"
            };
            if (!string.IsNullOrEmpty(c.SubtitleFile))
            {
                double fs2 = fs * 0.55;
                // the second line sits under the first one (its top = first line's top + 1.3 × size)
                string y2 = top ? N(margin + fs * 1.3)
                          : lower ? N(H * 0.70 + fs * 1.3)
                          : bottom ? $"h-text_h-{N(margin)}"
                          : $"(h-{N(fs)})/2+{N(fs * 0.85)}";
                if (anim == "SlideUp") y2 = $"{y2}+{N(0.06 * H)}*(1-{inP})";
                string x2 = left ? N(margin) : right ? $"w-text_w-{N(margin)}" : "(w-text_w)/2";
                if (anim == "SlideLeft") x2 = $"{x2}+{N(0.25 * W)}*(1-{inP})";
                list.Add($"drawtext={FontSpec}textfile='{EscapeFilterPath(c.SubtitleFile)}':fontsize='{N(fs2)}':fontcolor={color}" +
                         $":alpha='{alpha}':x='{x2}':y='{y2}'{(t.Box ? $":box=1:boxcolor=black@0.55:boxborderw={N(Math.Round(fs2 * 0.35))}" : $":borderw={N(Math.Max(1, Math.Round(fs2 / 16)))}:bordercolor=black@0.85")}{enable}");
            }
            return list;
        }

        /// <summary>
        /// v2.0 — makes the mask pictures of the layers once (one frame each, at the layer's own
        /// size), so the drawing only merges them instead of computing the shape for every pixel
        /// of every frame. Returns the files made.
        /// </summary>
        internal static async Task<List<string>> PrepareMasksAsync(IEnumerable<CompositeClip> layers, Dictionary<CompositeClip, (int w, int h)> sizes,
                                                                   string dir, Func<string, CancellationToken, Task<bool>> run, CancellationToken ct)
        {
            // v2.0: the shapes are drawn here (PGM pictures), any shape, inside out, moving or following
            var files = new List<string>();
            foreach (var l in layers)
            {
                ct.ThrowIfCancellationRequested();
                var ls = l.Layer;
                if (!HasMask(ls)) continue;
                var (w, h) = sizes.TryGetValue(l, out var sz) && sz.w > 0 && sz.h > 0 ? sz : (1920, 1080);
                string file = Path.Combine(dir, $"mask_{Guid.NewGuid():N}.pgm");
                try
                {
                    bool moving = MaskMoves(l);
                    bool ok;
                    if (moving)
                    {
                        var path = MaskPath(l, w, h);
                        double aspect = path.Count > 0 && path[0].H > 0.5 ? path[0].W / path[0].H : (double)w / h;
                        ok = await Task.Run(() => MaskShapes.WriteBoxMask(ls, aspect, file), ct);
                    }
                    else ok = await Task.Run(() => MaskShapes.WriteFrameMask(ls, w, h, file), ct);
                    if (ok && File.Exists(file)) { l.MaskFile = file; l.MaskIsBox = moving; files.Add(file); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Error("Layer mask", ex); }
            }
            return files;
        }

        /// <summary>v2.0 — the layer has a usable mask shape.</summary>
        internal static bool HasMask(LayerSettings ls) =>
            ls != null && MaskShapes.IsShape(ls.Mask) && MaskShapes.UnitPolygon(ls.Mask, ls.MaskPoints) != null;

        /// <summary>v2.0 — the mask moves (from box to box, or with the followed object).</summary>
        internal static bool MaskMoves(CompositeClip l) =>
            l.Layer != null && (l.Layer.MaskMoves || (l.Layer.MaskFollows && l.Tracking?.Path != null && l.Tracking.Path.Count > 0));

        /// <summary>
        /// v2.0 — where the mask box is over time, in the layer's own pixels and time
        /// (t = 0 at the layer's start): centre X/Y, width W, height H.
        /// </summary>
        internal static List<(double T, double X, double Y, double W, double H)> MaskPath(CompositeClip l, int sw, int sh)
        {
            var ls = l.Layer;
            var list = new List<(double, double, double, double, double)>();
            if (ls.MaskFollows && l.Tracking?.Path != null && l.Tracking.Path.Count > 0)
            {
                double grow = 1 + Math.Clamp(l.Tracking.Padding, 0, 300) / 100.0;
                foreach (var p in ObjectTracker.Simplify(l.Tracking.Path))
                    list.Add((p.T - l.SourceOffset, p.X * sw, p.Y * sh, Math.Max(2, p.W * sw * grow), Math.Max(2, p.H * sh * grow)));
                return list;
            }
            list.Add((-l.Cut, ls.MaskX / 100 * sw, ls.MaskY / 100 * sh, Math.Max(2, ls.MaskW / 100 * sw), Math.Max(2, ls.MaskH / 100 * sh)));
            if (ls.MaskMoves)
                list.Add((Math.Max(0.05, l.Duration + l.Cut) - l.Cut, ls.MaskEndX / 100 * sw, ls.MaskEndY / 100 * sh,
                          Math.Max(2, ls.MaskEndW / 100 * sw), Math.Max(2, ls.MaskEndH / 100 * sh)));
            return list;
        }

        /// <summary>v2.0 — a value along a path as an FFmpeg expression of <paramref name="tv"/> (straight lines between points).</summary>
        internal static string Pw(List<(double T, double X, double Y, double W, double H)> path,
                                  Func<(double T, double X, double Y, double W, double H), double> value, string tv)
        {
            if (path == null || path.Count == 0) return "0";
            if (path.Count == 1) return N(value(path[0]));
            string e = N(value(path[^1]));
            for (int i = path.Count - 2; i >= 0; i--)
            {
                double t0 = path[i].T, t1 = path[i + 1].T, v0 = value(path[i]), v1 = value(path[i + 1]);
                double dt = Math.Max(0.001, t1 - t0);
                string seg = Math.Abs(v1 - v0) < 1e-6 ? N(v0) : $"{N(v0)}+({N(v1 - v0)})*({tv}-({N(t0)}))/{N(dt)}";
                e = $"if(lt({tv},{N(t1)}),{seg},{e})";
            }
            return $"if(lt({tv},{N(path[0].T)}),{N(value(path[0]))},{e})";
        }

        /// <summary>
        /// v2.0 — blurs, pixelates, covers or lights the followed object in a picture stream
        /// <paramref name="inLabel"/> (frameW × frameH). The clip's own picture sits in it at
        /// (ox, oy) with size picW × picH; <paramref name="timeOf"/> turns file time into the
        /// stream's time. Active between <paramref name="from"/> and <paramref name="to"/>.
        /// Returns the output label, or null when there is nothing to do.
        /// </summary>
        internal static string RegionGraph(List<string> g, string inLabel, string tag, TrackingData tr, double frameW, double frameH,
                                           double picW, double picH, double ox, double oy, Func<double, double> timeOf,
                                           string tv, double from, double to)
        {
            if (tr == null || !tr.HasEffect) return null;
            double grow = 1 + Math.Clamp(tr.Padding, 0, 300) / 100.0;
            var pts = ObjectTracker.Simplify(tr.Path);
            if (pts.Count == 0) return null;
            var path = pts.Select(p => (T: timeOf(p.T), X: ox + p.X * picW, Y: oy + p.Y * picH, W: p.W * picW * grow, H: p.H * picH * grow)).ToList();
            int bw = (int)Math.Min(frameW, Math.Max(8, Math.Ceiling(path.Max(p => p.W) / 2) * 2));
            int bh = (int)Math.Min(frameH, Math.Max(8, Math.Ceiling(path.Max(p => p.H) / 2) * 2));
            bw -= bw % 2; bh -= bh % 2;
            string x = $"clip({Pw(path, p => p.X, tv)}-{bw / 2},0,{N(Math.Max(0, frameW - bw))})";
            string y = $"clip({Pw(path, p => p.Y, tv)}-{bh / 2},0,{N(Math.Max(0, frameH - bh))})";
            string en = $"enable='between(t,{N(from)},{N(to)})'";
            double s = Math.Clamp(tr.Strength, 1, 100);
            string a = $"[ra{tag}]", b = $"[rb{tag}]", c = $"[rc{tag}]", o = $"[ro{tag}]";
            string crop = $"crop=w={bw}:h={bh}:x='{x}':y='{y}'";
            switch (tr.Effect)
            {
                case "Spotlight":
                {
                    string dk = $"[rd{tag}]";
                    double dim = 0.15 + 0.5 * s / 100;
                    g.Add($"{inLabel}split{a}{b}");
                    g.Add($"{a}eq=brightness={N(-dim)}:saturation={N(1 - dim)}:{en}{dk}");
                    g.Add($"{b}{crop}{c}");
                    g.Add($"{dk}{c}overlay=x='{x}':y='{y}':eval=frame:{en}{o}");
                    return o;
                }
                case "Pixelate":
                {
                    int px = Math.Max(4, (int)Math.Round(Math.Min(bw, bh) * (0.04 + 0.21 * s / 100)));
                    g.Add($"{inLabel}split{a}{b}");
                    g.Add($"{b}{crop},pixelize=width={px}:height={px}{c}");
                    break;
                }
                case "Black":
                    g.Add($"{inLabel}split{a}{b}");
                    g.Add($"{b}{crop},drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill{c}");
                    break;
                default:   // Blur
                {
                    double sigma = Math.Max(2, Math.Min(bw, bh) * (0.03 + 0.17 * s / 100));
                    g.Add($"{inLabel}split{a}{b}");
                    g.Add($"{b}{crop},gblur=sigma={N(sigma)}:steps=2{c}");
                    break;
                }
            }
            g.Add($"{a}{c}overlay=x='{x}':y='{y}':eval=frame:{en}{o}");
            return o;
        }

        /// <summary>v2.0 — FFmpeg's name for a blend mode, or null for a normal overlay.</summary>
        internal static string BlendFilterMode(string mode) => mode switch
        {
            "Screen" => "screen", "Multiply" => "multiply", "Overlay" => "overlay", "SoftLight" => "softlight",
            "Lighten" => "lighten", "Darken" => "darken", "Add" => "addition", "Difference" => "difference",
            _ => null
        };

        /// <summary>v2.0 — the mask as an alpha expression (0 … 1) for geq, or null for none.</summary>
        internal static string MaskAlpha(LayerSettings ls)
        {
            double f = Math.Clamp(ls.MaskFeather, 0, 50) / 100.0;
            string F = N(Math.Max(0.005, f));
            return ls.Mask switch
            {
                // 1 at the centre, 0 on the ellipse edge; the soft band is f wide inside the edge
                "Ellipse" => $"clip((1-hypot((X-W/2)/(W/2),(Y-H/2)/(H/2)))/{F},0,1)",
                "Rectangle" => $"clip(min(min(X,W-X)/(W*{F}),min(Y,H-Y)/(H*{F})),0,1)",
                _ => null
            };
        }

        /// <summary>Opacity: a constant, or an animated alpha when the keyframes change it.</summary>
        private static void AddOpacity(List<string> chain, List<CompositeKeyframe> kf, double baseOpacity)
        {
            bool animated = kf.Count > 1 && kf.Select(k => Math.Round(k.Opacity, 3)).Distinct().Count() > 1;
            if (animated)
            {
                string o = Kf(kf, k => Math.Clamp(k.Opacity, 0, 1), "T");
                chain.Add($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{N(baseOpacity)}*({o})'");
            }
            else
            {
                double op = baseOpacity * (kf.Count > 0 ? Math.Clamp(kf[0].Opacity, 0, 1) : 1.0);
                if (op < 0.999) chain.Add($"colorchannelmixer=aa={N(op)}");
            }
        }

        /// <summary>
        /// A keyframed value as an FFmpeg expression of the time <paramref name="t"/>:
        /// straight lines between the keyframes, held before the first and after the last.
        /// </summary>
        internal static string Kf(List<CompositeKeyframe> kf, Func<CompositeKeyframe, double> value, string t)
        {
            if (kf == null || kf.Count == 0) return "0";
            if (kf.Count == 1) return N(value(kf[0]));
            string e = N(value(kf[kf.Count - 1]));
            for (int i = kf.Count - 2; i >= 0; i--)
            {
                double t0 = kf[i].Time, t1 = kf[i + 1].Time;
                double v0 = value(kf[i]), v1 = value(kf[i + 1]);
                double dt = Math.Max(0.001, t1 - t0);
                // v2.0: keyframe curves — u goes 0 → 1 between the two keyframes, eased as chosen
                string u = $"(({t})-({N(t0)}))/{N(dt)}";
                string eased = (kf[i].Easing ?? "Linear") switch
                {
                    "EaseIn" => $"pow({u},2)",
                    "EaseOut" => $"(1-pow(1-{u},2))",
                    "EaseInOut" => $"(pow({u},2)*(3-2*{u}))",
                    _ => u
                };
                string seg = Math.Abs(v1 - v0) < 1e-9 || kf[i].Easing == "Hold"
                    ? N(v0)
                    : $"{N(v0)}+({N(v1 - v0)})*{eased}";
                e = $"if(lt({t},{N(t1)}),{seg},{e})";
            }
            return $"if(lt({t},{N(kf[0].Time)}),{N(value(kf[0]))},{e})";
        }

        internal static string EscapeFilterPath(string path)
            => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

        // ── ffprobe ──────────────────────────────────────────────────────────────────

        private async Task<string> ProbeAsync(string args, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_ffprobe) || !File.Exists(_ffprobe)) return "";
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo(_ffprobe, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    }
                };
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct);
                await errTask;
                return (await outTask).Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        internal async Task<(int, int)> ProbeSizeAsync(string path, CancellationToken ct)
        {
            string o = await ProbeAsync($"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0:s=x \"{path}\"", ct);
            var parts = o.Split('\n')[0].Trim().Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) ? (w, h) : (0, 0);
        }

        private async Task<bool> ProbeHasAudioAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_ffprobe) || !File.Exists(_ffprobe)) return true;
            string o = await ProbeAsync($"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"", ct);
            return !string.IsNullOrWhiteSpace(o);
        }

        private async Task<double> ProbeDurationAsync(string path, CancellationToken ct)
        {
            string o = await ProbeAsync($"-v error -show_entries format=duration -of csv=p=0 \"{path}\"", ct);
            return double.TryParse(o.Split('\n')[0].Trim(), NumberStyles.Float, Inv, out double d) ? d : 0;
        }

        /// <summary>
        /// The frame rate of a video file as FFmpeg writes it ("30000/1001", "25", "60"), or
        /// null when it can't be read or is not a sensible output rate.
        /// </summary>
        public async Task<string> ProbeFrameRateAsync(string path, CancellationToken ct)
        {
            string o = await ProbeAsync($"-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate -of csv=p=0 \"{path}\"", ct);
            foreach (var cand in o.Replace("\n", ",").Split(',').Select(x => x.Trim()))
            {
                double v = ParseRate(cand);
                if (v >= 10 && v <= 120) return NormalizeRate(cand, v);
            }
            return null;
        }

        internal static double ParseRate(string r)
        {
            if (string.IsNullOrWhiteSpace(r)) return 0;
            var p = r.Split('/');
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out double a) &&
                double.TryParse(p[1], NumberStyles.Float, Inv, out double b) && b > 0) return a / b;
            return double.TryParse(r, NumberStyles.Float, Inv, out double v) ? v : 0;
        }

        /// <summary>23.976, 29.97 and 59.94 are kept exact; anything else is rounded to a whole number.</summary>
        internal static string NormalizeRate(string raw, double v)
        {
            foreach (var (n, d) in new[] { (24000, 1001), (30000, 1001), (60000, 1001) })
                if (Math.Abs(v - (double)n / d) < 0.01) return $"{n}/{d}";
            return Math.Round(v).ToString(Inv);
        }
    }
}
