using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UltraCaptions.Models;

namespace UltraCaptions.Services
{
    /// <summary>
    /// Reads and writes the standard .srt subtitle format. Used both to parse
    /// Whisper's output and to export whatever the user has typed/timed by
    /// hand - one format, one code path, regardless of how a caption was made.
    /// </summary>
    public static class SrtService
    {
        private static readonly Regex TimeLine = new(
            @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})",
            RegexOptions.Compiled);

        public static List<CaptionEntry> Import(string srtPath)
        {
            var result = new List<CaptionEntry>();
            var lines = File.ReadAllLines(srtPath);

            CaptionEntry? current = null;
            var textBuffer = new StringBuilder();

            void FlushCurrent()
            {
                if (current != null)
                {
                    current.Text = textBuffer.ToString().Trim();
                    result.Add(current);
                }
                current = null;
                textBuffer.Clear();
            }

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();

                var match = TimeLine.Match(line);
                if (match.Success)
                {
                    FlushCurrent();
                    current = new CaptionEntry
                    {
                        Start = ToTimeSpan(match, 1),
                        End = ToTimeSpan(match, 5)
                    };
                    continue;
                }

                // Skip the numeric index lines and blank separator lines.
                if (current == null) continue;
                if (line.Length == 0) { FlushCurrent(); continue; }
                if (int.TryParse(line, out _) && textBuffer.Length == 0) continue;

                if (textBuffer.Length > 0) textBuffer.Append(' ');
                textBuffer.Append(line);
            }

            FlushCurrent();
            return result;
        }

        public static void Export(string srtPath, IReadOnlyList<CaptionEntry> captions)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < captions.Count; i++)
            {
                var c = captions[i];
                sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine($"{Format(c.Start)} --> {Format(c.End)}");
                sb.AppendLine(c.Text);
                sb.AppendLine();
            }

            File.WriteAllText(srtPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static TimeSpan ToTimeSpan(Match m, int startGroup)
        {
            int h = int.Parse(m.Groups[startGroup].Value, CultureInfo.InvariantCulture);
            int min = int.Parse(m.Groups[startGroup + 1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(m.Groups[startGroup + 2].Value, CultureInfo.InvariantCulture);
            int ms = int.Parse(m.Groups[startGroup + 3].Value, CultureInfo.InvariantCulture);
            return new TimeSpan(0, h, min, sec, ms);
        }

        private static string Format(TimeSpan t) =>
            $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
    }
}
