using System;
using System.Collections.Generic;
using System.Linq;

namespace UltraVideoEditor
{
    /// <summary>
    /// Aligns the user's OWN lyrics (exact text, line by line) with the words Whisper
    /// heard in the song, so every line — and every word — gets a real start/end time.
    ///
    /// Why: before v1.4 the AI video creator only had timestamps when the user pressed
    /// "Transcribe" (and then Whisper's text, with its mistakes, replaced the lyrics).
    /// Typed or pasted lyrics got NO timing at all, so scenes were spread evenly over the
    /// song instead of following the singing. The old ForcedAlignAsync mapped Whisper
    /// segments to lines by count ratio, which drifts as soon as Whisper splits or merges
    /// a line differently.
    ///
    /// How: both texts are normalized (Cyrillic→Latin — Whisper often writes Serbian in
    /// Cyrillic — no diacritics, no punctuation). Then a global sequence alignment
    /// (Needleman–Wunsch) over WORDS with fuzzy similarity pairs user words with heard
    /// words. Misheard words still match ("sladolet" ≈ "sladoled"), extra or missing words
    /// become gaps, and repeated choruses stay in order. User words that found no partner
    /// get times interpolated between their matched neighbours.
    /// </summary>
    public static class LyricAligner
    {
        public class AlignedLine
        {
            public int LineIndex { get; set; }
            public string Text { get; set; } = "";
            public double Start { get; set; }
            public double End { get; set; }
            /// <summary>The user's own words (original spelling) with their times.</summary>
            public List<AITranscription.WordTiming> Words { get; set; } = new();
            /// <summary>How many of this line's words were matched to a heard word.</summary>
            public int MatchedWords { get; set; }
        }

        public class AlignmentResult
        {
            public List<AlignedLine> Lines { get; set; } = new();
            /// <summary>Share of the user's words that were matched to heard words (0..1).</summary>
            public double Confidence { get; set; }
            public bool IsUsable => Lines.Count > 0 && Confidence >= MinUsableConfidence;
        }

        /// <summary>Below this share of matched words the timing is not trusted.</summary>
        public const double MinUsableConfidence = 0.35;

        private const double MatchThreshold = 0.5;   // word similarity needed to count as the same word
        private const double GapScore = -0.6;
        private const double MismatchScore = -1.0;
        private const double DefaultWordDuration = 0.35;

        private struct UserWord
        {
            public int Line;
            public string Original;
            public string Norm;
        }

