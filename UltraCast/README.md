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

## Language

The app is available in **English** and **Serbian** (Latin script). The **Srpski / English** button at the top, next to About, switches the whole interface instantly, no restart needed, and the choice is remembered (`%LOCALAPPDATA%\UltraCast\language.txt`). Until you choose, the app follows the Windows display language: Serbian on a Serbian Windows, English otherwise. The "How to support" guide follows the same choice.

## Supporting the project

Ultra Cast is free and will stay free. If you'd like to support its development:

- **By card on [Ko-fi](https://ko-fi.com/ultracreativesuite)** — no account needed: type an amount, press *Donate* and pay with a Visa or Mastercard (credit or debit) or PayPal. The easiest way from abroad.
- **[GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)** — monthly or one-time, paid with an ordinary payment card (no PayPal needed); needs a free GitHub account.
- **Bank transfer from Serbia** — recipient Demir Ajvazi, account `265-0000006185877-28`, Raiffeisen banka, purpose *Donacija za Ultra programe*. Meant for domestic transfers; from abroad, Ko-fi costs far less in fees.

The same guide is inside the app: **About > How to support**, in English and Serbian, with buttons to open Ko-fi or GitHub Sponsors and to copy the account number.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
