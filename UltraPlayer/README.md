# Ultra Player

An accessible podcast/audiobook player — part of the [Ultra suite](https://github.com/demirajvazi10-max). Built because mainstream players (Spotify, Audible, and others) have real, reported problems with screen readers — custom UI controls with missing labels, hard-to-reach playback controls.

## What it does (v0.1 skeleton)

- **Playlist** — add one or more audio files, reorder by playing whichever you select.
- **Adjustable playback speed** (0.75x–2.0x) — changes speed without restarting the track.
- **Sleep timer** — 15/30/45/60 minutes, or "end of current track"; playback pauses automatically when it runs out.
- Auto-advances to the next track when one ends (unless the sleep timer is set to stop at end of track).

### Keyboard shortcuts

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `Right` / `Left` | Seek forward / back 10 seconds |
| `Ctrl+Right` / `Ctrl+Left` | Next / previous track |
| `+` / `-` | Speed up / down one step |

Arrow keys behave normally (list/combo navigation) while focus is on the playlist or a dropdown, so they don't fight with seeking.

## Not yet built (planned for later)

- Chapter navigation (needs ID3/MP4 chapter-marker parsing — a separate piece of work, deliberately left out of this first skeleton rather than rushed)
- Remembering playback position between sessions
- Playlist reordering via drag/keyboard

## Requirements

- Windows, .NET 8

## Built with

Uses [UltraAccessibleKit](https://github.com/demirajvazi10-max/Ultra-Accessible-Kit) for screen-reader detection, theming (Light/Dark/High Contrast), automatic label filling, and the MVVM foundation — same shared accessibility layer as the rest of the Ultra suite.

## Status

Early skeleton — has not yet been built/tested locally. Build in Visual Studio and verify before relying on it.

## Language

The app is available in **English** and **Serbian** (Latin script). The **Srpski / English** button in the toolbar, next to About, switches the whole interface instantly, no restart needed, and the choice is remembered (`%LOCALAPPDATA%\UltraPlayer\language.txt`). Until you choose, the app follows the Windows display language: Serbian on a Serbian Windows, English otherwise. The "How to support" guide follows the same choice.

## Supporting the project

Ultra Player is free and will stay free. If you'd like to support its development:

- **By card on [Ko-fi](https://ko-fi.com/ultracreativesuite)** — no account needed: type an amount, press *Donate* and pay with a Visa or Mastercard (credit or debit) or PayPal. The easiest way from abroad.
- **[GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)** — monthly or one-time, paid with an ordinary payment card (no PayPal needed); needs a free GitHub account.
- **Bank transfer from Serbia** — recipient Demir Ajvazi, account `265-0000006185877-28`, Raiffeisen banka, purpose *Donacija za Ultra programe*. Meant for domestic transfers; from abroad, Ko-fi costs far less in fees.

The same guide is inside the app: **About > How to support**, in English and Serbian, with buttons to open Ko-fi or GitHub Sponsors and to copy the account number.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
