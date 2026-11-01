using UltraVideoEditor;
using Xunit;

namespace UltraVideoEditor.Tests
{
    public class TransitionTests
    {
        [Theory]
        [InlineData(TransitionType.Fade, "fadeblack")]
        [InlineData(TransitionType.Crossfade, "fade")]
        [InlineData(TransitionType.ZoomIn, "zoomin")]       // "zoom" is not a valid xfade name
        [InlineData(TransitionType.ZoomOut, "circleclose")]
        [InlineData(TransitionType.SlideLeft, "slideleft")]
        public void ToXfadeName_GivesValidFfmpegNames(TransitionType type, string expected)
        {
            Assert.Equal(expected, TransitionEffect.ToXfadeName(type));
        }

        [Fact]
        public void FfmpegFilter_UsesDecimalPoint()
        {
            var t = new TransitionEffect { Type = TransitionType.Crossfade, Duration = 0.5 };
            Assert.Contains("duration=0.5", t.FFmpegFilter);
            Assert.DoesNotContain("0,5", t.FFmpegFilter);
        }
    }

    public class TimelineItemTests
    {
        [Theory]
        [InlineData("pesma.MP3", true, false, false)]
        [InlineData("slika.JPG", false, true, false)]
        [InlineData("klip.webm", false, false, true)]
        [InlineData("zvuk.ogg", true, false, false)]
        public void FileType_IsDetectedCaseInsensitively(string path, bool audio, bool image, bool video)
        {
            var item = new TimelineItem { Path = path };
            Assert.Equal(audio, item.IsAudio);
            Assert.Equal(image, item.IsImage);
            Assert.Equal(video, item.IsVideo);
        }
    }

    public class KidsQueryTests
    {
        [Fact]
        public void LyricsWithoutDiacritics_FindTheThings_NotTheSeason()
        {
            var q = StrictQueryEngine.GetAllHardCodedMatches("leti kupite sladoled, zimi neki topli caj. Moze i topla cokolada");
            Assert.Contains("child eating ice cream", q);
            Assert.Contains("child drinking hot chocolate mug", q);
            Assert.DoesNotContain("winter snow children outdoor", q);
            Assert.DoesNotContain("summer sunny children outdoor", q);
        }

        [Fact]
        public void SeasonAlone_StillGivesTheSeason()
        {
            var q = StrictQueryEngine.GetAllHardCodedMatches("Kad je zima");
            Assert.Contains("winter snow children outdoor", q);
        }

        [Theory]
        [InlineData("elderly, wheelchair, physiotherapy", true)]
        [InlineData("grandmother, senior, park", true)]
        [InlineData("grandmother, granddaughter, park, walk", false)]
        [InlineData("road, construction, warning sign", true)]
        [InlineData("crowd, aerial, square", true)]
        [InlineData("child, ice cream, summer, happy", false)]
        public void UnsuitableForKids(string tags, bool expected)
        {
            Assert.Equal(expected, IskraKidsSafeQuery.IsUnsuitableForKids(tags, out _));
        }

        [Theory]
        [InlineData("meerkat, animal, desert", "Šetaj parkićem", true)]
        [InlineData("monkey, jungle, child", "Mali majmun skače", false)]
        [InlineData("ants, tree, macro", "Kad je zima nosi čizme", true)]
        [InlineData("child, snow, winter", "Kad je zima nosi čizme", false)]
        public void AnimalsOnlyWhenSung(string tags, string lyric, bool expected)
        {
            Assert.Equal(expected, IskraKidsSafeQuery.IsUnsuitableForKids(tags, lyric, out _));
        }

        [Theory]
        [InlineData("Kad je zima nosi cizme", "winter")]
        [InlineData("Kad je zima nosi čizme", "winter")]
        [InlineData("Tokom proleca i jeseni, hladne zime i toplog leta", null)]
        public void SeasonFromLyric_WorksWithoutDiacritics(string lyric, string expected)
        {
            Assert.Equal(expected, StrictQueryEngine.DetectSeasonFromLyric(lyric));
        }
    }
}
