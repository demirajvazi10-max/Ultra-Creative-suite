# UltraVideoEditor — AI-Powered Music Video Creator

> Automatski generator video spotova za dječije pjesme, sa dubokom integracijom AI analize teksta, beat detectiona i semantičkog matchinga kadrova.

---

## Šta je ovo?

UltraVideoEditor je WPF desktop aplikacija (.NET 8) koja prima audio fajl i tekst pjesme, a vraća gotov video spot. Sistem koristi lokalni LLM (Ollama/Qwen), computer vision (Qwen2-VL + ONNX MobileNet), Whisper transkripiju i FFmpeg render pipeline da automatski:

- Analizira stihove i dodijeli semantički, emocionalni i sezonski kontekst svakom kadru
- Preuzme relevantne stock video klipove sa Pixabay API-ja
- Sinhronizuje rezove sa muzičkim frazama (beat detection + piano mode)
- Renderuje finalni video sa color gradingom, cross-dissolve prelazima i ambientalnim zvukovima

---

## Arhitektura sistema

```
Audio fajl + Tekst pjesme
        │
        ▼
┌─────────────────────┐
│   AITranscription   │  Whisper → timestampovani stihovi
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   BeatDetection     │  FFmpeg RMS energy → beat timestamps
│   + Piano Mode      │  Spectral flux → melodijske fraze
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  AIVideoCreator     │  Glavni orchestrator
│  ┌───────────────┐  │
│  │ StrictQuery   │  │  SLOJ 1: Ollama/Qwen → semantički query
│  │ Engine        │  │  SLOJ 2: _actionMap (552+ B/H/S → EN)
│  │               │  │  SLOJ 3: SmartFallback
│  └───────────────┘  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Pixabay API        │  Stock video pretraga + deduplication
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
│  RenderEngine       │  FFmpeg filter_complex → finalni video
│                     │  xfade + color grading + denoise
└─────────────────────┘
```

---

## Zahtjevi

### Runtime
| Komponenta | Verzija | Napomena |
|---|---|---|
| Windows | 10 / 11 | WPF aplikacija |
| .NET | 8.0 | `net8.0-windows` |
| FFmpeg | 6.0+ | Mora biti u `Ffmpeg/` folderu uz exe |
| Ollama | Bilo koja | Lokalni LLM server |
| Qwen2.5 14B | Via Ollama | `ollama pull qwen2.5:14b` |
| Qwen2-VL | Via Ollama | `ollama pull qwen2.5vl:latest` |
| Whisper | whisper.exe / faster-whisper-xxl.exe | U `Whisper/` folderu |

