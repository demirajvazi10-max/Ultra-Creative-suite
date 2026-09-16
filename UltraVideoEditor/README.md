# UltraVideoEditor — AI-Powered Music Video Creator

> Automatic music video generator for children's songs, with deep integration of AI lyric analysis, beat detection, and semantic shot matching.

---

## What is this?

UltraVideoEditor is a WPF desktop application (.NET 8) that takes an audio file and song lyrics and returns a finished music video. The system uses a local LLM (Ollama/Qwen), computer vision (Qwen2-VL + ONNX MobileNet), Whisper transcription, and an FFmpeg render pipeline to automatically:

- Analyze lyrics and assign semantic, emotional, and seasonal context to each shot
- Fetch relevant stock video clips from the Pixabay API
- Sync cuts to musical phrases (beat detection + piano mode)
- Render the final video with color grading, cross-dissolve transitions, and ambient sounds

---

## System Architecture

```
Audio file + Lyrics
        │
        ▼
┌─────────────────────┐
│   AITranscription   │  Whisper → timestamped lyrics
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   BeatDetection     │  FFmpeg RMS energy → beat timestamps
│   + Piano Mode      │  Spectral flux → melodic phrases
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  AIVideoCreator     │  Main orchestrator
│  ┌───────────────┐  │
│  │ StrictQuery   │  │  LAYER 1: Ollama/Qwen → semantic query
│  │ Engine        │  │  LAYER 2: _actionMap (552+ SR/BA/HR → EN)
│  │               │  │  LAYER 3: SmartFallback
│  └───────────────┘  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Pixabay API        │  Stock video search + deduplication
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  VisionAnalyzer     │  Qwen2-VL / ONNX → score, HasChildren,
│                     │  HasSmile, IsOutdoor, season, motion
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  MotionAnalyzer     │  FFmpeg optical flow → direction matching
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  RenderEngine       │  FFmpeg filter_complex → final video
│                     │  xfade + color grading + denoise
└─────────────────────┘
```

---

## Requirements

### Runtime
| Component | Version | Note |
|---|---|---|
| Windows | 10 / 11 | WPF application |
| .NET | 8.0 | `net8.0-windows` |
| FFmpeg | 6.0+ | Must be in the `Ffmpeg/` folder next to the exe |
| Ollama | Any | Local LLM server |
| Qwen2.5 14B | Via Ollama | `ollama pull qwen2.5:14b` |
| Qwen2-VL | Via Ollama | `ollama pull qwen2.5vl:latest` |
| Whisper | whisper.exe / faster-whisper-xxl.exe | In the `Whisper/` folder |

