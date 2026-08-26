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
    ///
    /// RAZLIČITI export-i pakuju ulaze/izlaze različito — neki uključuju batch
    /// dimenziju (npr. input_image kao [1,3,1024,1024]), neki je izostave
    /// ([3,1024,1024]); povrh toga, neki stavljaju kanale PRVO (CHW), a neki
    /// POSLEDNJE (HWC, [1024,1024,3]). Umesto da nagađamo (i onda krpimo
    /// grešku po grešku, model po model), OVDE se i rang i raspored svakog
    /// tenzora čitaju direktno iz metapodataka samog modela
    /// (session.InputMetadata) — kod radi ispravno bez obzira koju konkretnu
    /// export varijantu Demir preuzme.
    /// </summary>
    public class SamSegmenter : IDisposable
    {
        private const int SAM_SIZE = 1024; // SAM enkoder uvek očekuje 1024x1024

        // Gornja granica za rezoluciju maske koju TRAŽIMO od dekodera. Telefonske
        // fotografije lako imaju 4000+ piksela na dužoj strani — tražiti masku u
        // toj punoj rezoluciji znači da ONNX Runtime mora da alocira ogromne
        // native bafere (maska + međurezultati po kandidatu) za svaki poziv.
        // To je najverovatniji uzrok potpunog gašenja aplikacije BEZ ijedne
        // poruke o grešci koju je Demir prijavio: native alokacija koja ne uspe
        // (std::bad_alloc i slično) ruši ceo proces trenutno, van domašaja bilo
        // kog .NET try/catch bloka ili DispatcherUnhandledException hendlera.
        // Zato tražimo masku u ograničenoj rezoluciji, pa je pri izvozu (u
        // ExportCutout) mapiramo nazad na pravu rezoluciju originalne slike.
        private const int MAX_MASK_SIZE = 1536;

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

            if (_encoder == null)
            {
                DebugLog.Write("SamSegmenter: učitavam encoder session...");
                _encoder = new InferenceSession(EncoderPath);
                DebugLog.Write("SamSegmenter: encoder session učitan.");
            }
            if (_decoder == null)
            {
                DebugLog.Write("SamSegmenter: učitavam decoder session...");
                _decoder = new InferenceSession(DecoderPath);
                DebugLog.Write("SamSegmenter: decoder session učitan.");
            }
        }

        /// <summary>
        /// Upakuje ravan (flat) niz vrednosti u tenzor čiji rang ODGOVARA onome
        /// što TAJ KONKRETNI model stvarno očekuje za dati ulaz — proverava se
        /// direktno iz session.InputMetadata, ne pretpostavlja se unapred.
        /// Ukupan broj elemenata (proizvod dimenzija) ostaje isti bez obzira da
        /// li je prisutna "batch" dimenzija veličine 1 na početku, tako da je
        /// ovo bezbedno za bilo koju od dve uobičajene konvencije.
        /// </summary>
        private static DenseTensor<float> WrapForModel(
            InferenceSession session, string inputName, float[] flatData, int[] canonicalShapeWithBatch)
        {
            int expectedRank = session.InputMetadata.TryGetValue(inputName, out var meta)
                ? meta.Dimensions.Length
                : canonicalShapeWithBatch.Length;

            int[] shape =
                expectedRank == canonicalShapeWithBatch.Length - 1
                    ? canonicalShapeWithBatch.Skip(1).ToArray()   // model nema batch dimenziju
                    : canonicalShapeWithBatch;                     // model ima (ili nepoznato -> podrazumevano)

            return new DenseTensor<float>(flatData, shape);
        }

        /// <summary>
        /// Za sliku specifično: ne razlikuju se export-i samo po tome da li imaju
        /// batch dimenziju, nego i po REDOSLEDU osa — neki očekuju kanal-prvo
        /// (NCHW/CHW), neki kanal-poslednje (NHWC/HWC). Ovde se to čita direktno
        /// iz metapodataka modela: traži se osa čija je deklarisana veličina
        /// TAČNO 3 (RGB kanali su skoro uvek statička, ne dinamička dimenzija u
        /// exportu, za razliku od visine/širine/batch-a koji su često -1).
        /// Ako se ne pronađe (sve ose dinamične), podrazumeva se kanal-prvo, isto
        /// ponašanje kao pre.
        /// </summary>
        private static (int[] shape, bool channelsFirst) ResolveImageShape(
            InferenceSession session, string inputName, int size)
        {
            if (session.InputMetadata.TryGetValue(inputName, out var meta))
            {
                var d = meta.Dimensions;
                int chAxis = Array.IndexOf(d, 3);
                bool hasBatch = d.Length == 4;

                if (chAxis >= 0)
                {
                    bool channelsFirst = hasBatch ? chAxis == 1 : chAxis == 0;
                    int[] shape = hasBatch
                        ? (channelsFirst ? new[] { 1, 3, size, size } : new[] { 1, size, size, 3 })
                        : (channelsFirst ? new[] { 3, size, size } : new[] { size, size, 3 });
                    return (shape, channelsFirst);
                }

                // Kanal nije pronađen po vrednosti (sve dimenzije dinamične) —
                // bar broj osa (rang) i dalje pouzdano znamo.
                return hasBatch
                    ? (new[] { 1, 3, size, size }, true)
                    : (new[] { 3, size, size }, true);
            }

            return (new[] { 1, 3, size, size }, true); // podrazumevano ako nema metapodataka uopšte
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
                DebugLog.Write($"SamSegmenter: pripremam ulaznu sliku za encoder ({imagePath})...");
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

                // Raspored (kanal-prvo ili kanal-poslednje) i rang čitaju se iz
                // stvarnih metapodataka ENKODERA — različiti SAM ONNX export-i
                // se razlikuju i po jednom i po drugom. Videti ResolveImageShape.
                float[] mean = { 123.675f, 116.28f, 103.53f };
                float[] std = { 58.395f, 57.12f, 57.375f };
                var flat = new float[3 * SAM_SIZE * SAM_SIZE]; // nula-inicijalizovano -> padding zona već 0
                var (imageShape, channelsFirst) = ResolveImageShape(_encoder!, "input_image", SAM_SIZE);
                // Postavljamo Depth na 8 da Magick.NET Q16 vrati tačno 1 bajt po kanalu (0..255),
                // umesto 16-bitnih vrednosti (2 bajta po kanalu).
                img.Depth = 8;
                byte[] raw = img.ToByteArray(MagickFormat.Rgb);
                int planeSize = SAM_SIZE * SAM_SIZE;
                for (int y = 0; y < newH; y++)
                {
                    int rowBase = y * newW * 3;
                    int planeRow = y * SAM_SIZE;
                    for (int x = 0; x < newW; x++)
                    {
                        int idx = rowBase + x * 3;
                        float r = (raw[idx] - mean[0]) / std[0];
                        float g = (raw[idx + 1] - mean[1]) / std[1];
                        float b = (raw[idx + 2] - mean[2]) / std[2];

                        if (channelsFirst)
                        {
                            // [3, H, W] (bez obzira na batch dimenziju): kanal je
                            // "spoljna" ravan, pa je razmak između kanala planeSize.
                            int pix = planeRow + x;
                            flat[0 * planeSize + pix] = r;
                            flat[1 * planeSize + pix] = g;
                            flat[2 * planeSize + pix] = b;
                        }
                        else
                        {
                            // [H, W, 3]: tri kanala su susedna u memoriji po pikselu.
                            int pix = (planeRow + x) * 3;
                            flat[pix] = r;
                            flat[pix + 1] = g;
                            flat[pix + 2] = b;
                        }
                    }
                }

                var input = new DenseTensor<float>(flat, imageShape);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor("input_image", input) };
                DebugLog.Write("SamSegmenter: pokrećem encoder.Run()...");
                using var results = _encoder!.Run(inputs);
                DebugLog.Write("SamSegmenter: encoder.Run() završen.");
                _cachedEmbedding = results.First(r => r.Name == "image_embeddings")
                    .AsTensor<float>().ToArray();
                _cachedImagePath = imagePath;
            });
        }

        /// <summary>
        /// Vraća bool masku (true = deo objekta) na osnovu jedne tačke unutar
        /// objekta (u originalnim koordinatama piksela), zajedno sa skalom koja
        /// mapira koordinate originalne slike na koordinate maske — videti
        /// MAX_MASK_SIZE iznad za razlog zašto maska NIJE u punoj rezoluciji.
        /// </summary>
        public async Task<(bool[,] mask, float maskScale)> SegmentFromPointAsync(int pointX, int pointY)
        {
            if (_cachedEmbedding == null)
                throw new InvalidOperationException("Call EnsureEmbeddingAsync first.");

            return await Task.Run(() =>
            {
                // Tačka mora da se skalira ISTIM faktorom kojim je slika skalirana
                // pre enkodiranja — nema paddinga u samoj skali, padding je posle.
                float px = pointX * _cachedScale;
                float py = pointY * _cachedScale;

                float maskScale = Math.Min(1f, (float)MAX_MASK_SIZE / Math.Max(_cachedOrigW, _cachedOrigH));
                int maskW = Math.Max(1, (int)Math.Round(_cachedOrigW * maskScale));
                int maskH = Math.Max(1, (int)Math.Round(_cachedOrigH * maskScale));

                var embeddingTensor = WrapForModel(_decoder!, "image_embeddings", _cachedEmbedding, new[] { 1, 256, 64, 64 });
                var pointCoords = WrapForModel(_decoder!, "point_coords", new[] { px, py }, new[] { 1, 1, 2 });
                var pointLabels = WrapForModel(_decoder!, "point_labels", new[] { 1f }, new[] { 1, 1 });
                var maskInput = WrapForModel(_decoder!, "mask_input", new float[256 * 256], new[] { 1, 1, 256, 256 });
                var hasMaskInput = WrapForModel(_decoder!, "has_mask_input", new[] { 0f }, new[] { 1 });
                var origSize = WrapForModel(_decoder!, "orig_im_size", new[] { (float)maskH, (float)maskW }, new[] { 2 });

                var inputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("image_embeddings", embeddingTensor),
                    NamedOnnxValue.CreateFromTensor("point_coords", pointCoords),
                    NamedOnnxValue.CreateFromTensor("point_labels", pointLabels),
                    NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
                    NamedOnnxValue.CreateFromTensor("has_mask_input", hasMaskInput),
                    NamedOnnxValue.CreateFromTensor("orig_im_size", origSize),
                };

                DebugLog.Write($"SamSegmenter: pokrećem decoder.Run() (maska {maskW}x{maskH})...");
                using var results = _decoder!.Run(inputs);
                DebugLog.Write("SamSegmenter: decoder.Run() završen.");
                var masksResult = results.First(r => r.Name == "masks").AsTensor<float>();
                var iouResult = results.First(r => r.Name == "iou_predictions").AsTensor<float>();

                // Čita se generički prema STVARNOM rangu izlaza (3 ili 4 dimenzije),
                // ne prema pretpostavci — isti razlog kao WrapForModel iznad.
                bool masksHaveBatch = masksResult.Dimensions.Length == 4;
                int candOffset = masksHaveBatch ? 1 : 0;
                int outH = masksResult.Dimensions[candOffset + 1];
                int outW = masksResult.Dimensions[candOffset + 2];
                if (outH < maskH || outW < maskW)
                    throw new InvalidOperationException(
                        $"SAM decoder returned mask {outW}x{outH}, expected at least {maskW}x{maskH}. " +
                        "The SAM encoder/decoder model files may be mismatched or corrupted.");

                bool iouHasBatch = iouResult.Dimensions.Length == 2;
                int candCount = iouHasBatch ? iouResult.Dimensions[1] : iouResult.Dimensions[0];

                // SAM decoder standardno predlaže 3 kandidat-maske; uzmi onu sa
                // najvišim IoU predviđanjem (najpouzdaniju), ne prvu po redu.
                int bestIdx = 0; float bestIou = float.MinValue;
                for (int i = 0; i < candCount; i++)
                {
                    float v = iouHasBatch ? iouResult[0, i] : iouResult[i];
                    if (v > bestIou) { bestIou = v; bestIdx = i; }
                }

                var mask = new bool[maskH, maskW];
                for (int y = 0; y < maskH; y++)
                {
                    for (int x = 0; x < maskW; x++)
                    {
                        float v = masksHaveBatch ? masksResult[0, bestIdx, y, x] : masksResult[bestIdx, y, x];
                        mask[y, x] = v > 0f;
                    }
                }

                return (mask, maskScale);
            });
        }

        /// <summary>
        /// Upisuje PNG sa transparentnom pozadinom van maske — pravi "cutout".
        /// maskScale mapira koordinate originalne slike na (manju) rezoluciju
        /// maske — videti MAX_MASK_SIZE iznad.
        /// </summary>
        public static void ExportCutout(string originalPath, bool[,] mask, float maskScale, string outputPath)
        {
            DebugLog.Write("SamSegmenter: izvoz cutout-a — otvaram originalnu sliku...");
            using var img = new MagickImage(originalPath);
            img.Alpha(AlphaOption.Set);

            int maskH = mask.GetLength(0), maskW = mask.GetLength(1);
            int imgW = (int)img.Width, imgH = (int)img.Height;

            // Postavljamo Depth na 8 da Magick.NET Q16 vrati tačno 4 bajta po pikselu (RGBA 8-bit),
            // a ne 8 bajtova po pikselu (RGBA 16-bit).
            img.Depth = 8;
            byte[] rgba = img.ToByteArray(MagickFormat.Rgba);
            for (int y = 0; y < imgH; y++)
            {
                int my = Math.Min(maskH - 1, (int)(y * maskScale));
                int rowBase = y * imgW * 4;
                for (int x = 0; x < imgW; x++)
                {
                    int mx = Math.Min(maskW - 1, (int)(x * maskScale));
                    if (!mask[my, mx])
                        rgba[rowBase + x * 4 + 3] = 0; // alfa kanal na 0 (providno)
                }
            }

            DebugLog.Write("SamSegmenter: upisujem cutout na disk...");
            using var cutout = new MagickImage(rgba, new PixelReadSettings(
                (uint)imgW, (uint)imgH, StorageType.Char, PixelMapping.RGBA));
            cutout.Write(outputPath);
            DebugLog.Write($"SamSegmenter: cutout sačuvan u {outputPath}.");
        }

        public void Dispose()
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
        }
    }
}
