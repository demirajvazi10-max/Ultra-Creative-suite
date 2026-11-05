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

## Language

The app is available in **English** and **Serbian** (Latin script). The **Srpski / English** button in the toolbar, next to About, switches the whole interface instantly, no restart needed, and the choice is remembered (`%LOCALAPPDATA%\UltraCaptions\language.txt`). Until you choose, the app follows the Windows display language: Serbian on a Serbian Windows, English otherwise. The "How to support" guide follows the same choice.

## Supporting the project

Ultra Captions is free and will stay free. If you'd like to support its development:

- **By card on [Ko-fi](https://ko-fi.com/ultracreativesuite)** — no account needed: type an amount, press *Donate* and pay with a Visa or Mastercard (credit or debit) or PayPal. The easiest way from abroad.
- **[GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)** — monthly or one-time, paid with an ordinary payment card (no PayPal needed); needs a free GitHub account.
- **Bank transfer from Serbia** — recipient Demir Ajvazi, account `265-0000006185877-28`, Raiffeisen banka, purpose *Donacija za Ultra programe*. Meant for domestic transfers; from abroad, Ko-fi costs far less in fees.

The same guide is inside the app: **About > How to support**, in English and Serbian, with buttons to open Ko-fi or GitHub Sponsors and to copy the account number.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
