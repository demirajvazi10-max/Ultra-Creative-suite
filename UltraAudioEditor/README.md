# 🎵 Ultra Audio Editor

**A Windows audio editor with full screen reader accessibility.**  
Built from the ground up so blind and sighted users have equal access to every feature.

> Part of the **Ultra** platform — an open accessible engine for creative tools.  
> This tool lives in the `UltraAudioEditor` folder of the [Ultra Creative Suite](../README.md) repository and shares its [GPL-3.0 license](../LICENSE).

---

## Why This Exists

Most audio editors are graphically intensive and practically unusable with a screen reader.
Ultra Audio Editor is different: every single function is reachable by keyboard, every element is announced correctly by JAWS for Windows, and nothing requires a mouse.

At the same time, it is a fully functional audio editor — not a simplified version of one.

---

## Features

### Audio Editing
- Multi-track timeline with unlimited tracks
- Import WAV, MP3, OGG, FLAC, M4A, AIFF
- **Cut a clip at the position** (Ctrl+X) and **cut at all markers** (Ctrl+Shift+X)
- **Markers** — add with M, jump with Alt+Page Down / Alt+Page Up, listed at the end of the project list
- **Delete between two markers** (Ctrl+Delete) — removes that part on all tracks and closes the gap, so vocals and backing stay in sync
- **Copy / paste clips** (Ctrl+C / Ctrl+V at the position)
- **Preview a cut** (P) — plays 2 seconds before and after the position, then returns to it
- Clip positioning to millisecond precision
- Mixdown / combine tracks into one file
- **Real Undo / Redo** of every edit (cuts, moves, markers, effects, recording) — up to 100 steps

### Selection and Processing (Sound Forge style)
- **Select with the keyboard:** **I** sets the selection start at the position, **O** the end; Ctrl+A selects everything; select a clip or the part between two markers from the **Selection** menu. The selection is spoken on request (F6) and shown as a blue area in the visual view.
- **Work on the selection:** play it (Shift+Space, loops with L), copy (Ctrl+C), cut, delete and close the gap on all tracks (Ctrl+Delete), keep only the selection (trim), or turn it into silence.
- **Process menu** — works on the selection of the selected track (no selection: the selected clip, or the whole track):
  - Volume / gain in dB, normalize to a peak level, **normalize loudness to LUFS** (-14 YouTube/Spotify, -16 Apple/podcasts, -19 mono podcast, -23 TV/radio EBU R128) without ever pushing peaks over -1 dB
  - Fade in / fade out with any length and four curve shapes (linear, fast, slow, smooth S-curve)
  - Reverse, insert silence at the position, convert to mono, swap channels, remove DC offset
  - High-pass and low-pass filters
  - **Remove / shorten pauses** — silences are found in the whole mix and removed from all tracks together, so a multi-mic interview stays in sync
  - **Apply an effect permanently** to the selection — with presets, and a live preview of just that part while the dialog is open
- **Restoration:** real **noise removal** from a learned noise profile (select a part with only noise, Learn noise, then Remove noise — spectral subtraction, 8 / 14 / 22 dB), **click and crackle removal**, and **hum removal** (50 Hz Europe / 60 Hz America, plus harmonics).
- **Statistics (F7):** integrated loudness (LUFS, ITU BS.1770 / EBU R128), loudest moment, peak, RMS, number of distorted samples and where the first one is, silence share, DC offset — for the selection, the clip, the track, or the whole mix as heard.
- **Click-free cuts:** every clip edge gets an inaudible 5 ms fade, so a cut never pops wherever the wave was cut.
- Processed parts are written as new WAV files in `Documents\Ultra Audio Editor\Renders`; original files are never changed and Ctrl+Z undoes everything.

### Recording
- **Ctrl+R** records from the position into the selected track (or a new track if that spot is taken); Ctrl+R, Space or S stops
- Uses the default Windows microphone
- If the project already has audio, it plays along while you record (sing over a backing track), and the take is aligned for the output delay
- After recording you are told if the take is almost silent (wrong or muted microphone) or too loud (distorted)

### Tools
- **Volume envelope** per track — points (time, dB) with smooth changes between them, e.g. bring the music down under a voice-over and back up; heard live and in the export.
- **Effect order** per track — move effects up and down the chain (Alt+Up / Alt+Down).
- **Speech transcription with local Whisper** (the same tool Ultra Video Editor uses): every sentence with its time; Enter jumps to that place, add a marker at every sentence, search the text, save as TXT or SRT subtitles.
- **Batch convert** a whole folder to WAV, MP3, M4A, AIFF, FLAC, OGG or Opus, optionally all at the same loudness (-14 LUFS).
- **Song tags** — title, artist, album, year, genre, comment and cover image, written into MP3, M4A, FLAC, OGG and Opus on export.
- **FFmpeg** is used for FLAC, OGG, Opus and tags (and to open OGG / Opus / WMA files); if it is missing, the editor offers to download it once (about 100 MB).
- **Screen reader detection** — with JAWS, NVDA or Narrator running the editor opens in the accessible list view, otherwise in the visual waveform view (Alt+W switches any time).

