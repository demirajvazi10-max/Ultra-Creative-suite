# Ultra Video Editor

> An accessible video editor for Windows, with a built-in AI music video creator. Fully usable with JAWS and NVDA, and equally usable for sighted and low-vision users.

Part of [Ultra Creative Suite](../README.md). Built by a blind developer and tested daily with JAWS for Windows.

---

## What is this?

Ultra Video Editor is a WPF desktop application (.NET 8) with two sides:

- **A timeline video editor.** You can add video, images and audio, cut, arrange, add effects, transitions, keyframe animations and subtitles, and export, all from the keyboard with full screen reader feedback.
- **An AI music video creator.** It takes an audio file and song lyrics and produces a finished music video. A local LLM (Ollama/Qwen) analyzes the lyrics, matching stock footage is downloaded from Pixabay, cuts are synced to the music, and FFmpeg renders the result with color grading, transitions and ambient sound.

---

## Download and Install

Download the latest installer (`UltraVideoEditorSetup-vX.Y.Z.exe`) from the [Releases page](https://github.com/demirajvazi10-max/Ultra-Creative-suite/releases) and run it. Windows will ask for administrator permission; this is expected.

The installer:

- Lets you choose the language at the start (English, German or Serbian). This also becomes the app's default language.
- Automatically downloads and sets up **FFmpeg** (video processing), **VLC** (preview) and **Ollama** with AI models chosen for your hardware (GPU and RAM are detected automatically).
- Optionally installs AI transcription and subtitles (**Faster-Whisper-XXL**, about 1.3 GB). This is left as a checkbox because of its size.
- Starts the app when it finishes.

The ambient sound/SFX library is **not** included in the installer. If you want it, unpack it into the `Assets\` folder next to the `.exe`.

The app checks for new versions itself (**Help → Updates**) and can download and start the new installer for you.

---

## Features

### Editing
- Multiple video and audio tracks, with filtering by track type
- Cut, copy, paste, move clips left/right/up/down, set clip duration and volume
- Markers, including "cut at all markers"
- Video and audio effects, transitions between clips, keyframe animations (zoom, rotation, position)
- Text overlays and synchronized subtitles
- Project templates, batch export, and export for Shorts/TikTok/Instagram (9:16 and square)
- GPU-accelerated encoding (NVIDIA NVENC) with automatic fallback to `libx264`

### Accessibility
- Three display modes: **Sighted**, **Accessibility (screen reader)** and **Low Vision**
- Dark, light and high-contrast themes, plus adjustable text and control size
- Every action is available from the keyboard; menus show their shortcuts
- Status changes, progress and errors are announced to JAWS/NVDA through live regions, so you never need to open the log window to know what happened
- AI-generated audio descriptions of clips and an exportable accessibility report

### Import from YouTube (Timeline → Import from YouTube)
- Video (MP4, choice of quality) or audio (MP3, choice of bitrate)
- **Playlist links are never downloaded in full by accident.** When a link is part of a playlist you choose:
  - **Just this video**: the default when the link points to a specific video. Only that video is downloaded.
  - **Entire playlist**: private or deleted videos are skipped, and the rest is still downloaded.
  - **Choose specific tracks**: use the arrow keys to move and Space to select. Each selection is announced ("3 of 15 tracks selected"), and **Select all** / **Clear selection** buttons are available.
- Downloaded files can be added straight to the timeline
- Progress is announced at 25/50/75% and per track ("Track 2 of 5"); **Cancel** really stops the download
- Uses [yt-dlp](https://github.com/yt-dlp/yt-dlp). If it is missing, the app offers to download it, and keeps its own copy up to date weekly. If YouTube requires the Deno JavaScript helper, the app offers to download it and retries.

### AI tools
- AI music video creator (Video Engine, AUTO)
- AI video review, clip descriptions (vision AI), smart scene detection, AI highlight engine
- Voice generation, audio transcription, lyric sync
- Timeline AI assistant and color grading engine

### Languages
- English, German and Serbian. Change the language anytime from **View → Language**.

---

## What's New in 1.0.3

- YouTube import: "Just this video" and "Choose specific tracks" now download exactly what you chose (previously a whole playlist could be downloaded). Cancel now works, only files from the current download are added to the timeline, and screen reader announcements are audible.
- Fixed possible freezes during transcription, beat detection, 9:16/square export and other FFmpeg operations on longer videos. Cancelling these now stops FFmpeg/Whisper too.
- Update download no longer times out on slower connections.
- Correct version number in the title bar and About dialog.
- Complete German translation of the YouTube dialog and update messages.

---

## Requirements

| Component | Note |
|---|---|
| Windows | 10 / 11, 64-bit |
| RAM | 8 GB minimum, 16 GB+ recommended |
| GPU | NVIDIA GPU optional (NVENC encoding, faster AI) |
| FFmpeg, VLC, Ollama + models | Installed automatically by the installer |
| Whisper (faster-whisper-xxl) | Optional, installed by the installer on request |
| Pixabay API key | Free, needed for the AI music video creator ([pixabay.com/api/docs](https://pixabay.com/api/docs/)). Enter it under **Tools → Media Providers (API keys)**. |
| yt-dlp | Only for YouTube import; downloaded by the app on first use |

---

## Building from Source

This project lives in the Ultra Creative Suite monorepo:

```bash
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/UltraVideoEditor
dotnet restore
dotnet build -c Release
```

You can also open `UltraVideoEditor.csproj` in Visual Studio 2022 and build there.

For a local build, place the external tools next to the built `.exe` (the installer does this for end users):

```
UltraVideoEditor/
├── Ffmpeg/
│   ├── ffmpeg.exe
│   └── ffprobe.exe
├── Whisper/
│   └── faster-whisper-xxl.exe   # or whisper.exe
└── ...
```

Then start Ollama and pull the models, for example:
```bash
ollama pull qwen2.5:7b
ollama pull qwen2.5vl:7b
```

NuGet packages:
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

### Releases

Releases are built by GitHub Actions (`.github/workflows/build-release-video.yml`) when a tag in the form `video-vX.Y.Z` is pushed. The version from the tag is written into the app automatically, so the version shown in the app always matches the release.

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

## How the AI Music Video Creator Works

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
| `MainWindow` | Timeline editor, menus, keyboard shortcuts, screen reader announcements |
| `AIVideoCreator` | Main orchestrator of the AI music video creator: scene loop, query pipeline, media selection |
| `StrictQueryEngine` | SR/BA/HR → EN keyword map, Ollama prompt builder, ClassifyLyric/Sentiment |
| `BeatDetection` | Audio analysis, beat timestamps, piano mode phrase detection |
| `VisionAnalyzer` | Qwen2-VL / ONNX shot analysis, score, labels, smile |
| `MotionAnalyzer` | FFmpeg optical flow, direction matching |
| `RenderEngine` | FFmpeg filter_complex build, xfade, color grading, denoise |
| `SkiaAnimationEngine` | Skia-based text overlay and title animations |
| `CinematicProcessor` | Ken Burns, zoom/pan effects, Shorts/square export |
| `LocalSoundLibrary` | 1279+ ambient sounds, categorization and matching |
| `AITranscription` | Whisper transcription and lyric alignment |
| `YouTubeDownloadDialog` | Accessible YouTube import (yt-dlp), playlist scope and track selection |
| `UpdateChecker` | Checks GitHub Releases for new `video-v*` versions |
| `LanguageManager` | All UI text in English, German and Serbian |
| `ProcessExtensions` | Safe handling of external processes (FFmpeg, Whisper): no stalls on full output buffers, killed on cancel |

---

## Configuration

Everything is configured directly in code. Key parameters:

```csharp
// AIVideoCreator.xaml.cs
const int QUERY_COOLDOWN_SCENES = 4;     // Anti-repetition of themes

// BeatDetection.cs
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

## Stability & Diagnostics

The app has a global handler for unexpected errors (`App.xaml.cs`), at three levels:

- `DispatcherUnhandledException`: errors on the main (UI) thread. The user sees a message and the app **keeps running** instead of silently closing. This matters especially for screen reader users, for whom a silent close gives no signal at all that something happened.
- `AppDomain.CurrentDomain.UnhandledException`: more serious errors outside the main thread.
- `TaskScheduler.UnobservedTaskException`: errors from "fire and forget" async tasks that nobody awaited, which would otherwise vanish without a trace.

External tools (FFmpeg, Whisper, yt-dlp) are always run with their output read, so long operations cannot stall on a full output buffer. Cancelling an operation also stops the external process and its child processes.

`RenderEngine.cs` additionally logs rare, non-critical errors during processing, silently and without interrupting the render. These include filter string building, the post-processing file swap, temp folder cleanup and FFmpeg duration parsing. The log is written to:

```
%APPDATA%\UltraVideoEditor\render_errors.log
```

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

- **Pixabay pool**: For long songs (3+ minutes), the deduplication pool can run dry for recurring query themes. The system uses a cooldown variant as mitigation.
- **Beat detection on piano**: Piano mode relies on energy flux detection. For solo piano without accompaniment, it may generate fewer phrase boundaries than optimal.
- **GPU encoder**: Uses `h264_nvenc` (NVIDIA). On systems without an NVIDIA GPU, it automatically falls back to `libx264`.
- **YouTube import**: YouTube changes often. The app updates its own copy of yt-dlp weekly, but a download can occasionally fail until yt-dlp publishes a fix.

---

## Development

The project is active. Planned features:

- Contrast boost for children's playground scenes (`eq=contrast` when `HasChildren=true`)
- Preview renderer (30s test render before the full render)
- Support for more stock API providers (Pexels, Unsplash video)

---

## Author

Created by **Demir Ajvazi**.

## License

Licensed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE).
