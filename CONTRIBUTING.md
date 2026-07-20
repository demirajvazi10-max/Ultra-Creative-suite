# Contributing to Ultra Creative Suite

Thank you for your interest in contributing to Ultra Creative Suite.

This project exists to make video editing accessible to blind and visually impaired users. Every contribution — code, documentation, testing, or feedback — directly helps people who currently have no professional video editing tools available to them.

This repository contains two tools: the **Video Editor** (this root folder) and the **Audio Editor** (`UltraAudioEditor/`). The guidelines below apply to both unless noted otherwise; the Audio Editor's own setup steps are documented in [UltraAudioEditor/README.md](UltraAudioEditor/README.md).

---

## Before You Start

Please read the [Code of Conduct](CODE_OF_CONDUCT.md). All contributors are expected to follow it.

---

## How to Contribute

### Reporting Bugs

Open an issue with:
- A clear description of the problem
- Steps to reproduce
- Expected vs actual behavior
- Log output (copy from the log window in the app)
- Your system: Windows version, .NET version, GPU model
- Screen reader being used (JAWS version, NVDA version, or N/A)

### Accessibility Issues

Accessibility bugs are **highest priority**. If something does not work correctly with JAWS or NVDA, please open an issue immediately with the label `accessibility`.

Include:
- Screen reader name and version
- What JAWS/NVDA announced
- What it should have announced
- Which control or dialog is affected

### Suggesting Features

Open an issue with the label `enhancement`. Describe:
- What the feature does
- Why it matters for accessibility
- How a blind user would interact with it

### Submitting Code

1. Fork the repository
2. Create a branch: `git checkout -b fix/your-description`
3. Make your changes
4. Test with a screen reader if possible
5. Submit a pull request with a clear description

---

## Code Guidelines

**Language:** C# / .NET 8 / WPF

**Comments:** Write comments in Serbian (ekavica dialect) — this is the project convention.

**Accessibility first:** Every UI change must be tested for screen reader compatibility. If you add a new control, it must have:
- A proper `AutomationProperties.Name`
- Correct tab order
- Keyboard accessibility (no mouse-only interactions)

**No breaking changes to the Win32 ListView bridge** — this is the core accessibility component. Changes here require extra review.

**FFmpeg commands:** Document any new FFmpeg filter chains with comments explaining what each parameter does and why.

**Logging:** Use `LogToMainWindow()` for all significant operations. Blind users rely on the log to understand what the application is doing.

---

## Development Setup

These steps are for the **Video Editor** (root folder). For the Audio Editor, see its own [README](UltraAudioEditor/README.md).

1. Install Visual Studio 2022 or later
2. Install .NET 8 SDK
3. Clone the repository
4. Download FFmpeg and place `ffmpeg.exe` in `bin\Debug\net8.0-windows\Ffmpeg\`
5. Install VLC media player (for LibVLC preview)
6. Build and run

Optional for AI features:
- Install [Ollama](https://ollama.ai) and pull the models used by the project:
  ```bash
  ollama pull qwen2.5:14b
  ollama pull qwen2.5vl:latest
  ```
- faster-whisper-xxl (for transcription)
- Pixabay API key — free at [pixabay.com/api/docs](https://pixabay.com/api/docs/)
- Pexels API key — free at [pexels.com/api](https://www.pexels.com/api/) *(fallback provider)*
- Azure AI Foundry endpoint + key *(optional Layer 0 accessibility hints)*

---

## Priority Areas

These areas need the most help:

- **Accessibility testing** with different screen readers and Windows versions
- **Documentation** in English for international contributors
- **Linux/macOS port** investigation (currently Windows-only)
- **Performance** — render pipeline optimization
- **Localization** — both editors now ship with English as the default UI language and Serbian as a second language. Adding further languages (see the Localization guide below) is a welcoming first contribution.

---

## Localization

Both editors keep **every user-facing string** (window text, menus, buttons, dialog messages, and — importantly — the `AutomationProperties.Name` strings that screen readers announce) out of the code and in a translation layer. English is the default; Serbian is the second language. The two editors use slightly different mechanisms for historical reasons, but the rule is the same in both: **no UI string is ever hardcoded.**

### Video Editor — `LanguageManager.cs`

- Translations live in `LanguageManager.cs` as nested dictionaries (`en`, `sr`, `de`), looked up with `LanguageManager.GetText("key")` (falls back to English, then to the key itself).
- XAML elements are tagged with `Tag="key"` (or `Tag="emoji|key"`). At load, `ApplyLanguage()` walks the visual tree and fills in `Header`/`Content`/`Text`/`ToolTip` plus `AutomationProperties.Name`. `MenuItem` headers and WinForms `ListView` columns are handled by dedicated passes.
- **Do not** translate `StrictQueryEngine.cs` or `TimelineAIAssistant.cs`: the Serbian words there are *input* patterns that let users type/say commands in their language, not UI text.

### Audio Editor — `Localization/Lang.cs`

- A single static class holds a `key → (en, sr)` table. Call `Lang.T("key")` from code (with `string.Format` for placeholders like `{0}`).
- XAML binds via `{DynamicResource L_key}`. On startup and on every language switch, `Lang.ApplyToResources()` refreshes those resources, so the UI updates live.
- The chosen language is remembered in `%APPDATA%\UltraAudioEditor\language.txt` and reloaded on next launch.
- Users switch language from the **Language** menu (English / Srpski) in the menu bar.

### Adding a string

1. Add the key with all languages to the translation table (`LanguageManager.cs` or `Lang.cs`).
2. Reference it — Video: `Tag="key"` in XAML or `GetText("key")` in code; Audio: `{DynamicResource L_key}` in XAML or `Lang.T("key")` in code.
3. Never leave a literal in the UI. That includes `AutomationProperties.Name` — screen-reader users depend on those being translated too.

### Adding a language

- **Video:** add a new inner dictionary (e.g. `_translations["fr"]`) mirroring the English keys.
- **Audio:** extend each tuple in `Lang.cs` with the new column and widen the `T()` / `ApplyToResources()` selection, then add a menu entry.

---

## Questions

Open an issue with the label `question`. We read everything.
