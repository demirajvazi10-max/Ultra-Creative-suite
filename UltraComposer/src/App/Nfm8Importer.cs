using System.IO;
using System.Text;

namespace UltraComposer.App;

/// <summary>
/// Imports a Native Instruments FM8 preset (.nfm8) into Ultra FM.
///
/// The file layout was worked out by comparing 61 real presets; see
/// docs/fm8-import/NFM8-FORMAT-NOTES.md for every detail and for what is
/// still unknown. In short:
///   1. A 9-byte header (1, uncompressed size, compressed size) followed by
///      an LZF stream holds the preset body.
///   2. In the body, the "E8MF" chunk: version, a constant, 4 snapshot names,
///      then the parameter block.
///   3. Parameter block: 214 floats + fixed bytes up to offset 1145, then
///      4 operator records of 90 bytes (one per morph snapshot; the ratios of
///      A-F, X, Z and pitch are 9 floats at record offset 18), then at 1505
///      51 arrays (u8 n, then n+1 floats): 9 envelope time lists (ms),
///      9 envelope level lists, 9 slope lists and 3x8 keyscaling/velocity
///      lists, then the FM matrix: 8 rows (A-F, X, Z = source) x 11 columns
///      (0-7 = destination A-F, X, Z with the diagonal as feedback,
///      8 = output level), each cell 4 bytes (one per snapshot), values 0-100.
///
/// FM8 has 8 operators and a free matrix; Ultra FM has 6 operators and
/// 8 fixed algorithms. We keep operators A-F (X = noise, Z = filter are
/// skipped), try every algorithm with every way of placing A-F on the 6
/// slots, and keep the one that reproduces the most modulation and output.
/// Snapshot 0 is used (in presets with differing snapshots, that is the
/// one the user edited).
/// </summary>
public static class Nfm8Importer
{
    public sealed class Result
    {
        public string PresetName = "";
        public int Algorithm;
        public readonly List<(int Id, float Value)> Values = new();
        /// <summary>True when X/Z operators carried part of the sound (they are skipped).</summary>
        public bool UsedNoiseOrFilter;
        /// <summary>True when an operator ratio was outside Ultra FM's 0.5-16 range.</summary>
        public bool RatioClamped;
    }

    private const int Snapshot = 0;
    private const int OpRecordStart = 1145, OpRecordSize = 90, OpRatioOffset = 18;
    private const int ArraysStart = 1505, ArrayCount = 51, MatrixBytes = 496;

    // Ultra FM algorithms, same table as src/AudioEngine/FmSynth.cpp:
    // carriers bit i = slot i heard; Mods[i] bit j = slot j modulates slot i.
    private static readonly (int Carriers, int[] Mods)[] Algorithms =
    {
        (0b000001, new[] { 0b10, 0b100, 0b1000, 0b10000, 0b100000, 0 }),
        (0b000101, new[] { 0b10, 0, 0b1000, 0b10000, 0b100000, 0 }),
        (0b010101, new[] { 0b10, 0, 0b1000, 0, 0b100000, 0 }),
        (0b001001, new[] { 0b10, 0b100, 0, 0b10000, 0b100000, 0 }),
        (0b001001, new[] { 0b110, 0, 0, 0b110000, 0, 0 }),
        (0b011111, new[] { 0b100000, 0b100000, 0b100000, 0b100000, 0b100000, 0 }),
        (0b111111, new[] { 0, 0, 0, 0, 0, 0 }),
        (0b111101, new[] { 0b10, 0, 0, 0, 0, 0 }),
    };

    /// <summary>Reads and converts the file. Throws InvalidDataException when it is not a readable FM8 preset.</summary>
    public static Result Import(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] body = FindCompressedBody(raw);
        int e = IndexOf(body, Encoding.ASCII.GetBytes("E8MF"));
        if (e < 0) throw new InvalidDataException("No E8MF chunk.");

        var result = new Result();
        int p = e + 12;
        for (int i = 0; i < 4; i++)
        {
            int n = ReadInt(body, p);
            if (n < 0 || p + 4 + n > body.Length) throw new InvalidDataException("Bad snapshot name.");
            if (i == 0) result.PresetName = Encoding.Latin1.GetString(body, p + 4, n).Trim();
            p += 4 + n;
        }
        byte[] block = body[p..];
        if (block.Length < ArraysStart + 200) throw new InvalidDataException("Parameter block too short.");

        // Ratios of A-F from the snapshot record.
        var ratio = new float[6];
        int rec = OpRecordStart + Snapshot * OpRecordSize + OpRatioOffset;
        for (int f = 0; f < 6; f++) ratio[f] = ReadFloat(block, rec + 4 * f);

        // 51 arrays: [0..8] times (ms), [9..17] levels.
        var arrays = new List<float[]>(ArrayCount);
        int q = ArraysStart;
        for (int a = 0; a < ArrayCount; a++)
        {
            int n = block[q];
            if (q + 1 + 4 * (n + 1) > block.Length) throw new InvalidDataException("Envelope data cut off.");
            var values = new float[n + 1];
            for (int i = 0; i <= n; i++) values[i] = ReadFloat(block, q + 1 + 4 * i);
            arrays.Add(values);
            q += 1 + 4 * (n + 1);
        }
        if (q + MatrixBytes > block.Length) throw new InvalidDataException("Matrix cut off.");
        int Cell(int row, int col) => block[q + (row * 11 + col) * 4 + Snapshot];

        // m[src, dst] for A-F, outputs, feedback.
        var m = new int[6, 6];
        var output = new int[6];
        for (int s = 0; s < 6; s++)
        {
            output[s] = Cell(s, 8);
            for (int d = 0; d < 6; d++) m[s, d] = Cell(s, d);
        }
        for (int s = 0; s < 8; s++)
            for (int d = 0; d < 9; d++)
                if (Cell(s, d) > 0 && (s >= 6 || (d >= 6 && d < 8))) result.UsedNoiseOrFilter = true;

