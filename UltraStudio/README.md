# Ultra Studio — Accessible Photo Editor and Designer

> Part of the Ultra Creative Suite. A photo editor, graphic design and page layout tool built around the keyboard and the screen reader from day one, with a full visual mode for mouse users alongside it.

**Version 2.0.0**

---

## What is this?

Ultra Studio is a WPF desktop application (.NET 8) for editing photos and making simple graphic designs — social media posts, thumbnails, posters, flyers — usable equally well by blind and sighted users. Version 2.0 takes the everyday Photoshop toolset (selections, crop and resize, adjustments and looks, filters, retouching, background removal, layers with blend modes and styles) and makes every part of it work from the keyboard, with spoken feedback for everything that happens. It also lays out multi-page documents — brochures, booklets, books — with text frames, page numbers and PDF export, and includes Serbian typography and script tools for proofreading.

- **JAWS Mode**: adjustments and layers live in native, keyboard-navigable Win32 lists. Left/Right arrows change a value, Enter types an exact number, Delete resets it.
- **Visual Mode**: the same adjustments as mouse-friendly sliders; click a layer on the preview to select it and drag it; drag on the photo to make a selection. Toggle with **Alt+W**.
- **Startup mode is auto-detected** (JAWS, NVDA, Narrator → JAWS Mode, otherwise Visual Mode).
- Both modes edit the same project — two views onto the same data.

---

## Features

### Projects, undo and history
- **Project files (.ustudio)** — a single file with the photo, every layer, adjustments and the selection. Move it to another computer and keep working. Double-click opens it (installer registers the extension).
- **Undo / Redo** (Ctrl+Z / Ctrl+Y) for every change, up to 100 steps, and a **History** window (Ctrl+H) that lists every step by name — pick one and jump straight back to it.
- Destructive edits (crop, filters, retouching, background) are written as new files in a session folder, so undo is instant and lossless.

### Selection
- **Rectangle or ellipse by numbers** (pixels or percent), **ready areas** (halves, thirds, center, centered square or circle, top 40% for the sky), select all / deselect / invert, **feather** (soft edge).
- **AI: select subject** and **AI: select by description** ("the dog", "the red car") — local AI finds the object, Segment Anything outlines it pixel-precisely.
- **Color range** like Photoshop: reds, oranges, yellows, greens, cyans, blues, purples, magentas, highlights, midtones, shadows, skin tones, or any custom color, with fuzziness.
- **Selection info** is spoken: shape, where it is (e.g. "top left"), size and how much of the image it covers.
- **AI: what is in the selection?** — describes just the selected part.
- Everything that changes pixels respects the selection: filters, fill, delete, auto tone, applying adjustments.

### Image
- **Crop** to selection, to an **aspect ratio** (1:1, 4:5, 16:9, 9:16, 3:2, 4:3, 21:9, 1.91:1…) — optionally centered on an object you describe ("keep the child in the center") — or by numbers.
- **Image size** (pixels, percent, keep proportions) and **canvas size** with anchor and fill color. Layers scale and move with the image.
- **Rotate** 90° left/right, 180°, any angle; **flip** horizontal/vertical. **Flatten** merges everything into one image.

### Adjustments (non-destructive)
Exposure, brightness, contrast, highlights, shadows, midtones (gamma), temperature, tint, vibrance, saturation, hue shift, clarity, sharpen, blur, vignette, fade (matte), posterize, threshold, grayscale, sepia, invert. Always re-applied from the original, so repeated edits never degrade the photo. **Apply adjustments permanently** bakes them into the pixels (only inside the selection, if there is one).

- **Looks** (Ctrl+L) — 20 presets (Natural, Vivid, Warm, Cool, Golden hour, Matte, Film, Cinematic, Dramatic, Moody, Bright and airy, Soft portrait, B&W, B&W high contrast, Noir, Sepia, Vintage, Pop, Night…). Arrow through them and each one is shown on the photo while its description is read out; an AI button describes how the photo looks with it. Enter keeps it, Escape returns to how it was.
- **Auto tone, auto contrast, auto color** (white balance).
- **AI suggestions** — the AI reviews the photo adjustment by adjustment and proposes concrete changes, each confirmed with a Yes/No dialog.