### API Ključevi
- **Pixabay** — besplatan API ključ sa [pixabay.com/api/docs](https://pixabay.com/api/docs/)

### NuGet Paketi
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

## Instalacija

```bash
git clone https://github.com/YOUR_REPO/UltraVideoEditor.git
cd UltraVideoEditor
dotnet restore
dotnet build -c Release
```

Smjesti eksterne alate:
```
UltraVideoEditor/
├── Ffmpeg/
│   └── ffmpeg.exe
├── Whisper/
│   └── faster-whisper-xxl.exe   # ili whisper.exe
└── ...
```

Pokrni Ollama i pull modele:
```bash
ollama pull qwen2.5:14b
ollama pull qwen2.5vl:latest
```

---

## Kako radi

### 1. Query pipeline (3 sloja)

Svaki stih prolazi kroz tri sloja da dobije search query za Pixabay:

**Sloj 1 — Ollama/Qwen (primarni)**
Qwen dobija stih + `LyricTagType` (Action/Atmospheric/Object/Narrative) + `SentimentPolarity` (Positive/Negative/Neutral) + `needsCloseUp` flag i generiše engleski search query od 3-5 riječi.

**Sloj 2 — `_actionMap` (552+ unosa)**
Direktan match srpskih/bosanskih/hrvatskih ključnih riječi na engleski vizuelni query. Prioritetni scoring favorizuje konkretne objekte nad apstraktnim stanjima — "sladoled" (score 50) uvijek pobijedi "leti" (score 14) u istom stihu.

**Sloj 3 — SmartFallback**
Kontekstualni fallback baziran na detektovanoj sezoni i moodu — nikad ne vraća null, nikad crni ekran.

### 2. Beat Detection + Piano Mode

Za standardnu muziku sa bubnjom: RMS energy spikevi → beat timestamps → rezovi na downbeatovima.

Za klavirsku/melodijsku muziku (niska confidence ili neravnomjerni beati):
- **Phrase detection**: smoothovani energy profil → spectral flux → granice melodijskih fraza
- **Dynamic pacing**: `NoteDensity` (0-1) mapiran na trajanje kadrova — tiha fraza → 4.5s, gust pasaž → 1.8s
- **VibeScore modifikator**: high energy scene dobijaju 25% kraći kadar

Log: `🎹 Piano mode aktivan: 12 melodijskih fraza, gustoća=0.43 → 3.3s prosječno`

### 3. Vision Analysis

Svaki preuzeti klip analizira **Qwen2-VL** (ako dostupan) ili **ONNX MobileNetV2** (fallback):

- `Score` 1-10 (opšti vizuelni kvalitet)
- `HasChildren`, `HasFaces`, `HasSmile` — prisutnost djece i emocija
- `IsOutdoor`, `IsWarm` — ambijent i temperatura boje
- `RetryNeeded` — Qwen označava da klip ne odgovara kontekstu stiha

**Smile bonus**: ako `HasSmile=true` i stih je `Positive` sentiment → VisionScore +1.5

### 4. Sezonski Color Grading (per-kadar)

Svaki kadar dobija FFmpeg `curves` + `eq` filter baziran na sezoni **tog stiha**, ne globalne pesme:

| Sezona | Efekat |
|---|---|
| `winter` | Plavi toni, snižen R, podignut B, desaturacija -12% |
| `summer` | Zlatni toni, podignut R/G, snižen B, saturacija +18% |
| `spring` | Svježi zelenkast, blago podignut G |
| `autumn` | Topao narandžast, podignut R, snižen B |

### 5. Shot Composition

Sistem prati niz kadrova i izbjegava:
- Dva uzastopna `wide` kadrova bez djece (PATCH9 30% Rule)
- Dva uzastopna `medium` kadrova (Shot composition filter)
- Ekstreman preskok `wide → close` bez bridge kadra

### 6. Motion Matching

`MotionAnalyzer` analizira optički tok prvog i **zadnjeg** frame-a svakog klipa. Sljedeći klip mora imati kompatibilan smjer kretanja — eliminacija jump-cut problema.

### 7. Query Cooldown

Ista vizuelna tema (prvih 2 ključne riječi querija) ne smije se ponoviti unutar 4 uzastopne scene (~12-16s). Ako se detektuje ponavljanje, query dobija sezonsku varijantu.

---

## Ključne klase

| Klasa | Odgovornost |
|---|---|
| `AIVideoCreator` | Glavni orchestrator — scene loop, query pipeline, selekcija medija |
| `StrictQueryEngine` | B/H/S → EN keyword mapa, Ollama prompt builder, ClassifyLyric/Sentiment |
| `BeatDetection` | Audio analiza, beat timestamps, piano mode phrase detection |
| `VisionAnalyzer` | Qwen2-VL / ONNX analiza kadrova, score, labels, smile |
| `MotionAnalyzer` | FFmpeg optical flow, direction matching |
| `RenderEngine` | FFmpeg filter_complex build, xfade, color grading, denoise |
| `SkiaAnimationEngine` | Skia-based text overlay i animacije naslova |
| `CinematicProcessor` | Ken Burns, zoom/pan efekti |
| `LocalSoundLibrary` | 1279+ ambijentalnih zvukova, kategorizacija i matching |

---

## Konfiguracija

Sve se podešava direktno u kodu. Najvažniji parametri:

```csharp
// BeatDetection.cs
const int QUERY_COOLDOWN_SCENES = 4;     // Anti-ponavljanje tema
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

## Log poruke (referenca)

```
🥁 Beat detection: 120 BPM, 148 udaraca, confidence=0.72
🎹 Piano mode aktivan: 12 melodijskih fraza, gustoća=0.43 → 3.3s prosječno
🗓 Sezona: globalna=spring, po stihu=winter → ✅ Sezona promijenjena na: winter
🏷 LyricTag: Action | Sentiment: Positive | CloseUp: True
🤖 Ollama query: 'child running park joy sunlight'
😊 Smile bonus +1.5 (sentiment=Positive): 6.0 → 7.5
🎬 Shot composition: dva uzastopna 'medium' — trazim drugi tip...
📐 PATCH9 30%Rule: wide kadar bez djece — tražim medium/close plan...
🔄 Cooldown varijanta (tema 'children stream' bila scena 3): '...'
✅ Score 7.5/10 [Qwen] | Motion:Right | Shot:medium | Season:winter | Children:True Smile:True
✨ Cross-dissolve: 45 klipova, avg fade 0.50s (pacing-aware)
```

---

## Poznata ograničenja

- **Pixabay pool**: Za dugačke pjesme (3+ minute) deduplication pool može biti iscrpljen za ponavljajuće query teme. Sistem ima cooldown varijantu kao mitigation.
- **Beat detection na klaviru**: Piano mode se oslanja na energy flux detekciju — za solo klavir bez pratnje može generisati manje phrase boundaryja nego što je optimalno.
- **Magick.NET**: Verzija 14.13.0 sadrži security advisories za ImageMagick C biblioteku. Ne utiče na rad programa (ne obrađuje eksterne/untrusted slike), ali preporučuje se update na najnoviju verziju kada bude dostupna bez breaking changes.
- **GPU enkoder**: Koristi `h264_nvenc` (NVIDIA). Na sistemima bez NVIDIA GPU, automatski fallback na `libx264`.

---

## Razvoj

Projekt je aktivan. Sljedeće planirane funkcionalnosti:

- Contrast boost za dječije playground scene (`eq=contrast` when `HasChildren=true`)
- Preview renderer (30s test render prije punog rendera)
- Podrška za više stock API providera (Pexels, Unsplash video)

---

## Licenca

Privatni projekt. Sva prava zadržana.
