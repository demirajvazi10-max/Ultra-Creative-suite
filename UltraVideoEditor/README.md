# UltraVideoEditor — AI-Powered Music Video Creator

> Automatic music video generator with deep AI integration: lyric analysis, beat detection, semantic shot matching, color grading, and multi-format export.

---

## What is this?

UltraVideoEditor is a WPF desktop application (.NET 8) that takes an audio file and song lyrics as input and returns a finished music video. The system uses a local LLM (Ollama/Qwen), computer vision (Qwen2-VL + ONNX MobileNet), Whisper transcription, and an FFmpeg render pipeline to automatically:

- Analyze lyrics and assign semantic, emotional, and seasonal context to each shot
- Download relevant stock video clips from Pixabay (with Pexels and Coverr as fallback providers)
- Synchronize cuts to musical phrases (beat detection + piano mode)
- Apply per-lyric color grading, cross-dissolve transitions, and ambient sound mixing
- Export to multiple formats simultaneously (YouTube FHD, Reels/TikTok, MP3, accessibility report)

---

## System Architecture

```
Audio file + Song lyrics
        │
        ▼
┌─────────────────────┐
│   AITranscription   │  faster-whisper → timestamped lyrics (word-level)
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   BeatDetection     │  FFmpeg RMS energy → beat timestamps
│   + Piano Mode      │  Spectral flux → melodic phrase boundaries
└─────────┬───────────┘
          │
          ▼
┌──────────────────────────────────┐
│  AIVideoCreator  (orchestrator)  │
│                                  │
│  ┌────────────────────────────┐  │
│  │  Layer 0 — Azure Foundry  │  │  WCAG-based accessibility hints
│  │  Layer 1 — Ollama/Qwen    │  │  Semantic query generation
│  │  Layer 2 — StrictQuery    │  │  552+ B/H/S → EN keyword map
│  │  Layer 3 — SmartFallback  │  │  Never null, never black screen
│  └────────────────────────────┘  │
│                                  │
│  IskraKidsSafeQuery              │  3-layer kids-safety filter
└─────────┬────────────────────────┘
          │
          ▼
┌─────────────────────┐
│  Media Providers    │  Pixabay (primary) → Pexels → Coverr (waterfall)
│  + Deduplication   │  Per-session asset deduplication, page rotation
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  VisionAnalyzer     │  Qwen2-VL / ONNX → score, HasChildren,
│                     │  HasSmile, IsOutdoor, season, luminance
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  MotionAnalyzer     │  FFmpeg optical flow → direction matching
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  ColorGradingEngine │  AI auto-grade per clip (10 presets + Auto)
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  TransitionEngine   │  Beat-synced xfade selection by content type
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  SmartAudioMixer    │  Music + ambient sounds, ducking, LUFS normalization
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  RenderEngine       │  FFmpeg filter_complex → final video
│                     │  xfade + color grading + denoise + GPU encode
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  ExportPipeline     │  YouTube FHD + Reels/TikTok + MP3 + TXT report
└─────────────────────┘
```

---

## Requirements

### Runtime

| Component | Version | Notes |
|---|---|---|
| Windows | 10 / 11 | WPF application |
| .NET | 8.0 | `net8.0-windows` |
| FFmpeg | 6.0+ | Must be in `Ffmpeg/` folder next to the exe |
| Ollama | Any | Local LLM server |
| Qwen2.5 14B | Via Ollama | `ollama pull qwen2.5:14b` |
| Qwen2-VL | Via Ollama | `ollama pull qwen2.5vl:latest` |
| faster-whisper | xxl | Place `faster-whisper-xxl.exe` in `Whisper/` folder |

### API Keys

