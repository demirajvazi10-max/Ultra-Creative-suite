using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace UltraStudio.Models
{
    public enum ShapeKind { Rectangle, Ellipse, Line }

    /// <summary>Režimi stapanja — isti nazivi kao u Photoshopu.</summary>
    public enum BlendMode
    {
        Normal, Multiply, Screen, Overlay, SoftLight, HardLight, Darken, Lighten,
        ColorDodge, ColorBurn, Difference, Exclusion, Hue, Saturation, Color, Luminosity
    }

    public enum TextAlign { Left, Center, Right, Justify }

    public enum GradientKind { Linear, Radial }

    /// <summary>
    /// Bazna klasa za sve tipove slojeva na platnu. Pozicija/veličina su UVEK
    /// u pikselima platna. Sve javno sa get/set ide u Undo i u .ustudio fajl.
    /// </summary>
    public abstract class Layer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public Guid Id { get; set; } = Guid.NewGuid();

        private string _name = "Layer";
        public string Name { get => _name; set { _name = value; OnChanged(); } }

        private bool _visible = true;
        public bool Visible { get => _visible; set { _visible = value; OnChanged(); } }

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

        /// <summary>Rotacija oko centra sloja, u stepenima (pozitivno = u smeru kazaljke).</summary>
        public double Rotation { get; set; }

        public BlendMode Blend { get; set; } = BlendMode.Normal;

        /// <summary>Posebna uloga (npr. "page_number") — da komande mogu da ih nađu i zamene.</summary>
        public string Role { get; set; } = "";

        // ── Stilovi sloja (Layer Styles) ──
        public bool ShadowEnabled { get; set; }
        public string ShadowColorHex { get; set; } = "#000000";
        public double ShadowOffsetX { get; set; } = 6;
        public double ShadowOffsetY { get; set; } = 6;
        public double ShadowBlur { get; set; } = 8;
        public double ShadowOpacity { get; set; } = 60;

        public bool OutlineEnabled { get; set; }
        public string OutlineColorHex { get; set; } = "#000000";
        public double OutlineWidth { get; set; } = 3;

        public bool GlowEnabled { get; set; }
        public string GlowColorHex { get; set; } = "#FFFFAA";
        public double GlowSize { get; set; } = 12;

        [JsonIgnore] public abstract string TypeLabelKey { get; }

        [JsonIgnore] public double CenterX => X + Width / 2;
        [JsonIgnore] public double CenterY => Y + Height / 2;
    }

    public class TextLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_text";

        private string _text = "Text";
        /// <summary>Više redova je dozvoljeno (novi red = novi red na platnu).</summary>
        public string Text { get => _text; set { _text = value; OnChanged(); } }

        private string _fontFamily = "Segoe UI";
        public string FontFamily { get => _fontFamily; set { _fontFamily = value; OnChanged(); } }

        private double _fontSize = 32;
        public double FontSize { get => _fontSize; set { _fontSize = Math.Max(1, value); OnChanged(); } }

        private bool _bold;
        public bool Bold { get => _bold; set { _bold = value; OnChanged(); } }

        private bool _italic;
        public bool Italic { get => _italic; set { _italic = value; OnChanged(); } }

        private string _colorHex = "#FFFFFF";
        public string ColorHex { get => _colorHex; set { _colorHex = value; OnChanged(); } }

        /// <summary>Poravnanje unutar okvira sloja (X..X+Width).</summary>
        public TextAlign Align { get; set; } = TextAlign.Left;

        /// <summary>Razmak između redova, umnožak veličine fonta (1.2 = uobičajeno).</summary>
        public double LineSpacing { get; set; } = 1.2;

        /// <summary>Okvir sloja se sam prilagođava tekstu (tačno centriranje i poravnanje).</summary>
        public bool AutoSize { get; set; } = true;

        /// <summary>Tekstualni okvir: redovi se lome po širini okvira (prelom reči), visina okvira je fiksna.</summary>
        public bool Wrap { get; set; }
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

        /// <summary>Zaobljeni uglovi pravougaonika (px).</summary>
        public double CornerRadius { get; set; }
    }

    public class ImageLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_image";

        private string _sourcePath = "";
        public string SourcePath { get => _sourcePath; set { _sourcePath = value; OnChanged(); } }
    }

    /// <summary>Prelaz boja (gradijent) preko okvira sloja — linearni pod uglom ili radijalni.</summary>
    public class GradientLayer : Layer
    {
        public override string TypeLabelKey => "layer_type_gradient";

        public GradientKind Kind { get; set; } = GradientKind.Linear;
        public string StartColorHex { get; set; } = "#000000";
        public string EndColorHex { get; set; } = "#FFFFFF";
        /// <summary>Providnost krajnje boje, 0-100 (0 = potpuno providno — korisno za "fade" preko fotografije).</summary>
        public double StartOpacity { get; set; } = 100;
        public double EndOpacity { get; set; } = 100;
        /// <summary>Ugao linearnog prelaza: 0 = s leva na desno, 90 = odozgo nadole.</summary>
        public double Angle { get; set; } = 90;
    }
}
