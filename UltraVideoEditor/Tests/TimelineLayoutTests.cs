using System.Collections.Generic;
using UltraVideoEditor;
using Xunit;

namespace UltraVideoEditor.Tests
{
    public class TimelineLayoutTests
    {
        private static TimelineItem Clip(int track, double duration, double? fixedAt = null) => new TimelineItem
        {
            Path = "clip.mp4",
            TrackIndex = track,
            Duration = duration,
            UseFixedPosition = fixedAt.HasValue,
            FixedPosition = fixedAt ?? 0
        };

        [Fact]
        public void ClipsOnOneTrack_FollowEachOther()
        {
            var a = Clip(0, 3); var b = Clip(0, 4);
            TimelineLayout.Recalculate(new List<TimelineItem> { a, b });
            Assert.Equal(0, a.Start); Assert.Equal(3, a.End);
            Assert.Equal(3, b.Start); Assert.Equal(7, b.End);
        }

        [Fact]
        public void Tracks_RunInParallel()
        {
            var video = Clip(0, 10); var song = Clip(2, 180); var overlay = Clip(1, 5);
            TimelineLayout.Recalculate(new List<TimelineItem> { video, song, overlay });
            Assert.Equal(0, video.Start);
            Assert.Equal(0, song.Start);
            Assert.Equal(0, overlay.Start);
        }

        [Fact]
        public void PinnedClip_StaysAndOthersFlowAroundIt()
        {
            var first = Clip(0, 3); var second = Clip(0, 3); var pinned = Clip(0, 2, fixedAt: 5);
            TimelineLayout.Recalculate(new List<TimelineItem> { first, second, pinned });
            Assert.Equal(5, pinned.Start); Assert.Equal(7, pinned.End);
            Assert.Equal(0, first.Start);
            // 3..6 would overlap the pinned clip (5..7), so the second clip moves behind it
            Assert.Equal(7, second.Start); Assert.Equal(10, second.End);
        }

        [Fact]
        public void PinnedClip_IsNeverMovedEvenWhenItIsFirstInTheList()
        {
            var pinned = Clip(0, 4, fixedAt: 0); var next = Clip(0, 2);
            TimelineLayout.Recalculate(new List<TimelineItem> { pinned, next });
            Assert.Equal(0, pinned.Start);
            Assert.Equal(4, next.Start);
        }
    }
}