        public static AlignmentResult Align(IList<string> userLines, IList<AITranscription.WordTiming> heard)
        {
            var result = new AlignmentResult();
            if (userLines == null || userLines.Count == 0 || heard == null || heard.Count == 0)
                return result;

            // ── Tokenize ────────────────────────────────────────────────────────
            var user = new List<UserWord>();
            for (int li = 0; li < userLines.Count; li++)
            {
                foreach (string token in (userLines[li] ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string n = SerbianText.NormalizeWord(token);
                    if (n.Length > 0) user.Add(new UserWord { Line = li, Original = token, Norm = n });
                }
            }

            var words = new List<(string Norm, double Start, double End)>();
            foreach (var h in heard)
            {
                string n = SerbianText.NormalizeWord(h.Word);
                if (n.Length > 0) words.Add((n, h.StartSecond, Math.Max(h.EndSecond, h.StartSecond + 0.05)));
            }

            int nU = user.Count, nW = words.Count;
            if (nU == 0 || nW == 0) return result;

            // ── Needleman–Wunsch ────────────────────────────────────────────────
            var score = new double[nU + 1, nW + 1];
            var back = new byte[nU + 1, nW + 1];   // 0 = diagonal, 1 = up (skip user word), 2 = left (skip heard word)
            for (int i = 1; i <= nU; i++) { score[i, 0] = i * GapScore; back[i, 0] = 1; }
            for (int j = 1; j <= nW; j++) { score[0, j] = j * GapScore; back[0, j] = 2; }

            for (int i = 1; i <= nU; i++)
            {
                for (int j = 1; j <= nW; j++)
                {
                    double sim = SerbianText.Similarity(user[i - 1].Norm, words[j - 1].Norm);
                    double diag = score[i - 1, j - 1] + (sim >= MatchThreshold ? 1.0 + sim : MismatchScore);
                    double up = score[i - 1, j] + GapScore;
                    double left = score[i, j - 1] + GapScore;

                    if (diag >= up && diag >= left) { score[i, j] = diag; back[i, j] = 0; }
                    else if (up >= left) { score[i, j] = up; back[i, j] = 1; }
                    else { score[i, j] = left; back[i, j] = 2; }
                }
            }

            // ── Traceback ───────────────────────────────────────────────────────
            var match = new int[nU];
            for (int k = 0; k < nU; k++) match[k] = -1;
            {
                int i = nU, j = nW;
                while (i > 0 || j > 0)
                {
                    byte b = back[i, j];
                    if (i > 0 && j > 0 && b == 0)
                    {
                        if (SerbianText.Similarity(user[i - 1].Norm, words[j - 1].Norm) >= MatchThreshold)
                            match[i - 1] = j - 1;
                        i--; j--;
                    }
                    else if (i > 0 && (j == 0 || b == 1)) i--;
                    else j--;
                }
            }

            // ── Times for every user word ───────────────────────────────────────
            var start = new double[nU];
            var end = new double[nU];
            var known = new bool[nU];
            int matched = 0;
            for (int k = 0; k < nU; k++)
            {
                if (match[k] < 0) continue;
                start[k] = words[match[k]].Start;
                end[k] = words[match[k]].End;
                known[k] = true;
                matched++;
            }
            if (matched == 0) return result;

            for (int k = 0; k < nU; k++)
            {
                if (known[k]) continue;
                int prev = k - 1; while (prev >= 0 && !known[prev]) prev--;
                int next = k + 1; while (next < nU && !known[next]) next++;

                if (prev < 0)
                {
                    // Before the first matched word: count backwards from it
                    start[k] = Math.Max(0, start[next] - (next - k) * DefaultWordDuration);
                    end[k] = start[k] + DefaultWordDuration * 0.9;
                }
                else if (next >= nU)
                {
                    // After the last matched word: count forwards
                    start[k] = end[prev] + (k - prev - 1) * DefaultWordDuration;
                    end[k] = start[k] + DefaultWordDuration * 0.9;
                }
                else
                {
                    // Between two matched words: share the gap evenly
                    double t0 = end[prev], t1 = start[next];
                    if (t1 < t0) t1 = t0;
                    int slots = next - prev - 1;
                    double slot = (t1 - t0) / Math.Max(1, slots);
                    start[k] = t0 + slot * (k - prev - 1);
                    end[k] = start[k] + Math.Max(0.05, slot);
                }
            }

            // ── Group by line ───────────────────────────────────────────────────
            for (int li = 0; li < userLines.Count; li++)
            {
                var line = new AlignedLine { LineIndex = li, Text = userLines[li] ?? "" };
                for (int k = 0; k < nU; k++)
                {
                    if (user[k].Line != li) continue;
                    line.Words.Add(new AITranscription.WordTiming
                    {
                        Word = user[k].Original,
                        StartSecond = Math.Round(start[k], 3),
                        EndSecond = Math.Round(Math.Max(end[k], start[k] + 0.05), 3),
                        Probability = known[k] ? 1.0 : 0.0
                    });
                    if (known[k]) line.MatchedWords++;
                }
                if (line.Words.Count > 0)
                {
                    line.Start = line.Words[0].StartSecond;
                    line.End = line.Words[line.Words.Count - 1].EndSecond;
                }
                result.Lines.Add(line);
            }

            // Keep line starts monotonic (a badly matched line must not jump backwards)
            for (int li = 1; li < result.Lines.Count; li++)
            {
                var prevLine = result.Lines[li - 1];
                var cur = result.Lines[li];
                if (cur.Words.Count == 0) { cur.Start = prevLine.End; cur.End = prevLine.End; continue; }
                if (cur.Start < prevLine.Start) cur.Start = prevLine.Start + 0.05;
                if (cur.End < cur.Start) cur.End = cur.Start + DefaultWordDuration;
            }

            result.Confidence = (double)matched / nU;
            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        //  v1.4 — AlignSung: lyrics as they are SUNG (choruses repeated)
        // ════════════════════════════════════════════════════════════════════
        // Align() uses every typed line exactly once. Children's songs repeat the chorus
        // several times, but people type it once — so every repeat after the first got no
        // subtitle. AlignSung walks through what Whisper heard and, piece by piece, decides
        // which typed line was sung there (any line, any number of times, in any order).
        // Heard words that match no line (instrumental "hallucinations", ad-libs) are skipped.
        //
        // Dynamic programming over the heard words: best[j] = best score for the first j heard
        // words. From j we either skip one heard word (small penalty) or let a typed line
        // cover heard words j..k (its word-alignment score). Only windows where at least half
        // of the line's words match are allowed.

        private const double SkipHeardWordScore = -0.15;

        /// <summary>
        /// Returns the sung lines in time order. LineIndex points to the typed line, Text is the
        /// typed line (user's spelling), Words carry per-word times. Empty when nothing matched.
        /// </summary>
        public static List<AlignedLine> AlignSung(IList<string> userLines, IList<AITranscription.WordTiming> heard)
        {
            var sung = new List<AlignedLine>();
            if (userLines == null || userLines.Count == 0 || heard == null || heard.Count == 0) return sung;

            var lineTokens = new List<List<(string Original, string Norm)>>();
            foreach (string line in userLines)
            {
                var toks = new List<(string, string)>();
                foreach (string token in (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string n = SerbianText.NormalizeWord(token);
                    if (n.Length > 0) toks.Add((token, n));
                }
                lineTokens.Add(toks);
            }

            var words = new List<(string Norm, double Start, double End)>();
            foreach (var h in heard)
            {
                string n = SerbianText.NormalizeWord(h.Word);
                if (n.Length > 0) words.Add((n, h.StartSecond, Math.Max(h.EndSecond, h.StartSecond + 0.05)));
            }
            int N = words.Count;
            if (N == 0) return sung;

            var best = new double[N + 1];
            var from = new int[N + 1];
            var lineAt = new int[N + 1];
            var matchAt = new int[N + 1][];
            for (int i = 1; i <= N; i++) best[i] = double.NegativeInfinity;
            for (int i = 0; i <= N; i++) { from[i] = -1; lineAt[i] = -1; }

            for (int j = 0; j < N; j++)
            {
                if (double.IsNegativeInfinity(best[j])) continue;

                if (best[j] + SkipHeardWordScore > best[j + 1])
                {
                    best[j + 1] = best[j] + SkipHeardWordScore;
                    from[j + 1] = j; lineAt[j + 1] = -1; matchAt[j + 1] = null;
                }

                for (int li = 0; li < lineTokens.Count; li++)
                {
                    var toks = lineTokens[li];
                    int L = toks.Count;
                    if (L == 0) continue;
                    int kMin = j + Math.Max(1, L - 3), kMax = Math.Min(N, j + L + 4);
                    for (int k = kMin; k <= kMax; k++)
                    {
                        double score = AlignWindow(toks, words, j, k, out int[] match, out int matched);
                        if (matched < Math.Max(1, (L + 1) / 2)) continue;
                        if (best[j] + score > best[k])
                        {
                            best[k] = best[j] + score;
                            from[k] = j; lineAt[k] = li; matchAt[k] = match;
                        }
                    }
                }
            }

            // Walk back from the end
            for (int k = N; k > 0 && from[k] >= 0; k = from[k])
            {
                if (lineAt[k] < 0) continue;
                int j = from[k], li = lineAt[k];
                var toks = lineTokens[li];
                int[] match = matchAt[k];
                var line = new AlignedLine { LineIndex = li, Text = userLines[li] ?? "" };

                var start = new double[toks.Count];
                var end = new double[toks.Count];
                var known = new bool[toks.Count];
                for (int t = 0; t < toks.Count; t++)
                {
                    if (match[t] < 0) continue;
                    var w = words[j + match[t]];
                    start[t] = w.Start; end[t] = w.End; known[t] = true; line.MatchedWords++;
                }
                // Unmatched words share the time between their matched neighbours
                double windowStart = words[j].Start, windowEnd = words[k - 1].End;
                for (int t = 0; t < toks.Count; t++)
                {
                    if (known[t]) continue;
                    int p = t - 1; while (p >= 0 && !known[p]) p--;
                    int q = t + 1; while (q < toks.Count && !known[q]) q++;
                    double t0 = p >= 0 ? end[p] : windowStart;
                    double t1 = q < toks.Count ? start[q] : windowEnd;
                    if (t1 < t0) t1 = t0;
                    int slots = (q < toks.Count ? q : toks.Count) - (p >= 0 ? p : -1) - 1;
                    double slot = (t1 - t0) / Math.Max(1, slots);
                    start[t] = t0 + slot * (t - (p >= 0 ? p : -1) - 1);
                    end[t] = start[t] + Math.Max(0.05, slot);
                }
                for (int t = 0; t < toks.Count; t++)
                {
                    line.Words.Add(new AITranscription.WordTiming
                    {
                        Word = toks[t].Original,
                        StartSecond = Math.Round(start[t], 3),
                        EndSecond = Math.Round(Math.Max(end[t], start[t] + 0.05), 3),
                        Probability = known[t] ? 1.0 : 0.0
                    });
                }
                line.Start = line.Words[0].StartSecond;
                line.End = line.Words[line.Words.Count - 1].EndSecond;
                sung.Add(line);
            }
            sung.Reverse();
            return sung;
        }

        /// <summary>Global word alignment of one typed line against heard words [from, to).</summary>
        private static double AlignWindow(List<(string Original, string Norm)> toks,
                                          List<(string Norm, double Start, double End)> words,
                                          int from, int to, out int[] match, out int matched)
        {
            int n = toks.Count, m = to - from;
            var score = new double[n + 1, m + 1];
            var back = new byte[n + 1, m + 1];
            for (int i = 1; i <= n; i++) { score[i, 0] = i * GapScore; back[i, 0] = 1; }
            for (int j = 1; j <= m; j++) { score[0, j] = j * GapScore; back[0, j] = 2; }
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    double sim = SerbianText.Similarity(toks[i - 1].Norm, words[from + j - 1].Norm);
                    double diag = score[i - 1, j - 1] + (sim >= MatchThreshold ? 1.0 + sim : MismatchScore);
                    double up = score[i - 1, j] + GapScore;
                    double left = score[i, j - 1] + GapScore;
                    if (diag >= up && diag >= left) { score[i, j] = diag; back[i, j] = 0; }
                    else if (up >= left) { score[i, j] = up; back[i, j] = 1; }
                    else { score[i, j] = left; back[i, j] = 2; }
                }
            }

            match = new int[n];
            for (int t = 0; t < n; t++) match[t] = -1;
            matched = 0;
            int a = n, b = m;
            while (a > 0 || b > 0)
            {
                byte bk = back[a, b];
                if (a > 0 && b > 0 && bk == 0)
                {
                    if (SerbianText.Similarity(toks[a - 1].Norm, words[from + b - 1].Norm) >= MatchThreshold)
                    { match[a - 1] = b - 1; matched++; }
                    a--; b--;
                }
                else if (a > 0 && (b == 0 || bk == 1)) a--;
                else b--;
            }
            return score[n, m];
        }
    }
}
