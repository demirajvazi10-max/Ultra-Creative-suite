using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UltraStudio.Models
{
    public class ImageProject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public string? OriginalPath { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        // ════════════════════════════════════════════════════════════════
        // SLOJEVI (grafički dizajn deo) — platno + tekst/oblik/slika slojevi
        // koji se crtaju PREKO pozadinske fotografije (ako postoji) sa njenim
        // postojećim Brightness/Contrast/itd. podešavanjima. Kad nema otvorene
        // fotografije, platno je prazna/bela podloga zadate veličine — isto
        // ponašanje kao npr. Canva "blank canvas".
        // ════════════════════════════════════════════════════════════════
        public ObservableCollection<Layer> Layers { get; } = new();

        private int _canvasWidth = 1200;
        public int CanvasWidth { get => _canvasWidth; set { _canvasWidth = value; OnChanged(); } }

        private int _canvasHeight = 800;
        public int CanvasHeight { get => _canvasHeight; set { _canvasHeight = value; OnChanged(); } }

        // Koristi se samo za generisanje podrazumevanih imena ("Text 1",
        // "Text 2"...) — raste, nikad se ne smanjuje čak ni posle brisanja,
        // tako da se imena ne ponavljaju u istoj sesiji.
        public int NextLayerNumber { get; set; } = 1;

        public bool HasCanvasContent => HasImage || Layers.Count > 0;

        // Sve vrednosti su "delta" od originala — 0 znaci "bez promene" za
        // brightness/contrast/saturation/sharpen/blur/rotate, tako da Reset
        // znaci samo "vrati sve na 0" bez posebne logike.
        private double _brightness; public double Brightness { get => _brightness; set { _brightness = value; OnChanged(); } }
        private double _contrast;   public double Contrast   { get => _contrast;   set { _contrast = value; OnChanged(); } }
        private double _saturation; public double Saturation { get => _saturation; set { _saturation = value; OnChanged(); } }
        private double _sharpen;    public double Sharpen    { get => _sharpen;    set { _sharpen = value; OnChanged(); } }
        private double _blur;       public double Blur       { get => _blur;       set { _blur = value; OnChanged(); } }
        private double _rotate;     public double Rotate     { get => _rotate;     set { _rotate = value; OnChanged(); } }
        private bool _grayscale;    public bool Grayscale     { get => _grayscale;   set { _grayscale = value; OnChanged(); } }
        private bool _sepia;        public bool Sepia         { get => _sepia;       set { _sepia = value; OnChanged(); } }
        private bool _flipH;        public bool FlipHorizontal{ get => _flipH;       set { _flipH = value; OnChanged(); } }
        private bool _flipV;        public bool FlipVertical  { get => _flipV;       set { _flipV = value; OnChanged(); } }

        public bool HasImage => !string.IsNullOrEmpty(OriginalPath);

        public void ResetAdjustments()
        {
            Brightness = 0; Contrast = 0; Saturation = 0; Sharpen = 0; Blur = 0; Rotate = 0;
            Grayscale = false; Sepia = false; FlipHorizontal = false; FlipVertical = false;
        }
    }
}