### API Keys
- **Pixabay** — free API key from [pixabay.com/api/docs](https://pixabay.com/api/docs/)

### NuGet Packages
```xml
<PackageReference Include="LibVLCSharp" Version="3.9.6" />
<PackageReference Include="LibVLCSharp.WPF" Version="3.9.6" />
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="14.16.0" />
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.19.0" />
<PackageReference Include="Microsoft.Windows.Compatibility" Version="10.0.6" />
<PackageReference Include="NAudio" Version="2.3.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
<PackageReference Include="PixabaySharp" Version="1.1.0" />
<PackageReference Include="SkiaSharp" Version="2.88.6" />
<PackageReference Include="SkiaSharp.Views.WPF" Version="2.88.6" />
<PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.23" />
```

---

## Installation

```bash
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/UltraVideoEditor
dotnet restore
dotnet build -c Release
```

Place the external tools:
```
UltraVideoEditor/
├── Ffmpeg/
│   └── ffmpeg.exe
├── Whisper/
│   └── faster-whisper-xxl.exe   # or whisper.exe
└── ...
```

Start Ollama and pull the models:
```bash
ollama pull qwen2.5:14b
ollama pull qwen2.5vl:latest
```

### Hardware Tested On

- **CPU:** Intel Core i9-10885H
- **GPU:** NVIDIA RTX 2060 Max-Q (NVENC)
- **RAM:** 32GB DDR4 3200MHz
- **Output:** 4K H.264 via NVENC

---

## How It Works

### 1. Query Pipeline (3 layers)

Every lyric line passes through three layers to produce a Pixabay search query:

**Layer 1 — Ollama/Qwen (primary)**
Qwen receives the lyric line + `LyricTagType` (Action/Atmospheric/Object/Narrative) + `SentimentPolarity` (Positive/Negative/Neutral) + a `needsCloseUp` flag, and generates a 3-5 word English search query.

**Layer 2 — `_actionMap` (552+ entries)**
Direct matching of Serbian/Bosnian/Croatian keywords to an English visual query. Priority scoring favors concrete objects over abstract states — "ice cream" (score 50) always wins over "flies" (score 14) within the same line.

**Layer 3 — SmartFallback**
Contextual fallback based on the detected season and mood — never returns null, never a black screen.

### 2. Beat Detection + Piano Mode

For standard music with drums: RMS energy spikes → beat timestamps → cuts on downbeats.

For piano/melodic music (low confidence or uneven beats):
- **Phrase detection**: smoothed energy profile → spectral flux → melodic phrase boundaries
- **Dynamic pacing**: `NoteDensity` (0-1) mapped to shot duration — quiet phrase → 4.5s, dense passage → 1.8s
- **VibeScore modifier**: high-energy scenes get a 25% shorter shot

Log: `🎹 Piano mode active: 12 melodic phrases, density=0.43 → 3.3s average`

### 3. Vision Analysis

Every downloaded clip is analyzed by **Qwen2-VL** (if available) or **ONNX MobileNetV2** (fallback):

- `Score` 1-10 (overall visual quality)
- `HasChildren`, `HasFaces`, `HasSmile` — presence of children and emotion
- `IsOutdoor`, `IsWarm` — setting and color temperature
- `RetryNeeded` — Qwen flags that the clip doesn't match the lyric's context

**Smile bonus**: if `HasSmile=true` and the lyric is `Positive` sentiment → VisionScore +1.5

### 4. Seasonal Color Grading (per shot)

Every shot gets an FFmpeg `curves` + `eq` filter based on the season of **that specific lyric line**, not the song's global season:

| Season | Effect |
|---|---|
| `winter` | Blue tones, reduced R, boosted B, -12% desaturation |
| `summer` | Golden tones, boosted R/G, reduced B, +18% saturation |
| `spring` | Fresh greenish tint, slightly boosted G |
| `autumn` | Warm orange, boosted R, reduced B |

### 5. Shot Composition

The system tracks the sequence of shots and avoids:
- Two consecutive `wide` shots without children (PATCH9 30% Rule)
- Two consecutive `medium` shots (shot composition filter)
- An extreme jump from `wide → close` without a bridge shot

### 6. Motion Matching

`MotionAnalyzer` analyzes the optical flow of the first and **last** frame of every clip. The next clip must have a compatible motion direction — eliminating jump-cut problems.

### 7. Query Cooldown

The same visual theme (first 2 keywords of a query) may not repeat within 4 consecutive scenes (~12-16s). If a repeat is detected, the query gets a seasonal variant instead.

---

## Key Classes

| Class | Responsibility |
|---|---|
| `AIVideoCreator` | Main orchestrator — scene loop, query pipeline, media selection |
| `StrictQueryEngine` | SR/BA/HR → EN keyword map, Ollama prompt builder, ClassifyLyric/Sentiment |
| `BeatDetection` | Audio analysis, beat timestamps, piano mode phrase detection |
| `VisionAnalyzer` | Qwen2-VL / ONNX shot analysis, score, labels, smile |
| `MotionAnalyzer` | FFmpeg optical flow, direction matching |
| `RenderEngine` | FFmpeg filter_complex build, xfade, color grading, denoise |
| `SkiaAnimationEngine` | Skia-based text overlay and title animations |
| `CinematicProcessor` | Ken Burns, zoom/pan effects |
| `LocalSoundLibrary` | 1279+ ambient sounds, categorization and matching |

---

## Import from YouTube

A built-in dialog (`YouTubeDownloadDialog`) imports video or audio directly from a YouTube link via `yt-dlp`, fully accessible for JAWS/NVDA:

- **Video (MP4)** or **Audio (MP3)**, with quality selection. MP3 extraction and video/audio merging use FFmpeg explicitly located next to the app (`--ffmpeg-location`), so it works reliably even when FFmpeg isn't on the system `PATH`.
- **Playlist scope choice**: pasting a link that's part of a playlist (YouTube adds `&list=...` to a lot of links) never silently downloads the whole playlist. You get an explicit choice — just this video, the entire playlist, or hand-picked tracks from a list — before anything downloads.
- **Self-installing yt-dlp**: if `yt-dlp` isn't found, the dialog offers to download it automatically.
- Downloaded files can be added straight to the timeline, or just saved to a chosen folder.

## Language

The UI is available in **English, Serbian, and German**, switchable at runtime (`LanguageManager`). All dialogs, including the YouTube import dialog above, follow the app's current language setting.

## Updates

**Help → Check for Updates** queries the GitHub Releases of this repository for the latest `video-v*` tag, compares it to the running version, and — if a newer one is found — offers to download and run the installer directly.

---

## Configuration

Everything is configured directly in code. Key parameters:

```csharp
// BeatDetection.cs
const int QUERY_COOLDOWN_SCENES = 4;     // Anti-repetition of themes
double baseDuration = 4.5 - NoteDensity * 2.7; // Piano pacing: 1.8-4.5s

// RenderEngine.cs
// Pacing-aware fade durations:
"fast"     => 0.25s
"standard" => 0.50s
"slow"     => 0.70s

// hqdn3d=1.5:1.5:6:6  — denoise
// unsharp=3:3:0.4      — sharpening
```

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Arrow Up/Down | Navigate timeline clips |
| Page Up/Down | Jump 5 clips |
| Ctrl+Space | Play / Pause |
| Ctrl+R / F5 | Render video |
| Ctrl+O | Open media files |
| Ctrl+S | Save project |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+C / V / X | Copy / Paste / Cut clip |
| Delete | Remove selected clip |
| Ctrl+K | Add keyframe |
| Ctrl+M | Add marker |
| Ctrl+D | Set clip duration |
| Ctrl+Alt+V | Set clip volume |
| Ctrl+Shift+A | Toggle accessibility mode |
| F6 | Read selected clip description aloud |
| Menu key / Right-click | Context menu on timeline |

---

## Stability & Diagnostics

The app has a global handler for unexpected errors (`App.xaml.cs`), at three levels:

- `DispatcherUnhandledException` — errors on the main (UI) thread. The user sees a message, and the app **keeps running** instead of silently closing. This matters especially for screen reader users, for whom a silent close gives no signal at all that something happened.
- `AppDomain.CurrentDomain.UnhandledException` — more serious errors outside the main thread.
- `TaskScheduler.UnobservedTaskException` — errors from "fire and forget" async tasks nobody awaited, which would otherwise vanish without a trace.

`RenderEngine.cs` additionally logs (silently, without interrupting the render) rare, non-critical errors during processing — filter string building, post-processing file swap, temp folder cleanup, FFmpeg duration parsing — to:

```
%APPDATA%\UltraVideoEditor\render_errors.log
```

This doesn't change the behavior of these steps (non-critical errors are still skipped on purpose, so they don't interrupt the render) — it just leaves a trail for diagnostics if something starts happening repeatedly.

