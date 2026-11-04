using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace UltraStudio.Models
{
    /// <summary>
    /// Ceo projekat na jednom mestu. Sve što je ovde javno i ima get/set ulazi
    /// u Undo snimak i u .ustudio fajl (JSON) — zato su izračunata svojstva
    /// obeležena [JsonIgnore].
    ///
    /// Dva nivoa izmena (isti princip kao Photoshop):
    ///  - DESTRUKTIVNE (isecanje, promena veličine, filteri, retuš, uklanjanje
    ///    pozadine...) pišu NOVI PNG u folder sesije i samo promene BasePath.
    ///    Stari fajl ostaje na disku, pa je Undo samo vraćanje putanje — brzo
    ///    i bez gubitka kvaliteta.
    ///  - NEDESTRUKTIVNE (podešavanja tona/boje) su brojevi ispod; primenjuju
    ///    se svaki put iznova na BasePath pri prikazu i izvozu.
    /// </summary>
    public class ImageProject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        /// <summary>Fajl koji je korisnik otvorio (za ime i informacije). Ne menja se posle destruktivnih izmena.</summary>
        public string? OriginalPath { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }

        /// <summary>Trenutna radna slika (posle destruktivnih izmena). Null = koristi OriginalPath.</summary>
        public string? BasePath { get; set; }

        /// <summary>Putanja .ustudio fajla ako je projekat sačuvan.</summary>
        [JsonIgnore] public string? ProjectPath { get; set; }

        [JsonIgnore] public string? SourcePath => !string.IsNullOrEmpty(BasePath) ? BasePath : OriginalPath;
        [JsonIgnore] public bool HasImage => !string.IsNullOrEmpty(SourcePath);
        [JsonIgnore] public bool HasCanvasContent => HasImage || Layers.Count > 0 || BlankCanvas;

        /// <summary>Napravljeno prazno platno (Novo platno) — prikazuje se i bez slojeva.</summary>
        public bool BlankCanvas { get; set; }

        public ObservableCollection<Layer> Layers { get; } = new();

        // ── Višestranični dokument ──
        // Trenutna strana živi u poljima iznad (Layers, BasePath, platno, podešavanja...),
        // pa sav postojeći kod radi nad "trenutnom stranom" bez izmena. Ostale strane su
        // ovde kao JSON snimci; Pages[CurrentPage] se osvežava pri promeni strane i čuvanju.
        // Prazna lista = dokument od jedne strane.
        public System.Collections.Generic.List<string> Pages { get; set; } = new();
        public int CurrentPage { get; set; }
        [JsonIgnore] public int PageCount => System.Math.Max(1, Pages.Count);

        /// <summary>Rezolucija za štampu/PDF (tačaka po inču). Određuje veličinu PDF strane i pretvaranje pt ↔ px.</summary>
        public double Dpi { get; set; } = 300;

        private int _canvasWidth = 1200;
        public int CanvasWidth { get => _canvasWidth; set { _canvasWidth = value; OnChanged(); } }

        private int _canvasHeight = 800;
        public int CanvasHeight { get => _canvasHeight; set { _canvasHeight = value; OnChanged(); } }

        /// <summary>Boja praznog platna (kad nema fotografije). "transparent" je dozvoljeno.</summary>
        public string CanvasBackgroundHex { get; set; } = "#FFFFFF";

        public int NextLayerNumber { get; set; } = 1;

        /// <summary>Aktivna selekcija (u pikselima platna).</summary>
        public Selection Selection { get; set; } = new();

        /// <summary>Ime poslednjeg izabranog izgleda (look preset), samo informativno.</summary>
        public string LookName { get; set; } = "";

        // ── Nedestruktivna podešavanja. 0 / false = bez promene. ──
        private double _brightness; public double Brightness { get => _brightness; set { _brightness = value; OnChanged(); } }
        private double _contrast;   public double Contrast   { get => _contrast;   set { _contrast = value; OnChanged(); } }
        private double _saturation; public double Saturation { get => _saturation; set { _saturation = value; OnChanged(); } }
        private double _vibrance;   public double Vibrance   { get => _vibrance;   set { _vibrance = value; OnChanged(); } }
        private double _exposure;   public double Exposure   { get => _exposure;   set { _exposure = value; OnChanged(); } }   // -3..+3 EV
        private double _gamma;      public double Gamma      { get => _gamma;      set { _gamma = value; OnChanged(); } }      // -100..100 (0 = 1.0)
        private double _highlights; public double Highlights { get => _highlights; set { _highlights = value; OnChanged(); } } // -100..100
        private double _shadows;    public double Shadows    { get => _shadows;    set { _shadows = value; OnChanged(); } }    // -100..100
        private double _temperature;public double Temperature{ get => _temperature;set { _temperature = value; OnChanged(); } }// -100 hladno .. +100 toplo
        private double _tint;       public double Tint       { get => _tint;       set { _tint = value; OnChanged(); } }       // -100 zeleno .. +100 magenta
        private double _hue;        public double Hue        { get => _hue;        set { _hue = value; OnChanged(); } }        // -180..180 stepeni
        private double _clarity;    public double Clarity    { get => _clarity;    set { _clarity = value; OnChanged(); } }    // -100..100
        private double _sharpen;    public double Sharpen    { get => _sharpen;    set { _sharpen = value; OnChanged(); } }
        private double _blur;       public double Blur       { get => _blur;       set { _blur = value; OnChanged(); } }
        private double _vignette;   public double Vignette   { get => _vignette;   set { _vignette = value; OnChanged(); } }   // 0..100
        private double _fade;       public double Fade       { get => _fade;       set { _fade = value; OnChanged(); } }       // 0..100 (podignute crne, "mat" izgled)
        private double _posterize;  public double Posterize  { get => _posterize;  set { _posterize = value; OnChanged(); } }  // 0 = isklj, 2..32 nivoa
        private double _threshold;  public double Threshold  { get => _threshold;  set { _threshold = value; OnChanged(); } }  // 0 = isklj, 1..100
        private bool _grayscale;    public bool Grayscale    { get => _grayscale;  set { _grayscale = value; OnChanged(); } }
        private bool _sepia;        public bool Sepia        { get => _sepia;      set { _sepia = value; OnChanged(); } }
        private bool _invert;       public bool Invert       { get => _invert;     set { _invert = value; OnChanged(); } }

        [JsonIgnore]
        public bool HasAdjustments =>
            Brightness != 0 || Contrast != 0 || Saturation != 0 || Vibrance != 0 || Exposure != 0 || Gamma != 0 ||
            Highlights != 0 || Shadows != 0 || Temperature != 0 || Tint != 0 || Hue != 0 || Clarity != 0 ||
            Sharpen != 0 || Blur != 0 || Vignette != 0 || Fade != 0 || Posterize != 0 || Threshold != 0 ||
            Grayscale || Sepia || Invert;

        public void ResetAdjustments()
        {
            Brightness = Contrast = Saturation = Vibrance = Exposure = Gamma = 0;
            Highlights = Shadows = Temperature = Tint = Hue = Clarity = 0;
            Sharpen = Blur = Vignette = Fade = Posterize = Threshold = 0;
            Grayscale = Sepia = Invert = false;
            LookName = "";
        }

        /// <summary>Kopira samo podešavanja (za look presete i batch obradu).</summary>
        public void CopyAdjustmentsFrom(ImageProject o)
        {
            Brightness = o.Brightness; Contrast = o.Contrast; Saturation = o.Saturation; Vibrance = o.Vibrance;
            Exposure = o.Exposure; Gamma = o.Gamma; Highlights = o.Highlights; Shadows = o.Shadows;
            Temperature = o.Temperature; Tint = o.Tint; Hue = o.Hue; Clarity = o.Clarity;
            Sharpen = o.Sharpen; Blur = o.Blur; Vignette = o.Vignette; Fade = o.Fade;
            Posterize = o.Posterize; Threshold = o.Threshold;
            Grayscale = o.Grayscale; Sepia = o.Sepia; Invert = o.Invert; LookName = o.LookName;
        }
    }
}
