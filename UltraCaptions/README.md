# Ultra Captions

An accessible subtitle/caption editor — part of the [Ultra suite](https://github.com/demirajvazi10-max). Built because every existing captioning tool (Aegisub, Premiere's captions panel, Subtitle Edit) is built around a visual waveform timeline and drag-to-sync editing, neither of which works with a screen reader.

## What it does (v0.1 skeleton)

Two ways to build a caption list, both producing the same editable lines:

- **Auto-transcription** — point it at a media file and it runs local Whisper (no internet, no API key — same approach as Ultra Video Editor's AI Video Creator) to generate a full first-draft caption list with timestamps already filled in.
- **Manual, keyboard-driven timing** — listen to the media and mark exact start/end points as you go, no mouse or waveform required.

Either path lands in the same list, so a Whisper draft can be manually corrected, or a fully manual list can be built from scratch — whichever fits the moment.

### Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `[` | Mark start of the selected line at the current playback position (creates a new line if none is selected) |
| `]` | Mark end of the selected line at the current playback position |
| `Ctrl+N` | New caption line at the current playback position |
| `Delete` | Delete the selected line (when not typing in a text box) |

All shortcuts are disabled while typing in the text box, so they never interfere with writing caption text.

### Import / export

Reads and writes standard `.srt` files — the same format Whisper produces and the format most video editors (including Ultra Video Editor) expect.

## Requirements

- Windows, .NET 8
- For auto-transcription: a local Whisper install (`pip install openai-whisper`, plus ffmpeg). Manual timing works without it.

## Built with

Uses [UltraAccessibleKit](https://github.com/demirajvazi10-max/Ultra-Accessible-Kit) for screen-reader detection, theming (Light/Dark/High Contrast), automatic label filling, and the MVVM foundation — same shared accessibility layer as the rest of the Ultra suite.

## Status

Early skeleton — core loop (open media, transcribe or manually time, edit, export) works end to end. Not yet built/verified locally; review before first release.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