---

## Log Messages (reference)

```
🥁 Beat detection: 120 BPM, 148 beats, confidence=0.72
🎹 Piano mode active: 12 melodic phrases, density=0.43 → 3.3s average
🗓 Season: global=spring, per-line=winter → ✅ Season changed to: winter
🏷 LyricTag: Action | Sentiment: Positive | CloseUp: True
🤖 Ollama query: 'child running park joy sunlight'
😊 Smile bonus +1.5 (sentiment=Positive): 6.0 → 7.5
🎬 Shot composition: two consecutive 'medium' — looking for another type...
📐 PATCH9 30% Rule: wide shot without children — looking for medium/close shot...
🔄 Cooldown variant (theme 'children stream' was scene 3): '...'
✅ Score 7.5/10 [Qwen] | Motion:Right | Shot:medium | Season:winter | Children:True Smile:True
✨ Cross-dissolve: 45 clips, avg fade 0.50s (pacing-aware)
```

---

## Known Limitations

- **Pixabay pool**: For long songs (3+ minutes), the deduplication pool can run dry for recurring query themes. The system has a cooldown variant as mitigation.
- **Beat detection on piano**: Piano mode relies on energy flux detection — for solo piano without accompaniment it may generate fewer phrase boundaries than optimal.
- **Magick.NET**: kept on the latest available patched version (currently 14.16.0) to stay ahead of security advisories for the underlying ImageMagick C library.
- **GPU encoder**: Uses `h264_nvenc` (NVIDIA). On systems without an NVIDIA GPU, it automatically falls back to `libx264`.

