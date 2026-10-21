# Third-party software and content

Ultra Video Editor is created by **Demir Ajvazi** and is licensed under the **GNU General Public License v3.0** (see `LICENSE`).
It builds on the work of many other projects. This file lists them with their licences. The licence texts are on each project's page.

## Libraries shipped with the program (NuGet packages)

| Component | Used for | Licence |
|---|---|---|
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) and LibVLCSharp.WPF | Video and sound preview | LGPL-2.1 or later |
| [VLC / libVLC](https://www.videolan.org/vlc/) (VideoLAN.LibVLC.Windows) | Playback engine | LGPL-2.1 or later (libVLC); some plugins GPL-2.0 or later |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) (ImageMagick) | Preparing pictures for the render | Apache-2.0 (Magick.NET), ImageMagick licence |
| [NAudio](https://github.com/naudio/NAudio) | Audio helpers | MIT |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) | Project files and settings | MIT |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | Local image classification | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and SkiaSharp.Views.WPF | Animations and drawing | MIT |
| [Microsoft.Windows.Compatibility](https://github.com/dotnet/runtime) | Windows APIs | MIT |
| [.NET 8 runtime](https://github.com/dotnet/runtime) (self-contained build) | Runtime | MIT |

## Tools the installer downloads, or the program uses

| Component | Used for | Licence |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) (GPL build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) | Render, mixing, analysis | GPL-3.0 (this build includes GPL components) |
| [VLC media player](https://www.videolan.org/vlc/) | Installed alongside for playback | GPL-2.0 or later |
| [Ollama](https://github.com/ollama/ollama) | Running the local AI models | MIT |
| [Faster-Whisper-XXL](https://github.com/Purfview/whisper-standalone-win) (optional) | Speech recognition for subtitles and lyric timing | See the project page |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | YouTube download dialog | Unlicense (public domain) |

## AI models (downloaded by the installer or by you, run locally)

| Model | Used for | Licence |
|---|---|---|
| [Qwen2.5](https://github.com/QwenLM/Qwen2.5) 7B and 14B | Director / query text model | Apache-2.0 |
| Qwen2.5 3B (chosen on weaker computers) | Text model | Qwen Research License (non-commercial use) |
| [Qwen2.5-VL](https://github.com/QwenLM/Qwen2.5-VL) 7B | Picture analysis and frame descriptions | Apache-2.0 |
| [moondream](https://github.com/vikhyat/moondream) | Picture descriptions on weaker computers | Apache-2.0 |
| [TinyLlama](https://github.com/jzhang38/TinyLlama) (very weak computers) | Text model | Apache-2.0 |
| [Whisper](https://github.com/openai/whisper) models (through Faster-Whisper-XXL) | Speech recognition | MIT |
| MobileNetV2 (`Models\mobilenetv2.onnx`, ONNX Model Zoo) | Image classification | Apache-2.0 |

If you use the program commercially, check the licence of the model your computer received: the installer picks the model by hardware, and the small Qwen2.5 3B model is not licensed for commercial use. Any model can be replaced with `ollama pull`.

## Online media services (only when you use them, with your own key)

| Service | Content licence |
|---|---|
| [Pixabay](https://pixabay.com/service/license-summary/) | Pixabay Content License |
| [Pexels](https://www.pexels.com/license/) | Pexels License |
| [Freesound](https://freesound.org/help/faq/#licenses) | Creative Commons, per sound (attribution may be required) |
| [Pollinations.ai](https://pollinations.ai/) | See the service's terms |
| Cloudflare Workers AI | See Cloudflare's terms |

Clips downloaded from these services keep their own licence; check it before publishing a video commercially.

## Ambient sound library

The ambient sound library the AI music video creator uses is **Adobe Audition's free sound effects**. It is not shipped with Ultra Video Editor: Adobe's licence allows using the sounds in your own productions, but not sharing the sound files as a standalone package. The installer (optional task) and *Help → Download the sound library from Adobe* download the categories Weather, Animals, Human Elements, Liquid and Water, Ambience 1 and Ambience 2 directly from Adobe's server, under Adobe's terms, and convert them to OGG on your computer: https://www.adobe.com/products/audition/offers/AdobeAuditionDLCSFX.html

---

Something missing or wrong in this list? Please open an issue at https://github.com/demirajvazi10-max/Ultra-Creative-suite/issues.
