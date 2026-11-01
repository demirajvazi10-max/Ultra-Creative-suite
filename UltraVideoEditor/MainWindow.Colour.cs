using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using WInput = System.Windows.Input;

namespace UltraVideoEditor
{
    /// <summary>
    /// v2.0 — Ctrl+Alt+C, "Colour here": the picture on screen at the playhead (the highest
    /// shown video track, with its colour settings) is measured and described in words —
    /// brightness, contrast, crushed blacks and burnt-out whites, colour cast, saturation and
    /// the main colours. It is what scopes show, spoken. Pressed again, it describes the clip
    /// as a whole (three frames).
    /// </summary>
    public partial class MainWindow
    {
        private bool _colourBusy;
        private DateTime _lastColourKey = DateTime.MinValue;

        private async void ColourHere_Click(object sender, RoutedEventArgs e)
        {
            if (_colourBusy) return;
            RecalculatePositions();
            double t = _mixPreviewActive ? currentPlaybackPosition : TimelinePosition;
            var clip = TimelineMixPreview.VideoAt(timelineItems, t) ?? SelectedClip();
            if (clip == null || clip.IsAudio || clip.Type == "Audio" || string.IsNullOrEmpty(clip.Path) || !System.IO.File.Exists(clip.Path))
            { LogMessage(L("col_no_clip"), true); return; }
            bool whole = (DateTime.Now - _lastColourKey).TotalSeconds < 6;   // pressed again: the whole clip
            _lastColourKey = whole ? DateTime.MinValue : DateTime.Now;
            _colourBusy = true;
            try
            {
                double sp = clip.IsImage ? 1 : Math.Clamp(clip.VideoEffect?.Speed is double s0 && s0 > 0.01 ? s0 : 1, 0.1, 10);
                var times = whole || t < clip.Start || t >= clip.Start + clip.Duration
                    ? ColorAnalyzer.SampleTimes(clip)
                    : new List<double> { clip.SourceOffset + (t - clip.Start) * sp };
                LogMessage(L("cfx_analysing"), false);
                var stats = await ColorAnalyzer.MeasureAsync(clip.Path, times, clip.IsImage,
                                                             VideoEffects.GetFFmpegFilter(clip.VideoEffect, includeSpeed: false), CancellationToken.None);
                string where = times.Count == 1 ? LF("col_here_at", clip.Name ?? "", FormatTimeDetailed(t)) : LF("col_here_clip", clip.Name ?? "");
                LogMessage(where + " " + ColorAnalyzer.Describe(stats) + (times.Count == 1 ? " " + L("col_again_whole") : ""), true);
            }
            catch (Exception ex) { AppLog.Error("Colour here", ex); LogMessage(LF("generic_error", ex.Message), true); }
            finally { _colourBusy = false; }
        }

        private static bool IsColourKey(WInput.Key key, WInput.ModifierKeys mods) =>
            key == WInput.Key.C && mods == (WInput.ModifierKeys.Control | WInput.ModifierKeys.Alt);

        private bool HandleColourKey(WInput.Key key, WInput.ModifierKeys mods)
        {
            if (!IsColourKey(key, mods)) return false;
            ColourHere_Click(null, null);
            return true;
        }
    }
}