---

## Development

The project is active. Planned features:

- Contrast boost for children's playground scenes (`eq=contrast` when `HasChildren=true`)
- Preview renderer (30s test render before the full render)
- Support for more stock API providers (Pexels, Unsplash video)

---

## Project Structure

```
UltraVideoEditor/
├── AIVideoCreator.xaml.cs        # AI video generation orchestrator
├── AIHighlightEngine.cs          # Highlight extraction, arc scoring
├── AITranscription.cs            # faster-whisper integration, word alignment
├── BeatDetection.cs              # RMS energy + piano mode phrase detection
├── StrictQueryEngine.cs          # B/H/S → EN keyword map, Ollama prompt builder
├── IskraKidsSafeQuery.cs         # 3-layer kids-safety filter
├── VisionAnalyzer.cs             # Qwen2-VL / ONNX frame analysis
├── MotionAnalyzer.cs             # Optical flow, direction matching
├── ColorGradingEngine.cs         # AI auto color grade, 10 presets
├── TransitionEngine.cs           # Beat-synced xfade selection
├── SmartAudioMixer.cs            # Sidechain ducking, LUFS normalization
├── SmartSceneDetector.cs         # FFmpeg-based scene detection (Phase 4A)
├── TimelineAIAssistant.cs        # Natural language timeline commands (Phase 4B)
├── AccessibilityReportGenerator.cs # Audio-description script, nav markers
├── ExportPipeline.cs             # Multi-format simultaneous export
├── BatchExportEngine.cs          # Multi-project render queue
├── RenderEngine.cs               # FFmpeg filter_complex, NVENC, GPU/CPU
├── CinematicProcessor.cs         # Ken Burns, SmartCrop, audio ducking
├── SkiaAnimationEngine.cs        # Animated text overlays (SkiaSharp)
├── LocalSoundLibrary.cs          # 1,279+ ambient sounds, semantic matching
├── MediaProviders.cs             # Waterfall provider system (Pixabay/Pexels/Coverr)
├── FoundryIQClient.cs            # Azure AI Foundry Layer 0 client
├── OllamaClient.cs               # Local AI inference client
├── HardwareEncoderDetector.cs    # NVENC auto-detection
├── MainWindow.xaml.cs            # Main UI, Win32 ListView, accessibility
├── Models.cs                     # Data models (TimelineItem, SubtitleItem, etc.)
├── NativeListViewBridge.cs       # Win32 interop for accessible timeline
├── UpdateChecker.cs              # GitHub Releases version check for Check for Updates
├── YouTubeDownloadDialog.xaml.cs # Import from YouTube (yt-dlp)
└── Ffmpeg/
    └── ffmpeg.exe                # (not included — download separately)
```

---

## Author

Created by **Demir Ajvazi**.

## License

GPL-3.0 — see the repository's [LICENSE](../LICENSE) file. This tool shares its license with the rest of the Ultra Creative Suite repository.
