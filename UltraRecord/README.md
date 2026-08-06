# Ultra Record

Accessible multi-track audio recording — part of the [Ultra suite](https://github.com/demirajvazi10-max). Companion to Ultra Audio Editor: record each participant on their own track, then take the files into the Audio Editor for mixing.

## What it does (v0.1 skeleton)

- **Multiple tracks**, each with its own input device, recorded simultaneously — one WAV file per track.
- **Arm/disarm per track** — record only the tracks that are armed, without needing to remove the others from the session.
- **Audio-based clipping feedback, not a visual meter.** Visual VU meters are inherently inaccessible. Instead, a short system beep plays the moment a track clips (rate-limited so it doesn't spam), and the track's status text updates via `AutomationProperties.LiveSetting`, so a screen reader announces "Clipping!" without needing focus on that track.

## Requirements

- Windows, .NET 8
- A working input device (microphone) recognized by Windows

## Built with

- [UltraAccessibleKit](https://github.com/demirajvazi10-max/Ultra-Accessible-Kit) — screen-reader detection, theming (Light/Dark/High Contrast), automatic label filling, and the MVVM foundation, same as the rest of the Ultra suite.
- [NAudio](https://github.com/naudio/NAudio) — audio capture, same library already used in Ultra Audio Editor.

## Status

Early skeleton — has not yet been built/tested locally. Build in Visual Studio and verify before relying on it for anything important.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
