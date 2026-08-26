using System;
using System.IO;
using ImageMagick;
using UltraStudio.Models;

namespace UltraStudio.Services
{
    /// <summary>
    /// Sve operacije rade uvek na SVEŽOJ kopiji originalnog fajla (ne na
    /// prethodnom rezultatu) — isti princip kao Audio Editor-ova ne-destruktivna
    /// podešavanja: pomeranje "Brightness" slajdera dva puta ne pojačava efekat
    /// kumulativno, primenjuje se čist ceo lanac od originala svaki put.
    /// Sporije nego čuvanje međurezultata, ali predvidljivo i bez akumulirane
    /// greške kod ponovljenih izmena — isto zašto smo u Audio Editoru birali
    /// jasnoću nad brzinom kad god su bila u pitanju manja podešavanja.
    /// </summary>
    public static class ImageEngine
    {
        /// <param name="previewMaxDimension">
        /// Kad je zadato, slika se umanji NA OVU VELIČINU PRE primene filtera
        /// poput Sharpen/Blur, ne posle. Cena tih filtera raste sa rezolucijom
        /// slike na koju se primenjuju, ne samo sa jačinom efekta — primenjeni
        /// na punoj rezoluciji originalne fotografije (telefonske slike lako
        /// imaju 4000+ piksela), jak Sharpen/Blur je umeo da potraje i po
        /// nekoliko minuta, što je izgledalo kao potpuno zamrzavanje aplikacije
        /// (UI nit čeka sinhrono na taj jedan poziv). Export (bez ovog
        /// parametra) i dalje radi na punoj rezoluciji — samo pregled ne mora.
        /// </param>
        public static MagickImage ApplyAdjustments(string originalPath, ImageProject p, uint? previewMaxDimension = null)
        {
            var img = new MagickImage(originalPath);

            if (previewMaxDimension.HasValue && (img.Width > previewMaxDimension.Value || img.Height > previewMaxDimension.Value))
                img.Resize(previewMaxDimension.Value, previewMaxDimension.Value);

            if (p.Rotate != 0) img.Rotate(p.Rotate);
            if (p.FlipHorizontal) img.Flop();
            if (p.FlipVertical) img.Flip();

            if (p.Brightness != 0 || p.Contrast != 0)
            {
                // Magick.NET BrightnessContrast očekuje Percentage od -100 do 100
                img.BrightnessContrast(new Percentage(p.Brightness), new Percentage(p.Contrast));
            }

            if (p.Saturation != 0)
            {
                // Modulate: 100 = bez promene, 0 = potpuno crno-belo, 200 = duplo zasićenije
                double sat = 100 + p.Saturation;
                img.Modulate(new Percentage(100), new Percentage(Math.Max(0, sat)), new Percentage(100));
            }

            if (p.Sharpen > 0) img.Sharpen(0, Math.Min(2.5, p.Sharpen * 0.25)); // skalirano: 0-10 -> sigma 0-2.5 (sprečava višeminutno zamrzavanje velikih slika)
            if (p.Blur > 0) img.Blur(0, Math.Min(4.0, p.Blur * 0.4)); // skalirano: 0-10 -> sigma 0-4.0

            if (p.Grayscale) img.Grayscale();
            if (p.Sepia) img.SepiaTone();

            return img;
        }

        /// <summary>Vraća JPEG bajtove za prikaz u WPF Image kontroli (preview).</summary>
        public static byte[] RenderPreviewJpeg(string originalPath, ImageProject p, uint maxDimension = 1600)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var img = ApplyAdjustments(originalPath, p, maxDimension);
            if (img.Width > maxDimension || img.Height > maxDimension)
                img.Resize(maxDimension, maxDimension);
            img.Format = MagickFormat.Jpeg;
            img.Quality = 90;
            var bytes = img.ToByteArray();
            DebugLog.Write($"ImageEngine.RenderPreviewJpeg: {sw.ElapsedMilliseconds}ms (Sharpen={p.Sharpen}, Blur={p.Blur}).");
            return bytes;
        }

        public static void Export(string originalPath, ImageProject p, string outputPath)
        {
            using var img = ApplyAdjustments(originalPath, p);
            img.Write(outputPath);
        }

        public static (int width, int height) GetDimensions(string path)
        {
            var info = new MagickImageInfo(path);
            return ((int)info.Width, (int)info.Height);
        }
    }
}
