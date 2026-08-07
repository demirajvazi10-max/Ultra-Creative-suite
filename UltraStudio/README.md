# Ultra Studio — Accessible Photo Editor

> Part of the Ultra Creative Suite. A photo editor built for screen reader accessibility from day one, with a full visual mode for sighted users alongside it.

---

## What is this?

Ultra Studio is a WPF desktop application (.NET 8) for editing photos — adjustments, AI-assisted suggestions, and precise AI-powered object extraction — usable equally well by blind and sighted users.

- **JAWS Mode**: every adjustment lives in a single keyboard-navigable list (native Win32 ListView), Enter/F2 to edit a value, Space to toggle on/off options, Shift+F10 for menus.
- **Visual Mode**: the same adjustments as real, mouse-friendly WPF sliders and checkboxes — for sighted users who expect a standard photo-editor feel. Toggle between the two anytime with **Alt+W**.
- Both modes write to the same underlying project — nothing is duplicated, they're just two views onto the same data.

---

## Features

- Non-destructive adjustments: Brightness, Contrast, Saturation, Sharpen, Blur, Rotate, Grayscale, Sepia, Flip Horizontal/Vertical — always applied fresh from the original, so repeated edits never compound errors
- Live preview as you adjust
- **AI image description** (Qwen2.5-VL via Ollama, 100% local, no API key, no cloud) — detailed, accurate descriptions for anyone who can't see the image
- **AI editing suggestions** — the AI reviews the image adjustment-by-adjustment and proposes specific, concrete changes (e.g. "increase contrast by 15 — the sky looks flat"), each presented as a native Yes/No dialog, applied only on confirmation
- **AI-guided object extraction (SAM)** — describe what to extract ("the child", "the car") in plain text; the AI locates it, then Meta's Segment Anything model produces a pixel-precise cutout — not a rough approximation
- **Layers (graphic design)** — text, shape (rectangle/ellipse/line), and image layers composited on top of the base photo, or on a blank canvas of any size (**Layers > New canvas...**). Each layer has position, size, opacity and visibility, edited through the same JAWS-list / visual-panel duality as the adjustments above: a native ListView (**Enter** for properties, **Space** to toggle visibility, **Delete** to remove) in JAWS Mode, and a mouse-friendly stacked panel with per-layer sliders and an Edit button in Visual Mode. Reorder with **Layers > Move layer up/down**, duplicate with **Layers > Duplicate layer**.

---

## Requirements

| Component | Version | Note |
|---|---|---|
| Windows | 10 / 11 | WPF application |
| .NET | 8.0 | `net8.0-windows` |
| Ollama | Any | Local LLM server, for AI description/suggestions |
| Qwen2.5-VL | Via Ollama | `ollama pull qwen2.5vl:latest` |
| SAM ONNX models | Separate download | See below — required only for object extraction |

### SAM model files (object extraction)

Not bundled with the app (the encoder alone is ~350 MB). Expected at:

```
%APPDATA%\UltraStudio\Models\sam_encoder.onnx
%APPDATA%\UltraStudio\Models\sam_decoder.onnx
```

Export instructions: [github.com/vietanhdev/samexporter](https://github.com/vietanhdev/samexporter), or search Hugging Face for a ready-made "segment anything onnx vit_b" export. Without these files, every other feature works normally — you'll just get a clear message if you try to extract an object.

### NuGet Packages

```xml
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="14.15.0" />
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.19.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
<PackageReference Include="Ookii.Dialogs.Wpf" Version="5.0.1" />
```

---

## Installation

```bash
git clone https://github.com/demirajvazi10-max/Ultra-Creative-suite.git
cd Ultra-Creative-suite/UltraStudio
dotnet restore
dotnet build -c Release
```

Start Ollama and pull the vision model:
```bash
ollama pull qwen2.5vl:latest
```

---

## Accessibility

Built around a single, linearly navigable list of adjustments (native Windows ListView) in JAWS Mode — arrow keys move through every option, Enter/F2 edits a value, Space toggles on/off items. All AI suggestions arrive as native Windows dialogs (MessageBox), which screen readers announce reliably without any extra work. No feature requires a mouse.

---

## Known Limitations

- **Magick.NET**: like the other apps in the Ultra suite, this depends on Magick.NET, which has occasionally had security advisories for its underlying ImageMagick C library. Kept on the latest available patched version; watch for updates.
- **SAM extraction**: relies on precise coordinate math between the AI's point estimate and the model's expected input space. If a cutout looks misaligned, that's the first place to check.

---

## Author

Created by **Demir Ajvazi**.

© 2026 Demir Ajvazi. Part of the Ultra Creative Suite.