### Smart Export
- Export to **WAV, MP3, M4A (AAC), AIFF, FLAC, OGG Vorbis and Opus**, with song tags
- Before writing the file the mix is measured: if it would be distorted, you are asked whether to lower it just enough
- A short summary at the end: length, size, tracks and clips in the mix, skipped or missing files, loudest peak and loudness in LUFS

### Live DSP Effects (real-time, no re-render needed)
- **Equalizer** — parametric: bass shelf, mid band with adjustable frequency and width (Q), treble shelf, plus high-pass and low-pass filters
- **Reverb** — mix, room size, damping, pre-delay
- **Delay / Echo** — delay time, feedback, mix
- **Compressor** — threshold, ratio, attack, release, make-up gain
- **Noise Gate** — threshold
- **Bass Boost** — up to 24 dB gain
- **Pitch Shift** — ±12 semitones
- **Chorus** — depth, rate, mix

**Presets, Sound Forge style.** Every effect dialog starts with a preset list: arrow up and down and each preset is applied and heard immediately while the song plays, then fine-tune with the sliders.
- **Reverb** — small room, bathroom (tiles), room, vocal studio, plate, small church, concert hall, large hall, cathedral, arena / stadium, cave. The reverb is a stereo Freeverb-style engine with damping and pre-delay, with tails from under a second (small room) to about six seconds (cathedral).
- **Delay** — doubling, slapback, short, rhythmic, canyon, long, endless space echo
- **Compressor** — gentle vocal, vocal up front, speech / podcast, drum punch, even bass, mix glue, limiter (now with make-up gain)
- **Equalizer** — flat, warm, bright, vocal presence, loudness, strong bass, low cut, old radio, telephone
- **Noise gate, Bass boost, Pitch shift** (octave down to octave up, deep voice, child voice, chipmunk) and **Chorus** (subtle, classic, rich choir, fast shimmer, vibrato; now with rate and mix)

Effects are applied per track: open them from the **Effects** menu in the main menu bar (or the Effects tab in the right panel) for the selected track, or from the track's own menu (Shift+F10). A check mark in the Effects menu shows which effects are on, and **Turn off all effects on the track** resets them in one step.

All effects update in real time while audio is playing — move a slider or tick a checkbox and you hear the change immediately. Every effect dialog has its own **Play / Pause** button (Ctrl+Space), so you can start playback and tweak without closing the dialog.

Track **volume, pan, mute and solo** are live too: no need to stop and play again.

