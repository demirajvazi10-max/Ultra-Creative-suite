using UltraVideoEditor;
using Xunit;

namespace UltraVideoEditor.Tests
{
    public class SerbianTextTests
    {
        [Fact]
        public void Normalize_LowercasesAndRemovesDiacriticsAndPunctuation()
        {
            Assert.Equal("kad je zima nosi cizme", SerbianText.Normalize("Kad je ZIMA, nosi čizme!"));
        }

        [Fact]
        public void Normalize_ConvertsCyrillicToLatin()
        {
            Assert.Equal("djurdjevdan", SerbianText.Normalize("Ђурђевдан"));
            Assert.Equal("ljubav i njiva", SerbianText.Normalize("Љубав и њива"));
        }

        [Fact]
        public void Normalize_LatinDjBecomesDj()
        {
            Assert.Equal("djak", SerbianText.Normalize("Đak"));
        }

        [Fact]
        public void Normalize_EmptyAndNull()
        {
            Assert.Equal("", SerbianText.Normalize(null));
            Assert.Equal("", SerbianText.Normalize("  ...  "));
        }

        [Theory]
        [InlineData("leden vetar", "led", true)]     // word starts with the stem
        [InlineData("gledaj sneg", "led", false)]    // stem in the middle of a word does not count
        [InlineData("pada led", "=led", true)]       // '=' means the whole word
        [InlineData("leden vetar", "=led", false)]
        [InlineData("idemo u park sada", "u park", true)]   // phrase
        public void HasStem_MatchesWordStarts(string text, string stem, bool expected)
        {
            Assert.Equal(expected, SerbianText.HasStem(text, stem));
        }

        [Fact]
        public void Similarity_ToleratesOneWrongLetter()
        {
            Assert.Equal(1.0, SerbianText.Similarity("sladoled", "sladoled"));
            Assert.Equal(0.875, SerbianText.Similarity("sladolet", "sladoled"), 3);
            Assert.Equal(0.0, SerbianText.Similarity("", "sladoled"));
        }
    }
}