        // Best algorithm + placement of A-F on the six slots.
        double bestScore = -1; int bestAlg = 0; int[] bestPerm = { 0, 1, 2, 3, 4, 5 };
        foreach (int[] perm in Permutations(6))
        {
            for (int alg = 0; alg < Algorithms.Length; alg++)
            {
                var (car, mods) = Algorithms[alg];
                double score = 0;
                for (int i = 0; i < 6; i++)
                {
                    if (((car >> i) & 1) != 0) score += output[perm[i]];
                    for (int j = 0; j < 6; j++)
                        if (((mods[i] >> j) & 1) != 0) score += m[perm[j], perm[i]];
                }
                score += 0.3 * m[perm[5], perm[5]]; // Ultra FM feedback lives on operator 6
                if (score > bestScore) { bestScore = score; bestAlg = alg; bestPerm = perm; }
            }
        }

        result.Algorithm = bestAlg;
        var (carriers, modMasks) = Algorithms[bestAlg];
        result.Values.Add((SoundParameters.FmAlgorithm, bestAlg));
        result.Values.Add((SoundParameters.FmFeedback, Math.Clamp(m[bestPerm[5], bestPerm[5]] / 100f, 0f, 1f)));
        result.Values.Add((SoundParameters.FmVolume, 0.8f));
        result.Values.Add((SoundParameters.FmBrightness, 1f));

        for (int slot = 0; slot < 6; slot++)
        {
            int f = bestPerm[slot];
            float level;
            if (((carriers >> slot) & 1) != 0)
                level = output[f] / 100f;
            else
            {
                int strongest = 0;
                for (int d = 0; d < 6; d++)
                    if (((modMasks[d] >> slot) & 1) != 0) strongest = Math.Max(strongest, m[f, bestPerm[d]]);
                level = strongest / 100f;
            }

            float r = ratio[f];
            if (!float.IsFinite(r) || r <= 0) r = 1f;
            if (r < 0.5f || r > 16f) result.RatioClamped = true;
            r = Math.Clamp(r, 0.5f, 16f);

            // Envelope: times[n] is the release segment, levels[n-1] the level held before it.
            float[] t = arrays[f], lv = arrays[9 + f];
            int n = t.Length - 1;
            float attack = Ms(t[0]);
            float decay = 0.05f;
            if (n >= 2) { decay = 0; for (int i = 1; i < n; i++) decay += Ms(t[i]); }
            float sustain = n >= 1 && lv.Length >= n ? lv[n - 1] : 1f;
            float release = Ms(t[n]);

            int b = SoundParameters.FmOpBase + slot * 10;
            result.Values.Add((b + 0, Math.Clamp(level, 0f, 1f)));
            result.Values.Add((b + 1, r));
            result.Values.Add((b + 2, 0f));
            result.Values.Add((b + 3, Math.Clamp(attack, 0.001f, 5f)));
            result.Values.Add((b + 4, Math.Clamp(decay, 0.01f, 10f)));
            result.Values.Add((b + 5, Math.Clamp(float.IsFinite(sustain) ? sustain : 1f, 0f, 1f)));
            result.Values.Add((b + 6, Math.Clamp(release, 0.01f, 4f)));
            result.Values.Add((b + 7, 0.5f));
        }
        return result;
    }

    private static float Ms(float ms) => float.IsFinite(ms) && ms > 0 ? ms / 1000f : 0f;

    // ---- container / LZF ----

    private static byte[] FindCompressedBody(byte[] d)
    {
        for (int p = 0; p + 9 < d.Length; p++)
        {
            if (d[p] != 1) continue;
            int usize = ReadInt(d, p + 1), csize = ReadInt(d, p + 5);
            if (usize <= 100 || usize >= 1_000_000 || csize <= 10 || csize >= usize || csize > d.Length - p - 9) continue;
            byte[]? body = Lzf(d, p + 9, p + 9 + csize, usize);
            if (body != null) return body;
        }
        throw new InvalidDataException("Not an FM8 preset (no compressed body found).");
    }

    /// <summary>Plain LZF; returns null unless the output is exactly <paramref name="expected"/> bytes.</summary>
    private static byte[]? Lzf(byte[] d, int i, int end, int expected)
    {
        var o = new byte[expected];
        int op = 0;
        while (i < end)
        {
            int ctrl = d[i++];
            if (ctrl < 32)
            {
                int n = ctrl + 1;
                if (i + n > end || op + n > expected) return null;
                Array.Copy(d, i, o, op, n); i += n; op += n;
            }
            else
            {
                int len = ctrl >> 5;
                if (len == 7) { if (i >= end) return null; len += d[i++]; }
                if (i >= end) return null;
                int r = op - ((ctrl & 0x1F) << 8) - d[i++] - 1;
                if (r < 0 || op + len + 2 > expected) return null;
                for (int k = 0; k < len + 2; k++) o[op++] = o[r++];
            }
        }
        return op == expected ? o : null;
    }

    private static int ReadInt(byte[] d, int p) => p + 4 <= d.Length ? BitConverter.ToInt32(d, p) : -1;
    private static float ReadFloat(byte[] d, int p) => p + 4 <= d.Length ? BitConverter.ToSingle(d, p) : 0f;

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        IEnumerable<int[]> Rec(int k)
        {
            if (k == n) { yield return (int[])a.Clone(); yield break; }
            for (int i = k; i < n; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var x in Rec(k + 1)) yield return x;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
        return Rec(0);
    }
}
