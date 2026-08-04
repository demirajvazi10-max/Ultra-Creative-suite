using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UltraStudio.Models
{
    public enum ShapeKind { Rectangle, Ellipse, Line }

    /// <summary>
    /// Bazna klasa za sve tipove slojeva na platnu (tekst/oblik/slika).
    /// Pozicija/veličina su UVEK u koordinatama platna (canvas pixels), ne
    /// ekrana — isti princip kao ImageProject: sve se čuva kao "čisti" podatak,
    /// crtanje/kompozicija se radi tek u CanvasEngine iz ovih vrednosti, nikad
    /// se ne modifikuje piksel po piksel u samom modelu.
    /// </summary>
    public abstract class Layer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public Guid Id { get; } = Guid.NewGuid();

        private string _name = "Layer";
        public string Name { get => _name; set { _name = value; OnChanged(); } }

        private bool _visible = true;
        public bool Visible { get => _visible; set { _visible = value; OnChanged(); } }

        // 0-100, isti opseg kao ostala Ultra Studio podešavanja (Brightness/itd.)
        private double _opacity = 100;
        public double Opacity { get => _opacity; set { _opacity = Math.Clamp(value, 0, 100); OnChanged(); } }

        private double _x;
        public double X { get => _x; set { _x = value; OnChanged(); } }

        private double _y;
        public double Y { get => _y; set { _y = value; OnChanged(); } }

        private double _width = 100;
        public double Width { get => _width; set { _width = Math.Max(1, value); OnChanged(); } }

        private double _height = 100;
        public double Height { get => _height; set { _height = Math.Max(1, value); OnChanged(); } }

        public abstract string TypeLabelKey { get; }
    }

    public class TextLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_text";

        private string _text = "Text";
        public string Text { get => _text; set { _text = value; OnChanged(); } }

        private string _fontFamily = "Segoe UI";
        public string FontFamily { get => _fontFamily; set { _fontFamily = value; OnChanged(); } }

        private double _fontSize = 32;
        public double FontSize { get => _fontSize; set { _fontSize = Math.Max(1, value); OnChanged(); } }

        private bool _bold;
        public bool Bold { get => _bold; set { _bold = value; OnChanged(); } }

        private bool _italic;
        public bool Italic { get => _italic; set { _italic = value; OnChanged(); } }

        // #RRGGBB — čuvano kao string radi jednostavnog prikaza u JAWS listi
        // (native ListView ne zna za WPF Color), konvertuje se u MagickColor
        // tek u CanvasEngine.
        private string _colorHex = "#FFFFFF";
        public string ColorHex { get => _colorHex; set { _colorHex = value; OnChanged(); } }
    }

    public class ShapeLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_shape";

        private ShapeKind _shapeKind = ShapeKind.Rectangle;
        public ShapeKind ShapeKind { get => _shapeKind; set { _shapeKind = value; OnChanged(); } }

        private bool _fillEnabled = true;
        public bool FillEnabled { get => _fillEnabled; set { _fillEnabled = value; OnChanged(); } }

        private string _fillColorHex = "#7C6AF7";
        public string FillColorHex { get => _fillColorHex; set { _fillColorHex = value; OnChanged(); } }

        private bool _strokeEnabled;
        public bool StrokeEnabled { get => _strokeEnabled; set { _strokeEnabled = value; OnChanged(); } }

        private string _strokeColorHex = "#FFFFFF";
        public string StrokeColorHex { get => _strokeColorHex; set { _strokeColorHex = value; OnChanged(); } }

        private double _strokeWidth = 2;
        public double StrokeWidth { get => _strokeWidth; set { _strokeWidth = Math.Max(0, value); OnChanged(); } }
    }

    public class ImageLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_image";

        private string _sourcePath = "";
        public string SourcePath { get => _sourcePath; set { _sourcePath = value; OnChanged(); } }
    }
}