### AI Features
- **Vocal / Instrumental separation** — powered by [Demucs](https://github.com/facebookresearch/demucs) (Meta AI, free, runs locally, no data leaves your machine)
- **Noise removal analysis**, **EQ recommendations**, **loudness analysis**, project description — local **Ollama** by default (no API key, nothing leaves your computer); Groq or Anthropic Claude are optional

### Accessibility
- **JAWS mode** — dedicated accessible view: track list + file list, nothing else on screen
- **Playhead slider** — keyboard-controlled: arrow keys (0.1s steps), Ctrl+arrows (1s), Home/End
- **Context menus** — Shift+F10 or Apps key, every item fully announced by JAWS
- **Effect dialogs** — each effect has its own dialog with labeled sliders and text input fields; no custom WPF controls embedded in menus
- **F6 "where am I"** — time, time to the end, selected track, the clip under the position and the nearest marker, spoken on request only
- **Shift+F6** — full project overview window (all tracks, clips and markers)
- Same shortcuts as Ultra Video Editor wherever both apps do the same thing — nothing new to learn
- Complete Tab order; mouse is never required for any function
- **Diagnostic log** (Ctrl+Shift+L) — `%APPDATA%\UltraAudioEditor\debug.log`, useful for bug reports
- Visual mode shows markers (numbered yellow lines) and the correct part of the waveform for every cut clip

---

## Requirements

| Component | Requirement |
|-----------|-------------|
| Windows | 10 or 11, 64-bit |
| .NET Runtime | 8.0 |
| JAWS for Windows | Any recent version (optional) |
| Python | 3.8+ (optional, only for Demucs vocal separation) |

**.NET 8 Runtime:** https://dotnet.microsoft.com/download/dotnet/8.0

---

## Getting Started

Ultra Audio Editor lives inside the main Ultra Creative Suite repository, in the `UltraAudioEditor` folder.

### Run from source

```cmd
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/UltraAudioEditor
dotnet run --project UltraAudioEditor.csproj
```

### Build a standalone .exe (no .NET required on target)

```cmd
cd Ultra-Creative-suite/UltraAudioEditor
dotnet publish UltraAudioEditor.csproj -c Release -r win-x64 --self-contained -o publish
```

### Open in Visual Studio 2022

Open `UltraAudioEditor.sln` (inside the `UltraAudioEditor` folder) and press F5.

> Note: adjust the paths above if your local folder layout differs — these assume the `.csproj`/`.sln` sit directly inside `UltraAudioEditor/`.

---

## Keyboard Reference

### Moving through time (works anywhere — project list, buttons, slider)

| Key | Action |
|-----|--------|
| Alt+→ / Alt+← | Forward / back 1 second |
| Alt+Shift+→ / Alt+Shift+← | Forward / back 0.1 second |
| Alt+Home / Alt+End | Beginning / end |
| Ctrl+G | Go to a time you type (seconds or minutes:seconds) |

Every move plays a short piece (about 0.3 s) from the new position, so you hear where you are (Playback menu, can be turned off). While the song is playing, moving the position continues playback from the new place.

### Transport

| Key | Action |
|-----|--------|
| Space | Play / Pause |
| S | Stop |
| Home | Go to beginning |
| End | Go to end |
| L | Toggle loop |
| Ctrl+R | Record from the position / stop recording |
| P | Preview: 2 s before and after the position |

### Editing and Markers

| Key | Action |
|-----|--------|
| Ctrl+X | Cut the clip at the position (selected track) |
| Ctrl+Shift+X | Cut at all markers |
| Ctrl+C / Ctrl+V | Copy clip / paste at the position |
| M | Add marker at the position |
| Alt+Page Down / Alt+Page Up | Next / previous marker |
| Ctrl+Delete | Delete everything between the two markers around the position (all tracks, no gap) |
| Ctrl+Z / Ctrl+Y | Undo / Redo |

### Selection

| Key | Action |
|-----|--------|
| I / O | Selection start / end at the position |
| Ctrl+A | Select all |
| Shift+Space | Play the selection (loops when Loop is on) |
| Escape | Clear the selection |
| Ctrl+C | Copy the selection (when there is one) |
| Ctrl+Delete | Delete the selection and close the gap |
| F7 | Statistics: loudness, peak, distortion |

### File and Project

| Key | Action |
|-----|--------|
| Ctrl+N | New project |
| Ctrl+O | Open project |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save as |
| Ctrl+I | Import audio |
| Ctrl+E | Export / mixdown |

### Accessibility

| Key | Action |
|-----|--------|
| Alt+W | Switch JAWS mode / Visual mode |
| F6 | Where am I: time, track, clip under the position, nearest marker |
| Shift+F6 | Full project overview window |
| Ctrl+Shift+L | Diagnostic log |
| Shift+F10 | Context menu for focused track or clip |
| Apps | Context menu (alternative key) |
| F2 | Set clip position (opens dialog) |
| Tab / Shift+Tab | Navigate between elements |

### Clips

| Key | Action |
|-----|--------|
| Ctrl+→ | Move clip right 1s |
| Ctrl+← | Move clip left 1s |
| Ctrl+Shift+→ | Move clip right 0.1s |
| Ctrl+Shift+← | Move clip left 0.1s |
| Delete | Delete selected clip (on a marker row: delete that marker) |

### Playhead slider (when focused)

| Key | Action |
|-----|--------|
| → / ← | Move 0.1 seconds |
| Ctrl+→ / ← | Move 1 second |
| Home | Go to beginning |
| End | Go to end |
| Space | Play / Pause |
| Enter or Tab | Move focus to track list |

---

## JAWS Mode

Press **Alt+W** to switch to JAWS mode. The interface becomes a focused two-panel layout.

**Left panel — Track list**  
Each track announces: name, type, volume, number of files, total duration, mute/solo state.  
Press Shift+F10 for the track context menu.

**Top — Playhead panel**  
Slider always visible. Time display updates live as ARIA assertive region.

**Right panel — File list**  
Native ListView with columns: Name · Start (MM:SS) · Start (s) · Duration (MM:SS) · Duration (s) · End (MM:SS).  
Press Shift+F10 for the clip context menu.

### Track context menu

- Import audio / Import at playhead position / Import at custom position
- **Vocal and instrumental separation** (Demucs, local AI)
- Mute / Solo toggle
- Volume dialog / Pan dialog
- **Effects submenu** — each effect shows current state (on/off), opens dedicated parameter dialog
- Normalize / Fade In / Fade Out
- Combine with another track (mixdown to file)
- Move up / down / Duplicate / Rename / Delete

### Effect dialogs

Each effect opens its own dialog window containing:
- Checkbox: enable / disable
- Slider + text input for each parameter (fully keyboard editable)
- Reset to defaults

Playback keeps running while the dialog is open, so you hear every change live. The **Play / Pause** button (Ctrl+Space) inside the dialog starts or pauses playback without closing it.

---

## Vocal / Instrumental Separation

Uses [Demucs](https://github.com/facebookresearch/demucs) by Meta AI Research. Runs entirely locally — no audio is uploaded anywhere.

**Install once:**

```cmd
pip install demucs
```

Then: track context menu → **Vocal and instrumental separation...**

- **2 stems** — Vocals + Instrumental (faster, ~2–5 min per song)
- **4 stems** — Vocals + Drums + Bass + Other (more detailed)

Separated stems are imported automatically as new tracks.

---

## AI Features Setup

### Ollama — Local, default

Nothing to set up in the app: the installer installs Ollama and a model sized for your hardware. AI requests never leave your computer.

### Groq — Free

1. Register at https://console.groq.com
2. API Keys → Create API Key
3. In the app: right panel → AI Functions → Groq → enter key

Provides free access to Llama 3 (analysis) and Whisper (transcription).

### Anthropic Claude — Pay per use

1. Register at https://console.anthropic.com
2. API Keys → Create Key
3. In the app: AI Functions → Anthropic → enter key

---

## Project Structure

```
UltraAudioEditor/
├── Models/
│   └── AudioModels.cs          — AudioProject, AudioTrack, AudioClip, TrackEffects
├── ViewModels/
│   ├── MainViewModel.cs        — commands, editing, undo, playback, recording, export
│   └── MainViewModel.Pro.cs    — selection, Process menu, restoration, statistics, tools
├── Views/
│   ├── MainWindow.xaml/.cs     — main window, dual mode switching
│   ├── Controls/
│   │   └── AccessibleTrackList — JAWS mode: track list + file list + playhead
│   ├── EffectDialog.cs         — accessible per-effect parameter dialogs (with Play / Pause)
│   ├── AboutWindow.cs          — About, version, GitHub Sponsors link
│   ├── LogWindow.cs            — diagnostic log viewer (Ctrl+Shift+L)
│   ├── ProDialogs.cs           — choice dialog, envelope, effect order, tags, transcript, batch convert
│   ├── SetClipPositionDialog   — clip position input (seconds or MM:SS)
│   └── SetValueDialog.cs       — generic labeled value input dialog
├── Services/
│   ├── AudioEngine.cs          — NAudio playback, live DSP chain, export (loudness check + encode)
│   ├── AudioRecorder.cs        — microphone recording to WAV, peak level check
│   ├── DebugLog.cs             — diagnostic log
│   ├── Dsp/Dsp.cs              — FFT, LUFS loudness, processing, noise/click/hum removal, statistics
│   ├── AudioBuffers.cs         — read a track range into memory, write processed renders
│   ├── FfmpegService.cs        — FLAC/OGG/Opus, tags, import of other formats, one-time download
│   ├── WhisperService.cs       — local speech transcription
│   ├── ScreenReaderDetector.cs — opens the right view for screen reader users
│   ├── EffectPresets.cs        — Sound Forge style presets for every effect
│   ├── DemucsService.cs        — Demucs subprocess wrapper, stem import
│   └── AnthropicService.cs     — Groq and Anthropic Claude API client
└── Controls/
    └── WaveformControl.cs      — waveform renderer (visual mode only)
```

**Live DSP chain** (per clip, evaluated in real time):
```
File → Mono/Stereo → Resample → NoiseGate → Compressor → PitchShift → EQ → BassBoost → Chorus → Delay → Reverb → Volume/Pan/Mute/Solo (live) → Offset → Mixer
```

---

## NLnet / NGI

This project is part of the **Ultra** platform — a proposal for fully accessible professional creative tools.  
The goal is that blind and visually impaired users should not have to use stripped-down alternatives when professional tools exist.

If you are from NLnet or NGI0, welcome. Feel free to open an issue or contact us.

---

## Versioning

The version shown in the title bar, status bar and About window comes from one place: `<Version>` in `UltraAudioEditor.csproj`. Bump it before tagging a release (`audio-vX.Y.Z`).

---

## Author

Created by **Demir Ajvazi**.
If Ultra Audio Editor is useful to you, you can support its development on [GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max).

---

## License

GPL-3.0 — see the repository's [LICENSE](../LICENSE) file. This tool shares its license with the rest of the Ultra Creative Suite repository.

---

## Contributing

Pull requests welcome. If you use a screen reader and find anything that JAWS or NVDA does not announce correctly, please open an issue — accessibility regressions are treated as critical bugs.
