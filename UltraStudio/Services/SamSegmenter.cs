using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace UltraStudio.Services
{
    /// <summary>
    /// SAM (Segment Anything, Meta AI) preko ONNX Runtime-a — piksel-precizno
    /// izdvajanje objekta iz slike, ne gruba aproksimacija. Očekuje standardni
    /// Meta SAM ONNX export (dva fajla: image encoder + mask decoder), isti
    /// format koji koriste desetine open-source SAM/ONNX projekata.
    ///
    /// MODEL FAJLOVI (nisu deo koda, moraju se preuzeti zasebno — isti princip
    /// kao FFmpeg/Whisper/Ollama u ostatku Ultra paketa):
    ///   %APPDATA%\UltraStudio\Models\sam_encoder.onnx  (~350 MB, ViT-B)
    ///   %APPDATA%\UltraStudio\Models\sam_decoder.onnx  (~4 MB)
    /// Izvor: https://github.com/vietanhdev/samexporter (uputstvo za izvoz iz
    /// zvaničnih Meta SAM težina), ili gotovi ONNX export-i sa Hugging Face-a
    /// (pretraga "segment anything onnx vit_b").
    /// </summary>
    public class SamSegmenter : IDisposable
    {
        private const int SAM_SIZE = 1024; // SAM enkoder uvek očekuje 1024x1024

        private InferenceSession? _encoder;
        private InferenceSession? _decoder;
        private float[]? _cachedEmbedding;
        private string? _cachedImagePath;
        private float _cachedScale;
        private int _cachedOrigW, _cachedOrigH;

        public static string ModelsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraStudio", "Models");
        public static string EncoderPath => Path.Combine(ModelsDir, "sam_encoder.onnx");
        public static string DecoderPath => Path.Combine(ModelsDir, "sam_decoder.onnx");

        public static bool ModelsAvailable => File.Exists(EncoderPath) && File.Exists(DecoderPath);

        private void EnsureLoaded()
        {
            if (!ModelsAvailable)
                throw new FileNotFoundException(
                    $"SAM model files not found.\n\nExpected at:\n{EncoderPath}\n{DecoderPath}\n\n" +
                    "Download instructions: see samexporter (github.com/vietanhdev/samexporter) or search " +
                    "Hugging Face for a ready-made 'segment anything onnx vit_b' export.");

            _encoder ??= new InferenceSession(EncoderPath);
            _decoder ??= new InferenceSession(DecoderPath);
        }

        /// <summary>
        /// Računa embedding slike (skup korak, ~1-3s na CPU-u). Keširano po putanji
        /// fajla — ponovljeni pozivi za istu sliku (npr. više klikova/predloga)
        /// ne ponavljaju enkodiranje.
        /// </summary>
        public async Task EnsureEmbeddingAsync(string imagePath)
        {
            EnsureLoaded();
            if (_cachedImagePath == imagePath && _cachedEmbedding != null) return;

            await Task.Run(() =>
            {
                using var img = new MagickImage(imagePath);
                _cachedOrigW = (int)img.Width;
                _cachedOrigH = (int)img.Height;

                // SAM-ov transform: dugu stranu skaliraj na 1024, kraću srazmerno,
                // pa popuni ostatak (padding) nulama do tačno 1024x1024.
                _cachedScale = (float)SAM_SIZE / Math.Max(_cachedOrigW, _cachedOrigH);
                int newW = (int)Math.Round(_cachedOrigW * _cachedScale);
                int newH = (int)Math.Round(_cachedOrigH * _cachedScale);

                img.FilterType = FilterType.Triangle;
                img.Resize((uint)newW, (uint)newH);

                var input = new DenseTensor<float>(new[] { 1, 3, SAM_SIZE, SAM_SIZE });
                // ImageNet-stil normalizacija koju SAM koristi (na skali piksela 0-255,
                // ne 0-1) — mean/std su fiksne konstante iz zvaničnog SAM preprocesa.
                float[] mean = { 123.675f, 116.28f, 103.53f };
                float[] std = { 58.395f, 57.12f, 57.375f };

                using var pixels = img.GetPixels();
                for (int y = 0; y < newH; y++)
                {
                    for (int x = 0; x < newW; x++)
                    {
                        var p = pixels.GetPixel(x, y);
                        var rgb = p.ToColor()!;
                        input[0, 0, y, x] = ((float)rgb.R - mean[0]) / std[0];
                        input[0, 1, y, x] = ((float)rgb.G - mean[1]) / std[1];
                        input[0, 2, y, x] = ((float)rgb.B - mean[2]) / std[2];
                    }
                    // ostatak reda (padding zona) ostaje 0 — DenseTensor je već
                    // nula-inicijalizovan, ništa dodatno ne treba raditi.
                }

                var inputs = new[] { NamedOnnxValue.CreateFromTensor("input_image", input) };
                using var results = _encoder!.Run(inputs);
                _cachedEmbedding = results.First(r => r.Name == "image_embeddings")
                    .AsTensor<float>().ToArray();
                _cachedImagePath = imagePath;
            });
        }

        /// <summary>
        /// Vraća bool masku (true = deo objekta) u ORIGINALNOJ rezoluciji slike,
        /// na osnovu jedne tačke unutar objekta (u originalnim koordinatama piksela).
        /// </summary>
        public async Task<bool[,]> SegmentFromPointAsync(int pointX, int pointY)
        {
            if (_cachedEmbedding == null)
                throw new InvalidOperationException("Call EnsureEmbeddingAsync first.");

            return await Task.Run(() =>
            {
                // Tačka mora da se skalira ISTIM faktorom kojim je slika skalirana
                // pre enkodiranja — nema paddinga u samoj skali, padding je posle.
                float px = pointX * _cachedScale;
                float py = pointY * _cachedScale;

                var embeddingTensor = new DenseTensor<float>(_cachedEmbedding, new[] { 1, 256, 64, 64 });
                var pointCoords = new DenseTensor<float>(new[] { 1, 1, 2 });
                pointCoords[0, 0, 0] = px; pointCoords[0, 0, 1] = py;
                var pointLabels = new DenseTensor<float>(new[] { 1, 1 });
                pointLabels[0, 0] = 1f; // 1 = tačka je NA objektu (foreground)
                var maskInput = new DenseTensor<float>(new[] { 1, 1, 256, 256 });
                var hasMaskInput = new DenseTensor<float>(new[] { 1 });
                hasMaskInput[0] = 0f;
                var origSize = new DenseTensor<float>(new[] { 2 });
                origSize[0] = _cachedOrigH; origSize[1] = _cachedOrigW;

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", embeddingTensor),
                    NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                    NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                    NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                    NamedOnnxValue.CreateFromTensor("orig_im_size", origSize),
                };

                using var results = _decoder!.Run(inputs);
                var masksResult = results.First(r => r.Name == "masks").AsTensor<float>();
                var iouResult = results.First(r => r.Name == "iou_predictions").AsTensor<float>();

                // SAM decoder standardno predlaže 3 kandidat-maske; uzmi onu sa
                // najvišim IoU predviđanjem (najpouzdaniju), ne prvu po redu.
                int bestIdx = 0; float bestIou = float.MinValue;
                for (int i = 0; i < iouResult.Dimensions[1]; i++)
                    if (iouResult[0, i] > bestIou) { bestIou = iouResult[0, i]; bestIdx = i; }

                var mask = new bool[_cachedOrigH, _cachedOrigW];
                for (int y = 0; y < _cachedOrigH; y++)
                    for (int x = 0; x < _cachedOrigW; x++)
                        mask[y, x] = masksResult[0, bestIdx, y, x] > 0f;

                return mask;
            });
        }

        /// <summary>Upisuje PNG sa transparentnom pozadinom van maske — pravi "cutout".</summary>
        public static void ExportCutout(string originalPath, bool[,] mask, string outputPath)
        {
            using var img = new MagickImage(originalPath);
            img.Alpha(AlphaOption.Set);
            using var pixels = img.GetPixels();

            int h = mask.GetLength(0), w = mask.GetLength(1);
            for (int y = 0; y < (int)img.Height && y < h; y++)
            {
                for (int x = 0; x < (int)img.Width && x < w; x++)
                {
                    if (!mask[y, x])
                    {
                        var p = pixels.GetPixel(x, y);
                        var c = p.ToColor()!;
                        pixels.SetPixel(x, y, new ushort[] { c.R, c.G, c.B, 0 });
                    }
                }
            }
            img.Write(outputPath);
        }

        public void Dispose()
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
        }
    }
}
