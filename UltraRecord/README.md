# Ultra Record

Accessible multi-track audio recording — part of the [Ultra suite](https://github.com/demirajvazi10-max). Companion to Ultra Audio Editor: record each participant on their own track, then take the files into the Audio Editor for mixing.

## What it does (v0.1 skeleton)

- **Multiple tracks**, each with its own input device, recorded simultaneously — one WAV file per track.
- **"Record this track" checkbox per track** — record only the tracks you check, without needing to remove the others from the session. The visible text and the screen-reader name are the same.
- **Audio-based clipping feedback, not a visual meter.** Visual VU meters are inherently inaccessible. Instead, a short system beep plays the moment a track clips (rate-limited so it doesn't spam), and the track's status text updates via `AutomationProperties.LiveSetting`, so a screen reader announces "Clipping!" without needing focus on that track.

## Requirements

- Windows, .NET 8
- A working input device (microphone) recognized by Windows

## Built with

- [UltraAccessibleKit](https://github.com/demirajvazi10-max/Ultra-Accessible-Kit) — screen-reader detection, theming (Light/Dark/High Contrast), automatic label filling, and the MVVM foundation, same as the rest of the Ultra suite.
- [NAudio](https://github.com/naudio/NAudio) — audio capture, same library already used in Ultra Audio Editor.

## Status

Early skeleton — has not yet been built/tested locally. Build in Visual Studio and verify before relying on it for anything important.

## Language

The app is available in **English** and **Serbian** (Latin script). The **Srpski / English** button in the toolbar, next to About, switches the whole interface instantly, no restart needed, and the choice is remembered (`%LOCALAPPDATA%\UltraRecord\language.txt`). Until you choose, the app follows the Windows display language: Serbian on a Serbian Windows, English otherwise. The "How to support" guide follows the same choice.

## Supporting the project

Ultra Record is free and will stay free. If you'd like to support its development:

- **By card on [Ko-fi](https://ko-fi.com/ultracreativesuite)** — no account needed: type an amount, press *Donate* and pay with a Visa or Mastercard (credit or debit) or PayPal. The easiest way from abroad.
- **[GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)** — monthly or one-time, paid with an ordinary payment card (no PayPal needed); needs a free GitHub account.
- **Bank transfer from Serbia** — recipient Demir Ajvazi, account `265-0000006185877-28`, Raiffeisen banka, purpose *Donacija za Ultra programe*. Meant for domestic transfers; from abroad, Ko-fi costs far less in fees.

The same guide is inside the app: **About > How to support**, in English and Serbian, with buttons to open Ko-fi or GitHub Sponsors and to copy the account number.

## Author

Built by [Demir Ajvazi](https://github.com/demirajvazi10-max).

## License

GPL-3.0 — same as the rest of the Ultra suite.