### Filters
Gaussian blur, motion blur, spin blur, unsharp mask, add noise (grain), reduce noise, emboss, find edges, oil paint, pencil sketch, charcoal, pixelate, solarize, swirl, wave, pinch — each with a strength value; **Ctrl+F** repeats the last one.

### Retouching
- **Remove object** — describe what should disappear ("the trash can", "the wire"); AI finds it and the area is filled from its surroundings. Best for smaller objects; you are warned before filling a very large area.
- **Remove the selected area**, **spot healing** at a point with a radius.
- **Red-eye removal** — AI finds the eyes, or works inside a selection.

### AI background tools
- **Remove background**, **replace it with a color or an image**, **blur the background** (portrait effect).
- **Extract an object as a layer** — the cut-out becomes a new layer at the same place (and a PNG copy is saved next to the original).
- With an active selection, these tools use it as the subject instead of asking the AI.
- **Describe this image** and **describe how the photo looks** (light, colors, mood). Answers come in the interface language.

### Layers and graphic design
- Text, shape (rectangle with optional rounded corners, ellipse, line), image and **gradient** layers (linear at any angle or radial, each end with its own opacity — the classic dark fade behind text on a photo).
- **Blend modes**: normal, multiply, screen, overlay, soft light, hard light, darken, lighten, color dodge, color burn, difference, exclusion, hue, saturation, color, luminosity.
- **Layer styles**: drop shadow, outline (stroke) and outer glow, for any layer type. **Rotation** and opacity per layer.
- **Text**: several lines, left/center/right alignment, line spacing, any installed font, and a box that fits the text automatically — so centering is exact.
- New layers are placed by words ("top center", "bottom right") instead of coordinates.
- **Arrange** without a mouse: center horizontally/vertically/both, align to any edge, fill the canvas, **Alt+arrows** to move 10 px (**Alt+Shift+arrows** 1 px) with the new position spoken, and "where is the layer?" at any time. The layer list shows each layer's position in words.
- **Canvas presets**: Instagram (square, portrait, story/Reels), Facebook post and cover, YouTube thumbnail and banner, X, LinkedIn, Full HD, 4K, A4 and A3 for print, poster, flyer, business card.

### Design check (F7)
A spoken report of the whole design: canvas size and orientation, every layer from the top with its position in words, size as a share of the canvas, centering, rotation, blend mode and styles; text sizes from largest to smallest; **WCAG contrast of every text against what is actually behind it** (measured on the rendered image, worst spot counts); and warnings for text that is hard to read, layers that go outside the canvas, text too close to the edge or too small, overlapping text, empty text and missing image files.

**F6** answers "where am I": canvas, selection, selected layer, active look and unsaved changes.

### Export and batch processing
- **Export** (Ctrl+E) to JPG, PNG, WebP, TIFF or BMP with quality control, at the original size, a long-side limit, or an exact social-media size (filled and center-cropped). Metadata such as GPS location is removed.
- **Batch processing** of a whole folder (optionally with subfolders): a look or the current adjustments, size, format, quality, a text watermark at any of nine positions with opacity, and a file-name suffix. Originals are never overwritten. Progress is spoken every 10%, not after every file.

