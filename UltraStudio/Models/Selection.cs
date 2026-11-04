using System;
using Newtonsoft.Json;

namespace UltraStudio.Models
{
    public enum SelectionKind { None, Rectangle, Ellipse, Mask }

    /// <summary>
    /// Selekcija u pikselima platna. Pravougaonik/elipsa su čisti brojevi
    /// (tačno onako kako ih korisnik upiše), a AI selekcija i opseg boja su
    /// maska — sivi PNG u folderu sesije (belo = izabrano).
    /// </summary>
    public class Selection
    {
        public SelectionKind Kind { get; set; } = SelectionKind.None;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Meka ivica u pikselima (0 = oštra).</summary>
        public double Feather { get; set; }

        /// <summary>Obrnuta selekcija (sve OSIM izabranog).</summary>
        public bool Inverted { get; set; }

        /// <summary>Za Kind=Mask: putanja do sive maske (može biti manje rezolucije, razvlači se na platno).</summary>
        public string? MaskPath { get; set; }

        /// <summary>Kratak opis odakle je selekcija (npr. "AI: dete", "Plave nijanse") — za izgovor.</summary>
        public string Label { get; set; } = "";

        [JsonIgnore] public bool IsActive => Kind != SelectionKind.None;

        public Selection Clone() => (Selection)MemberwiseClone();
    }
}
