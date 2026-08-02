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
        public static MagickImage ApplyAdjustments(string originalPath, ImageProject p)
        {
            var img = new MagickImage(originalPath);

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

            if (p.Sharpen > 0) img.Sharpen(p.Sharpen, p.Sharpen);
            if (p.Blur > 0) img.Blur(p.Blur, p.Blur);

            if (p.Grayscale) img.Grayscale();
            if (p.Sepia) img.SepiaTone();

            return img;
        }

        /// <summary>Vraća JPEG bajtove za prikaz u WPF Image kontroli (preview).</summary>
        public static byte[] RenderPreviewJpeg(string originalPath, ImageProject p, uint maxDimension = 1600)
        {
            using var img = ApplyAdjustments(originalPath, p);
            if (img.Width > maxDimension || img.Height > maxDimension)
                img.Resize(maxDimension, maxDimension);
            img.Format = MagickFormat.Jpeg;
            img.Quality = 90;
            return img.ToByteArray();
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