### Multi-page documents and PDF
- **Pages**: new page (same or another size), duplicate, delete, move earlier/later, next/previous (**Ctrl+PageDown / Ctrl+PageUp**) and a list of pages (**Ctrl+G**) where each page is described by its first line of text. Every page has its own photo, layers and adjustments; undo and history cover the whole document.
- **Text frames**: a text layer can be a frame — lines break at the frame's width, the height is fixed, alignment can be left, center, right or **justified**. Lines that don't fit are hidden and reported (status, design check).
- **Place long text across pages**: open a Word, PDF, EPUB or TXT file (or paste text) and it is laid out in frames on as many pages as needed, with the font, size in points, line spacing, alignment and margins in millimeters you choose, optional page numbers, and Serbian typography applied first.
- **Page numbers**: "1", "Page 1", "1 / 10" or "— 1 —", bottom center, bottom right, bottom outside (book style) or top right; re-running replaces them, and they can be removed.
- **Print sizes**: A4, A5, A3, B5, a 15 × 22 cm book, flyer, poster and business card presets carry their print resolution; **Document > Print resolution** sets the DPI that defines the physical page size.
- **PDF export** (**Ctrl+Shift+E**): all pages in one PDF. Plain text goes in as **real text** (selectable, searchable, read by screen readers, Serbian Latin and Cyrillic included), placed in reading order over the page image; text with styles, rotation or transparency, or in a font without a .ttf file, goes in as part of the image and you are told how many. Image quality: print (300 dpi), standard (200) or small (150).
- **Export all pages as images** into a folder (page_001.png, page_002.png…).

### Serbian typography, script and proofreading
- **Serbian typography** (no AI, instant): quotes „like this“ (or English “like this”), apostrophe ’, the dash – between words, — in dialogue, 10–20 for ranges, double spaces, spaces before punctuation, missing space after a comma, … for three dots, and non-breaking spaces so one-letter words (i, u, a, s, k, v, o) and units (10 km, 5 %) never stay alone at the end of a line. Links and e-mail addresses are left alone.
- **Latin ↔ Cyrillic** by the rules of Serbian orthography (lj, nj, dž as single letters, with exceptions such as konjunkcija and nadživeti); links, e-mails, abbreviations and words with q, w, x, y stay in Latin; you are warned about fonts that may lack Cyrillic.
- Apply to the selected text layer, all text on the page, or the whole document — or to a **document file** (Word, PDF, TXT…), saved as TXT, DOCX or RTF.
- **AI proofreading** follows the Pravopis srpskoga jezika: č/ć and dž/đ, ije/je with dialect kept (and mixing flagged), "ne" with verbs, capitalization, commas, enclitics, numbers and dates; it keeps the text's script, and reports spelling, grammar, punctuation, typography and style separately. The proofreading window also has a **Serbian typography** button.
- **Proofread all text in the design** walks through every text layer on every page, one dialog at a time.
- Proofread a whole document (PDF, DOCX, DOC, RTF, ODT, EPUB, HTML, SRT/VTT, TXT/MD) or a single text layer, as before.

---

## Keyboard

| Shortcut | Action |
|---|---|
| Ctrl+N / Ctrl+O | New canvas / open photo or project |
| Ctrl+S / Ctrl+Shift+S | Save project / save project as |
| Ctrl+E | Export image |
| Ctrl+Z / Ctrl+Y / Ctrl+H | Undo / redo / history |
| Ctrl+A / Ctrl+D / Ctrl+Shift+I | Select all / deselect / invert selection |
| Ctrl+J / Ctrl+Shift+J | Copy / cut selection to a new layer |
| Ctrl+PageDown / Ctrl+PageUp | Next / previous page |
| Ctrl+G | List of pages |
| Ctrl+Shift+E | Export PDF |
| Ctrl+L | Looks |
| Ctrl+F | Repeat last filter |
| F6 / F7 | Quick status / design check |
| Alt+arrows, Alt+Shift+arrows | Move the selected layer 10 px / 1 px |
| Alt+W | JAWS Mode / Visual Mode |
| F1 | Shortcut list |
| Ctrl+Shift+L | Diagnostic log |

In the adjustments list: Left/Right change the value (Shift for bigger steps), Enter types a value, Delete resets, Space toggles on/off items. In the layers list: Enter opens properties, Space shows/hides, Delete removes. Everything else is in the menus, and every menu item shows its shortcut.

---

## Requirements

