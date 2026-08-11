# Ultra Cast

Accessible screen recording — part of the [Ultra suite](https://github.com/demirajvazi10-max). Built for tutorials, demos, and walkthroughs, without requiring sight to operate or to confirm that recording is actually happening.

## What it does (v0.1 skeleton)

- **Screen recording** of the primary monitor, encoded to MP4 via a bundled FFmpeg.
- **System audio capture ("what you hear")** via WASAPI loopback. This naturally includes screen-reader speech (JAWS, NVDA, Narrator) — no screen-reader-specific hook is needed, since the speech is just audio going to the default output device like anything else.
- **Optional microphone track**, mixed in alongside system audio for spoken narration on top of the screen reader.
- **Global hotkeys that work from any window** — `Ctrl+Alt+R` starts/stops recording, `Ctrl+Alt+P` pauses/resumes — since the whole point is to record whatever else you're doing, not to keep Ultra Cast itself focused.
- **Non-visual confirmation**: a short sound plays when recording starts, stops, and pauses, so nothing depends on seeing an indicator.
- **Live-announced status** via `AutomationProperties.LiveSetting`, so a screen reader speaks state changes ("Recording...", "Saved: ...") without needing focus on the status text.

## How it works internally

Video and audio are captured on two independent pipelines and combined only once, at the end:

1. `ScreenCaptureService` grabs frames on a timer (`Graphics.CopyFromScreen`) and streams them as raw video straight into FFmpeg's stdin, which encodes a video-only MP4 as frames arrive.
2. `AudioLoopbackMixer` records WASAPI loopback (system audio) and the microphone in parallel, mixes them via NAudio's `MixingSampleProvider`, and writes a WAV file.
3. On stop, `RecordingCoordinator` runs one more FFmpeg pass to mux the video MP4 and the audio WAV into the final file, then deletes the temporary files.

Keeping video and audio as two separate pipelines (instead of one shared FFmpeg process) was a deliberate choice — easier to reason about, and easier to diagnose from a build/test report if something goes wrong.

## Requirements

- Windows 10/11 (64-bit)
- FFmpeg (downloaded automatically by the installer if not already present)

## Built with

- [UltraAccessibleKit](https://github.com/demirajvazi10-max/Ultra-Accessible-Kit) — screen-reader detection, theming (Light/Dark/High Contrast), automatic label filling, and the MVVM foundation, same as the rest of the Ultra suite.
- [NAudio](https://github.com/naudio/NAudio) — WASAPI loopback and microphone capture/mixing, same library already used elsewhere in the suite.
- FFmpeg — video encoding and final audio/video muxing.

## Status

Early skeleton — has not yet been built/tested locally. Build in Visual Studio and verify before relying on it for anything important. Known v1 limitations: primary monitor only (no monitor picker or window-specific capture yet), no live preview while recording.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
