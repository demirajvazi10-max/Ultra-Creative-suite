using System.Collections.Generic;
using UltraVideoEditor;
using Xunit;

namespace UltraVideoEditor.Tests
{
    public class LyricAlignerTests
    {
        private static AITranscription.WordTiming W(string word, double start, double end) =>
            new AITranscription.WordTiming { Word = word, StartSecond = start, EndSecond = end };

        [Fact]
        public void PerfectMatch_GivesEveryLineItsSungTime()
        {
            var lines = new List<string> { "Kad je zima", "nosi čizme" };
            var heard = new List<AITranscription.WordTiming>
            {
                W("kad", 1.0, 1.2), W("je", 1.2, 1.3), W("zima", 1.3, 1.8),
                W("nosi", 2.0, 2.3), W("cizme", 2.3, 2.9)
            };

            var result = LyricAligner.Align(lines, heard);

            Assert.True(result.IsUsable);
            Assert.Equal(1.0, result.Confidence, 3);
            Assert.Equal(1.0, result.Lines[0].Start, 3);
            Assert.Equal(1.8, result.Lines[0].End, 3);
            Assert.Equal(2.0, result.Lines[1].Start, 3);
            // the user's own spelling is kept
            Assert.Equal("čizme", result.Lines[1].Words[1].Word);
        }

        [Fact]
        public void CyrillicAndMisheardWords_StillMatch()
        {
            var lines = new List<string> { "volim sladoled" };
            var heard = new List<AITranscription.WordTiming> { W("ВОЛИМ", 3.0, 3.4), W("сладолет", 3.4, 4.0) };

            var result = LyricAligner.Align(lines, heard);

            Assert.Equal(1.0, result.Confidence, 3);
            Assert.Equal(3.4, result.Lines[0].Words[1].StartSecond, 3);
        }

        [Fact]
        public void NothingHeard_IsNotUsable()
        {
            var result = LyricAligner.Align(new List<string> { "neki tekst" }, new List<AITranscription.WordTiming>());
            Assert.False(result.IsUsable);
        }

        [Fact]
        public void AlignSung_RepeatsTheChorusTypedOnlyOnce()
        {
            // Typed once, sung twice — with typical Whisper mistakes in the heard text
            var lines = new List<string>
            {
                "Kad cveće latice zatvori, i kad se završi dan.",
                "Laku noć, laku noć, sad mirno spavaj Bebane moj."
            };
            string heardText = "latice zlatvori i kad se završi dan Mala pauza " +
                               "Laku noć, laku noć, sad mirno spavaj, bebane moj. " +
                               "Lako noć, lako noć, sad mirno spavaj beba nemoj.";
            var heard = new List<AITranscription.WordTiming>();
            double t = 0;
            foreach (string w in heardText.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                heard.Add(W(w, t, t + 0.4));
                t += 0.5;
            }

            var sung = LyricAligner.AlignSung(lines, heard);

            Assert.Equal(3, sung.Count);
            Assert.Equal(0, sung[0].LineIndex);
            Assert.Equal(1, sung[1].LineIndex);
            Assert.Equal(1, sung[2].LineIndex);
            Assert.True(sung[2].Start > sung[1].Start);
            Assert.Equal(lines[1], sung[2].Text);   // the typed text, not what Whisper heard
        }

        [Fact]
        public void LineStarts_NeverGoBackwards()
        {
            var lines = new List<string> { "prvi red", "drugi red", "treci red" };
            var heard = new List<AITranscription.WordTiming>
            {
                W("prvi", 1, 1.5), W("red", 1.5, 2), W("treci", 5, 5.5), W("red", 5.5, 6)
            };

            var result = LyricAligner.Align(lines, heard);

            for (int i = 1; i < result.Lines.Count; i++)
                Assert.True(result.Lines[i].Start >= result.Lines[i - 1].Start);
        }
    }
}