| Component | Version | Note |
|---|---|---|
| Windows | 10 / 11 | WPF application |
| .NET | 8.0 | `net8.0-windows` |
| Ollama | Any | Local AI server, for descriptions, suggestions and AI selection |
| Qwen2.5-VL | Via Ollama | `ollama pull qwen2.5vl:latest` |
| SAM ONNX models | Separate download | Optional — pixel-precise AI selection (see below) |

### SAM model files (pixel-precise AI selection)

Not bundled (the encoder alone is ~350 MB). Expected at:

```
%APPDATA%\UltraStudio\Models\sam_encoder.onnx
%APPDATA%\UltraStudio\Models\sam_decoder.onnx
```

Export instructions: [github.com/vietanhdev/samexporter](https://github.com/vietanhdev/samexporter), or search Hugging Face for a ready-made "segment anything onnx vit_b" export. Without these files every AI tool still works: the AI finds the object's bounding box and the selection becomes a soft ellipse around it (you are told when that happens).

### NuGet Packages

```xml
<PackageReference Include="Magick.NET-Q16-AnyCPU" Version="14.15.0" />
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.19.0" />
<PackageReference Include="NPOI" Version="2.7.2" />
<PackageReference Include="PdfPig" Version="0.1.17-alpha-202608250026-fd3a9" />
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

- Native Windows lists for adjustments and layers; native dialogs for every choice; every field in every dialog has a name for the screen reader.
- Every action ends with a short spoken confirmation in the status bar (live region) — what changed, the new value, or where a layer now is. Long operations say when they start and when they finish; nothing chatters in between.
- Positions are given in words (top left, center, bottom right) alongside pixel values, and the design check reads out the whole layout.
- Looks, selections and AI results can be explored without seeing the preview, and the preview itself is fully usable with a mouse.
- No feature requires a mouse; no feature requires a screen reader.

---

## Known Limitations

- **Remove object** uses a fast fill from the surroundings (pyramid interpolation with matching grain), not generative AI. It is very good for wires, spots, small objects and text on walls, and visibly smeared on large areas.
- **PDF text** is real, readable text, but the PDF is not a fully tagged (PDF/UA) document — there are no heading or list tags yet.
- **Text frames** are not linked: after "Place long text across pages", editing one page's text does not re-flow the following pages. There is no automatic hyphenation.
- **Latin → Cyrillic** follows the rules and a list of known exceptions; foreign names written in Serbian Latin are converted too, so check names once.
- **Text layers** are drawn with ImageMagick; a font must be installed on Windows to be used. Rotated layers are checked by their unrotated box in the design check.
- **Magick.NET** has occasionally had security advisories for the underlying ImageMagick library; it is kept on the latest available version.
- **SAM selection** depends on the AI's point estimate; if it picks the wrong object, describe it more specifically ("the woman on the left").

---

## Supporting the project

Ultra Studio is free and will stay free. If you'd like to support its development:

- **By card on [Ko-fi](https://ko-fi.com/ultracreativesuite)** — no account needed: type an amount, press *Donate* and pay with a Visa or Mastercard (credit or debit) or PayPal. The easiest way from abroad.
- **[GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max)** — monthly or one-time, paid with an ordinary payment card (no PayPal needed); needs a free GitHub account.
- **Bank transfer from Serbia** — recipient Demir Ajvazi, account `265-0000006185877-28`, Raiffeisen banka, purpose *Donacija za Ultra programe*. Meant for domestic transfers; from abroad, Ko-fi costs far less in fees.

The same guide is inside the app: **Help > How to support this project**, in English and Serbian, with buttons to open Ko-fi or GitHub Sponsors and to copy the account number.

## Author

Created by **Demir Ajvazi**. Support development on [GitHub Sponsors](https://github.com/sponsors/demirajvazi10-max).

## License

GPL-3.0 — see the repository's [LICENSE](../LICENSE) file.

© 2026 Demir Ajvazi. Part of the Ultra Creative Suite.