- **Pixabay** — free API key from [pixabay.com/api/docs](https://pixabay.com/api/docs/) *(primary)*
- **Pexels** — free API key from [pexels.com/api](https://www.pexels.com/api/) *(fallback)*
- **Azure AI Foundry** — optional, enables Layer 0 accessibility hints

### NuGet Packages

```xml
<PackageReference Include="LibVLCSharp" Version="3.9.6" />
<PackageReference Include="LibVLCSharp.WPF" Version="3.9.6" />
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="14.13.0" />
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
git clone https://github.com/YOUR_REPO/UltraVideoEditor.git
cd UltraVideoEditor
dotnet restore
dotnet build -c Release
```

Place external tools:

```
UltraVideoEditor/
├── Ffmpeg/
│   └── ffmpeg.exe
├── Whisper/
│   └── faster-whisper-xxl.exe
└── Assets/
    └── Sounds/           # optional: 1,279+ ambient sound files
```

Pull Ollama models:

```bash
ollama pull qwen2.5:14b
ollama pull qwen2.5vl:latest
```

---

## How It Works

### 1. Query Pipeline (4 layers)

Every lyric line passes through four layers to produce a Pixabay search query:

**Layer 0 — Azure AI Foundry (optional)**
If an Azure AI Foundry endpoint is configured, the system sends the lyric to an Azure-hosted model that returns WCAG-based accessibility visual context hints. These hints are passed to Ollama as additional context — improving query accuracy for educational and children's content.

**Layer 1 — Ollama/Qwen (primary)**
Qwen receives the lyric + `LyricTagType` (Action / Atmospheric / Object / Narrative) + `SentimentPolarity` (Positive / Negative / Neutral) + `needsCloseUp` flag and generates a 3–5 word English search query.

**Layer 2 — `_actionMap` (552+ entries)**
Direct match of B/H/S keywords to English visual queries. Priority scoring favors concrete objects over abstract states — "sladoled" (score 50) always wins over "leti" (score 14) in the same lyric.

**Layer 3 — SmartFallback**
Contextual fallback based on detected season and mood. Never returns null, never produces a black screen.

### 2. Kids-Safe Query Filter (`IskraKidsSafeQuery`)

Three-layer safety system for children's content (ages 3–7):

- **Layer 1** — Positive suffix: `" kids sunny"` appended to every query
- **Layer 2** — Hard-block: 80+ forbidden category terms removed from the query string before sending to API (medical/pregnancy, exotic animals, dangerous content, adult lifestyle, gym/fitness, etc.)
- **Layer 3** — `IsHitSafe()`: Pixabay tag string validation **before** download — rejects hits containing blacklisted tags regardless of query

**Seasonal locking**: if a lyric mentions a season ("ljeto" / "zima"), the query is locked to season-appropriate visuals and the opposite season is blocked entirely for that shot.

### 3. Beat Detection + Piano Mode

For standard music with drums: RMS energy spikes → beat timestamps → cuts on downbeats.

For piano/melodic music (low confidence or irregular beats):
- **Phrase detection**: smoothed energy profile → spectral flux → melodic phrase boundaries
- **Dynamic pacing**: `NoteDensity` (0–1) maps to clip duration — quiet phrase → 4.5s, dense passage → 1.8s
- **VibeScore modifier**: high-energy scenes receive 25% shorter clips
- **Cut-advance**: clips are trimmed 100–150ms before the beat so the visual change lands at the same moment the ear hears the onset

### 4. Vision Analysis

Every downloaded clip is analyzed by **Qwen2-VL** (if available) or **ONNX MobileNetV2** (fallback):

| Field | Description |
|---|---|
| `Score` 1–10 | Overall visual quality |
| `HasChildren`, `HasFaces` | Presence of children / faces |
| `HasSmile` | Detected smile — triggers +1.5 score bonus on Positive lyrics |
| `IsOutdoor`, `IsWarm` | Environment and color temperature |
| `Luminance` | Average brightness — used for tone-match filtering |
| `RetryNeeded` | Qwen flags the clip as contextually mismatched |

### 5. Per-Lyric Color Grading (`ColorGradingEngine`)

Each shot receives an FFmpeg `curves` + `eq` filter based on the season of **that specific lyric**, not the global song:

| Season | Effect |
|---|---|
| `winter` | Blue tones, lowered R, raised B, desaturation −12% |
| `summer` | Golden tones, raised R/G, lowered B, saturation +18% |
| `spring` | Fresh greenish cast, slight G boost |
| `autumn` | Warm orange, raised R, lowered B |

10 manual presets also available: Cinematic, Warm, Cool, Vintage, Vivid, Noir, Golden, Morning, Moody, Natural.

### 6. Transition Engine (`TransitionEngine`)

Beat-synced transition selection based on adjacent clip content:

| Scenario | Transition |
|---|---|
| Action → Action | FadeWhite flash cut (0.20s) |
| Arc shot (dynamic motion) | WipeLeft/Right in direction of motion |
| Same content type repeating | Diagonal wipe (Diagtl/Diagtr) |
| Default | Cross-dissolve (0.25s / 0.50s / 0.70s by energy) |

Energy ramping is smoothed — transitions cannot jump more than 2 energy levels between consecutive shots.

### 7. Smart Audio Mixer (`SmartAudioMixer`)

- Music volume control with **audio ducking**: music is automatically lowered when dialogue/vocals are present (sidechain compressor via FFmpeg `sidechaincompress` filter)
- Original clip audio at configurable volume
- LUFS normalization to −14 LUFS (YouTube standard)
- Ambient sounds from 1,279+ categorized local sound library, mixed at 15% volume below music
- 1-second crossfade between ambient sound segments

### 8. Shot Composition Rules

The system tracks the sequence of shots and avoids:
- Two consecutive `wide` shots without children (PATCH9 30% Rule)
- Two consecutive `medium` shots (shot composition filter)
- Extreme jump `wide → close` without a bridge shot
- Cold/blue palette shots in a warm-themed song (Warm Continuity filter)
- Static animal shots with no motion (Frozen Animal filter)
- Commercial/posed adult shots (Candid filter, PATCH10)
- Indoor shots in an outdoor song context

### 9. Motion Matching (`MotionAnalyzer`)

Analyzes optical flow at the **end** of the current clip and the **start** of the next. Compatible motion direction is required — eliminates jump-cut artifacts at edit points.

### 10. Query Cooldown System

The same visual theme (first 2 keywords of the query) cannot repeat within 4 consecutive scenes (~12–16s). When repetition is detected, the query receives a seasonal variant automatically.

### 11. Batch Export (`BatchExportEngine`)

Multiple `.iskra` project files can be queued and rendered sequentially in one click. Each job renders independently with its own settings and output path.

### 12. AI Highlight Engine (`AIHighlightEngine`)

Separate from AIVideoCreator — extracts the best moments from an existing long-form video:

- Multi-frame arc scoring: static opening → movement → frozen composition
- Beat-synced cut points
- Per-segment thumbnail preview with selection UI
- Exports directly to timeline or renders as standalone highlight video
- Integrates with Phase 3 pipeline (transitions + audio mix + accessibility report + multi-format export)

### 13. Smart Scene Detection (`SmartSceneDetector` — Phase 4A)

Automatic scene detection from any video file:

- FFmpeg `select` filter with configurable threshold
- Uniform fallback cuts if FFmpeg detects nothing (some container formats)
- Minimum scene gap filtering (MinSceneSec)
- Per-scene thumbnail generation
- Full text report with change scores, motion type, and timestamps
- Direct export to timeline with selective inclusion

### 14. Timeline AI Assistant (`TimelineAIAssistant` — Phase 4B)

Natural language commands over the timeline, powered by Ollama:

- Voice input via faster-whisper (hold-to-speak)
- Commands: `remove shorter than 2s`, `keep faces only`, `sort by score`, `keep first 10`, `remove static`, `sort by duration`
- Full undo support
- Command history panel

### 15. Accessibility Report Generator (Phase 3C)

Generates a complete audio-description script for the finished video:

- Per-segment audio description (content, motion, season, transitions)
- Navigation markers (timestamp list for screen reader navigation)
- TTS-optimized summary (no ASCII art, clean for speech synthesis)
- WCAG-based visual context notes

---

## Key Classes

| Class | Responsibility |
|---|---|
| `AIVideoCreator` | Main orchestrator — scene loop, query pipeline, media selection |
| `StrictQueryEngine` | B/H/S → EN keyword map, Ollama prompt builder, lyric classification |
| `IskraKidsSafeQuery` | 3-layer kids-safety filter for Pixabay/Pexels queries |
| `BeatDetection` | Audio analysis, beat timestamps, piano mode phrase detection |
| `VisionAnalyzer` | Qwen2-VL / ONNX frame analysis, score, labels, smile |
| `MotionAnalyzer` | FFmpeg optical flow, direction matching |
| `ColorGradingEngine` | Per-clip AI color grade, 10 presets, FFmpeg vf filter builder |
| `TransitionEngine` | Beat-synced xfade type selection by content and energy |
| `SmartAudioMixer` | FFmpeg sidechain ducking, LUFS normalization, ambient mixing |
| `RenderEngine` | FFmpeg filter_complex pipeline, xfade, NVENC/CPU encode |
| `SmartSceneDetector` | FFmpeg-based scene detection from existing video |
| `TimelineAIAssistant` | Ollama-powered natural language timeline commands |
| `AIHighlightEngine` | Multi-frame arc scoring, highlight extraction |
| `AccessibilityReportGenerator` | Audio-description script, nav markers, TTS summary |
| `ExportPipeline` | Simultaneous multi-format export (YouTube / Reels / MP3 / TXT) |
| `BatchExportEngine` | Sequential multi-project render queue |
| `SkiaAnimationEngine` | SkiaSharp-based animated text overlays, title cards |
| `CinematicProcessor` | Ken Burns zoom/pan, SmartCrop, audio ducking |
| `LocalSoundLibrary` | 1,279+ ambient sounds, semantic matching, context filtering |
| `MediaProviders` | Waterfall provider system (Pixabay → Pexels → Coverr + JSON extensions) |
| `FoundryIQClient` | Azure AI Foundry Layer 0 — WCAG-based accessibility query hints |
| `HardwareEncoderDetector` | NVENC auto-detection with fallback to libx264 |

---

## Configuration Reference

Key parameters configured directly in code:

```csharp
// AIVideoCreator.xaml.cs
const int QUERY_COOLDOWN_SCENES = 4;      // Anti-repetition cooldown
const double MAX_LYRIC_SCENE_DURATION = 8.0; // Max seconds per shot

// BeatDetection.cs
double baseDuration = 4.5 - NoteDensity * 2.7;  // Piano pacing: 1.8–4.5s

// RenderEngine.cs — pacing-aware fade durations
"fast"     => 0.25s
"standard" => 0.50s
"slow"     => 0.70s

// hqdn3d=1.5:1.5:6:6  — temporal + spatial denoise
// unsharp=3:3:0.4      — mild sharpening pass
```

---

## Log Output Reference

```
🥁 Beat detection: 120 BPM, 148 beats, confidence=0.72
🎹 Piano mode active: 12 melodic phrases, density=0.43 → 3.3s average
🗓 Season: global=spring, per-lyric=winter → ✅ Season changed to: winter
🏷 LyricTag: Action | Sentiment: Positive | CloseUp: True
   Layer 0 (Azure): hint="child playing in snow, warm clothing, joyful"
🤖 Ollama query: 'child running park joy sunlight'
😊 Smile bonus +1.5 (sentiment=Positive): 6.0 → 7.5
🎬 Shot composition: two consecutive 'medium' shots — looking for different type...
📐 PATCH9 30%Rule: wide shot without children — looking for medium/close shot...
⏭ Cooldown variant (theme 'children stream' was scene 3): '...'
🗓 StrictSeason: clip=summer, song=winter — not compatible, looking for consistent clip...
✅ Score 7.5/10 [Qwen] | Motion:Right | Shot:medium | Season:winter | Children:True Smile:True
✨ Cross-dissolve: 45 clips, avg fade 0.50s (pacing-aware)
```

---

## Known Limitations

- **Pixabay pool**: For longer songs (3+ minutes) the deduplication pool may be exhausted for repeating query themes. The cooldown variant system mitigates this.
- **Piano mode**: Relies on energy flux detection — solo piano without accompaniment may produce fewer phrase boundaries than optimal.
- **Magick.NET**: Version 14.13.0 contains security advisories for the underlying ImageMagick C library. Does not affect runtime behavior (only trusted local images are processed), but updating to the latest version when available without breaking changes is recommended.
- **GPU encoder**: Uses `h264_nvenc` (NVIDIA). Systems without an NVIDIA GPU automatically fall back to `libx264`.

---

## Planned Features

- Contrast boost for playground scenes (`eq=contrast` when `HasChildren=true`)
- 30-second preview renderer before full render
- Additional stock API providers via `.iskraprovider` extension files

---

## License

Private project. All rights reserved.
