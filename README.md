# Ultra Creative Suite

**The world's first professional video editor that is fully accessible to blind, low-vision, and sighted users — without compromise.**

Built by a blind developer. Tested daily with JAWS for Windows.

---

## Demo

This video was created entirely by the author — who is blind — using Ultra Creative Suite with JAWS for Windows. No sighted assistance.

[![Ultra Creative Suite Demo](https://img.youtube.com/vi/K1mXPN4hEFs/maxresdefault.jpg)](https://www.youtube.com/watch?v=K1mXPN4hEFs)

> A children's song video: lyrics analyzed by AI, stock footage automatically selected and downloaded, mood-based color grading applied, ambient sounds mixed, rendered to 4K. Created independently by a blind user.

---

## What is this?

Ultra Creative Suite is a professional video editor for Windows, built from the ground up with full accessibility as a core requirement — not an afterthought.

Every feature works for blind, low-vision, and sighted users equally. Blind users can independently create professional-quality videos, edit timelines, apply AI effects, transcribe audio, and render 4K output — without sighted assistance, and without a stripped-down "accessible mode" that hides features.

> "I am blind and I use JAWS for Windows. I built this because no professional video editor on the market is actually usable with a screen reader."
> — Author

---

## Repository Structure

This repository hosts the full Ultra Creative Suite as a monorepo. Every app lives in its own sibling subfolder — none of them is "the root project":

- **`/UltraVideoEditor`** — Ultra Video Editor, the flagship AI-assisted video editor
- **`/UltraAudioEditor`** — Ultra Audio Editor, a companion professional audio editor with the same accessibility standard
- **`/UltraStudio`** — Ultra Studio, an AI-assisted photo editor with the same accessibility standard: a dual JAWS Mode / Visual Mode interface, local AI image description and editing suggestions, and AI-guided precise object extraction (Segment Anything)
- **`/UltraCaptions`** — Ultra Captions, an accessible subtitle/caption editor combining Whisper auto-transcription with manual keyboard-driven timing
- **`/UltraCast`** — Ultra Cast, an accessible screen recorder for tutorials, demos, and walkthroughs
- **`/UltraRecord`** — Ultra Record, a multi-track audio recorder with audio-based clipping alerts instead of visual VU meters
- **`/UltraPlayer`** — Ultra Player, an accessible podcast/audiobook player with adjustable playback speed and sleep timer

Each app has its own README with full details.

All apps share this repository's single [GPL-3.0 license](./LICENSE).

---

## Current Status

**Fully functional.** Source code is provided as-is — the application works and is actively used in real-world production by the author.

**Want to test it?** Clone the repo, follow the installation steps below, and try it. If you find issues, open a GitHub issue.

---

## Key Accessibility Features

**Native Win32 ListView timeline** — Uses the same Windows control as File Explorer. JAWS and NVDA read every clip natively without plugins or workarounds: clip name, type, duration, position, and AI-generated audio description.

**Live region status bar** — Every action, render progress, error, and confirmation is announced automatically. No need to manually navigate to find what happened.

**Full keyboard control** — Every feature is reachable without a mouse. No drag-and-drop required for any core workflow.

**AI audio descriptions** — Every image and video clip on the timeline receives an AI-generated description that JAWS reads aloud, giving blind users full situational awareness of visual content.

**Accessibility report export** — The AI Highlight Engine generates a complete audio-description script (TXT) with per-segment descriptions, navigation markers, and a TTS-optimized summary — readable by any screen reader or TTS engine.

**Screen reader optimized dialogs** — All dialogs use proper focus management, labeled controls, and logical tab order.

---

## Technical Stack

| Component | Technology |
|---|---|
| Language | C# / .NET 8 |
| UI Framework | WPF (Windows Presentation Foundation) |
| Render Engine | FFmpeg with NVENC GPU acceleration |
| Audio | NAudio |
| Video Preview | LibVLC |
| AI Transcription | faster-whisper (xxl model, word-level timestamps) |
| AI Text / Story | Ollama — Qwen 2.5 14B (local inference) |
| AI Vision Analysis | Qwen2-VL (Ollama) + ONNX MobileNetV2 |
| AI Accessibility Hints | Microsoft Azure AI Foundry (optional Layer 0) |
| Beat Detection | FFmpeg RMS energy + Spectral flux (piano mode) |
| Image Generation | SkiaSharp animated text overlays |
| Stock Media | Pixabay API → Pexels → Coverr (waterfall fallback) |
| Color Grading | FFmpeg curves/eq, 10 AI-selected presets |
| Screen Reader | JAWS for Windows (primary), NVDA (compatible) |
| Platform | Windows 10/11 (64-bit) |

---

## Core Features

### AI Video Creator

Generates a complete music video from a single audio file and lyrics:

- **4-layer query pipeline**: Azure AI Foundry (Layer 0, accessibility hints) → Ollama/Qwen semantic query (Layer 1) → 552+ B/H/S→EN keyword map (Layer 2) → SmartFallback (Layer 3) — never returns null, never produces a black screen
- `LyricTagType` classification per line: Action / Atmospheric / Object / Narrative
- `SentimentPolarity` detection: Positive / Negative / Neutral — negative lyrics trigger dark, desaturated, rainy visuals
- **IskraKidsSafeQuery**: 3-layer safety filter — positive query suffix, 80+ hard-blocked categories, per-hit tag validation before download
- **Piano mode beat detection**: spectral flux phrase boundaries instead of drum onsets; system paces with the melody rather than fighting it
- Dynamic pacing: `NoteDensity` (0–1) maps to clip duration (1.8s–4.5s) following melodic intensity
- Per-lyric seasonal color grading: "winter" lyric → blue/cold tones, "summer" lyric → warm golden tones, applied independently per shot
- Smile detection via Qwen2-VL: clips with happy faces receive +1.5 score bonus when lyric sentiment is Positive
- Shot composition rules: no consecutive same-type shots, 30% Rule (no wide shots without children), motion direction matching between consecutive clips
- Warm Continuity filter, Frozen Animal filter, Candid filter (no posed adult stock shots)
- Query cooldown: same visual theme cannot repeat within 4 scenes (~15 seconds)
- Brightness-based tone matching: shots deviating significantly from rolling luminance average are rejected
- Ambient sound mixing from 1,279+ categorized local sound library (semantic matching, outdoor/indoor context)
- `hqdn3d` denoise + `unsharp` masking on every clip for consistent visual texture across different source cameras
- Handwritten font (Segoe Script) title cards for children's content

### AI Highlight Engine

Extracts the best moments from any existing long-form video:

- Multi-frame arc scoring: static opening → fast movement → frozen composition
- Beat-synced cut points aligned to music
- Per-segment thumbnail preview with selective inclusion UI
- Exports directly to timeline or renders as standalone highlight video
- Feeds into Phase 3 pipeline (transitions + audio mix + accessibility report + export)

### Color Grading Engine (Phase 4C)

- AI automatically selects a color grade preset based on clip content (Qwen2-VL / VisionResult)
- 10 presets: Auto, Cinematic, Warm, Cool, Vintage, Vivid, Noir, Golden, Morning, Moody, Natural
- Per-clip FFmpeg `curves` + `eq` filter — no re-render of unaffected clips
- Preview thumbnails per clip before applying
- Also applied per-lyric in AIVideoCreator (seasonal grading independent of the global preset)

### Smart Scene Detection (Phase 4A)

- Automatic scene detection from any video file using FFmpeg `select` filter
- Configurable threshold, minimum scene gap
- Thumbnail generation per scene
- Full text report with change scores, motion type, and timestamps
- Direct export to timeline with selective inclusion

### Timeline AI Assistant (Phase 4B)

- Natural language commands over the timeline via Ollama
- Voice input: hold-to-speak, transcribed by faster-whisper
- Example commands: `remove shorter than 2s`, `keep faces only`, `sort by score`, `keep first 10`, `remove static`
- Full undo support, command history panel

### Transition Engine

- Automatic beat-synced transition selection based on adjacent clip content and energy
- Types: cross-dissolve, FadeBlack, FadeWhite (flash), WipeLeft/Right/Up/Down, SlideLeft/Right, ZoomIn, Pixelize, diagonal wipes
- Energy ramping: smooth ±2 level maximum between consecutive shots
- Pacing-aware durations: fast 0.25s / standard 0.50s / slow 0.70s

### Smart Audio Mixer

- Music track with configurable volume
- **Sidechain ducking**: music is automatically lowered when vocals are present (FFmpeg `sidechaincompress`)
- LUFS normalization to −14 LUFS (YouTube standard)
- Ambient sounds from local library, mixed at 15% below music with 1-second crossfades
- Mute original clip audio option

### Accessibility Report Generator (Phase 3C)

- Per-segment audio-description script with content, motion, season, and transition notes
- Navigation markers: timestamped list for screen reader navigation
- TTS-optimized summary: clean for speech synthesis, no ASCII art
- WCAG-based visual context notes (via Azure AI Foundry when configured)

### Multi-Format Export Pipeline (Phase 3D)

Single click exports all enabled formats simultaneously:
- YouTube FHD — 1920×1080 MP4
- Reels / TikTok — 1080×1920 MP4 (vertical crop)
- MP3 Audio — 192kbps audio-only
- TXT — accessibility report

### Batch Export

- Queue multiple `.iskra` project files
- Sequential render with per-job progress tracking
- Shared output folder with per-project filenames
- Cancel at any point; completed jobs preserved

### Render Engine

- NVENC GPU-accelerated encoding (RTX/GTX)
- Automatic fallback to CPU (libx264) if GPU unavailable
- 4K (3840×2160) output tested on RTX 2060 Max-Q
- Parallel clip processing (up to 4 simultaneous)
- Smart zoompan pipeline — trim before filter prevents CPU hangs on long clips
- FastRender mode for quick previews
- Loop support for clips shorter than their target duration

### AI Transcription

- faster-whisper-xxl (large-v3 model)
- Word-level timestamp alignment (forced alignment mode)
- float16 compute type on CUDA for GPU-accelerated transcription
- SRT subtitle output with millisecond-accurate synchronization
- Hallucination detection: abnormal first-segment gaps are corrected automatically
- VAD filter: segments without human voice are skipped

### Timeline Editor

- Win32 native ListView (JAWS/NVDA compatible out of the box)
- Multi-track support
- Undo/redo system
- Keyframe animation support
- Audio waveform display
- Project templates: save and load full project settings
- Export profiles: YouTube 1080p/4K, TikTok 9:16, Instagram 1:1, Compact

---

## Hardware Tested On

- **CPU:** Intel Core i9-10885H
- **GPU:** NVIDIA RTX 2060 Max-Q (NVENC)
- **RAM:** 32GB DDR4 3200MHz
- **Output:** 4K H.264 via NVENC

---

## Installation

### Prerequisites

- Windows 10 or 11 (64-bit)
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download)
- FFmpeg — download from [ffmpeg.org](https://ffmpeg.org/download.html) and place `ffmpeg.exe` in `Ffmpeg\` subfolder
- VLC media player — for video preview

### Optional (for AI features)

- [Ollama](https://ollama.ai) with models pulled:
  ```bash
  ollama pull qwen2.5:14b
  ollama pull qwen2.5vl:latest
  ```
- faster-whisper-xxl (for transcription and voice commands)
- Pixabay API key — free at [pixabay.com/api/docs](https://pixabay.com/api/docs/)
- Pexels API key — free at [pexels.com/api](https://www.pexels.com/api/) *(fallback provider)*
- Azure AI Foundry endpoint + key *(optional Layer 0 accessibility hints)*

### Build

1. Clone the repository
2. Open `UltraVideoEditor.csproj` in Visual Studio 2022
3. Build (Ctrl+Shift+B)
4. Place `ffmpeg.exe` in `bin\Debug\net8.0-windows\Ffmpeg\`
5. Run

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
└── Ffmpeg/
    └── ffmpeg.exe                # (not included — download separately)
```

---

## Why This Matters

There is no professional video editing software that blind users can actually use independently. Adobe Premiere, DaVinci Resolve, Final Cut — none of them work meaningfully with screen readers.

Ultra Creative Suite exists to change that. It is the only editor where a blind person can open the application, import audio, generate a complete video with stock footage, effects, and subtitles, and render to 4K — all without sighted assistance.

This project is being developed as part of an [NLnet Foundation](https://nlnet.nl) grant application under the NGI0 Commons Fund.

---

## Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

## Code of Conduct

See [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md).

## License

GPL-3.0 License — see [LICENSE](./LICENSE) file.

---

*Ultra Creative Suite — Because creativity has no boundaries.*
