using System;
using System.Collections.Generic;
using System.Text;

namespace UltraVideoEditor
{
    /// <summary>
    /// v1.4.5 — the "Sec-MS-GEC" token the Microsoft Edge read-aloud voice service asks for
    /// (version 2: "&lt;16 hex&gt;-&lt;64 hex&gt;"). A C# port of the open-source edge-tts project
    /// (github.com/rany2/edge-tts, drm.py, LGPL-3.0/GPL-3.0 compatible), checked against its output.
    /// The token depends only on the time (in 5-minute steps).
    /// </summary>
    internal static class EdgeTtsToken
    {
        public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        public const string ChromiumVersion = "143.0.3650.75";
        public const string GecVersion = "2-" + ChromiumVersion;
        private const double WinEpoch = 11644473600;

        /// <summary>Seconds to add to this computer's clock (set from the server's Date header when the clock is off).</summary>
        public static double ClockSkewSeconds;

        private static readonly uint[] Initial = { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
        private static readonly uint[] State0 = { 0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2 };
        private static readonly uint[] Schedule = { 0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6,
                                                    0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564 };
        private static readonly uint[] K =
        {
            0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
            0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
            0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
            0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
            0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
            0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
            0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
            0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2
        };
        private const uint Adjustment = 0xC9DC06A7;
        private const uint Sigma0Adjustment = 0x059C0B80;
        private const uint Sigma1Adjustment = 0x000B92C0;
        private static readonly (uint Coef, uint Offset)[] Cs64Coefficients =
        {
            (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
            (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
            (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
            (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167)
        };
        private const uint Cs64Factor = 0x82893933, Cs64Adjustment0 = 0xF2F90B1A, Cs64Adjustment1 = 0x0B1A0000;

        private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
        private static uint Rol16(uint x) => (x << 16) | (x >> 16);
        private static uint SmallSigma0(uint x) => Rotr(x, 7) ^ Rotr(x, 18) ^ (x >> 3);
        private static uint SmallSigma1(uint x) => Rotr(x, 17) ^ Rotr(x, 19) ^ (x >> 10);

        /// <summary>The token for now (or for a given Unix time, for testing).</summary>
        public static string Generate(double? unixTime = null)
        {
            double ticks = unixTime ?? (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + ClockSkewSeconds;
            ticks += WinEpoch;
            ticks -= ticks % 300;
            ticks *= 1e9 / 100;
            byte[] data = Encoding.ASCII.GetBytes(((long)ticks).ToString(System.Globalization.CultureInfo.InvariantCulture) + TrustedClientToken);
            byte[] digest = Sha256Digest(data);
            var (c0, c1) = Cs64Digest(data, digest);
            var cs = new byte[8];
            BitConverter.TryWriteBytes(cs.AsSpan(0, 4), c0);
            BitConverter.TryWriteBytes(cs.AsSpan(4, 4), c1);
            return Convert.ToHexString(cs) + "-" + Convert.ToHexString(digest);
        }

        private static void Rounds(uint[] state, uint[] w, int first, uint[] constants)
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
            unchecked
            {
                for (int i = first; i < 64; i++)
                {
                    uint t1 = h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + (g ^ (e & (f ^ g))) + constants[i] + w[i];
                    uint t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) | (c & (a | b)));
                    h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
                }
            }
            state[0] = a; state[1] = b; state[2] = c; state[3] = d; state[4] = e; state[5] = f; state[6] = g; state[7] = h;
        }

        private static void Expand(uint[] w, int from)
        {
            unchecked
            {
                for (int i = from; i < 64; i++)
                    w[i] = SmallSigma1(w[i - 2]) + w[i - 7] + SmallSigma0(w[i - 15]) + w[i - 16];
            }
        }

        private static uint[] InitState(byte[] first7)
        {
            var w = new uint[64];
            w[14] = (uint)((first7[0] << 16) | (first7[1] << 8) | first7[2]);
            w[15] = (uint)((first7[3] << 24) | (first7[4] << 16) | (first7[5] << 8) | first7[6]);
            unchecked
            {
                for (int i = 16; i < 30; i++)
                {
                    uint v = Schedule[i - 16];
                    if (i - 2 >= 14) v += i == 16 ? (SmallSigma1(w[i - 2]) ^ Sigma1Adjustment) : SmallSigma1(w[i - 2]);
                    if (i - 7 >= 14) v += w[i - 7];
                    if (i - 15 >= 14) v += i == 29 ? (SmallSigma0(w[i - 15]) ^ Sigma0Adjustment) : SmallSigma0(w[i - 15]);
                    w[i] = v;
                }
                w[30] = SmallSigma1(w[28]) + w[23] + SmallSigma0(w[15]) + w[14] + Adjustment - K[14];
            }
            Expand(w, 31);
            var blockConstants = (uint[])K.Clone();
            blockConstants[14] = Adjustment;
            var working = (uint[])State0.Clone();
            Rounds(working, w, 14, blockConstants);
            var result = new uint[8];
            unchecked { for (int i = 0; i < 8; i++) result[i] = Initial[i] + working[i]; }
            return result;
        }

        private static byte[] Sha256Digest(byte[] data)
        {
            int length = 57 + data.Length;
            int zeros = ((55 - length) % 64 + 64) % 64;
            var padded = new List<byte>();
            for (int i = 7; i < data.Length; i++) padded.Add(data[i]);
            padded.Add(0x80);
            for (int i = 0; i < zeros; i++) padded.Add(0);
            ulong bits = (ulong)length * 8;
            for (int i = 7; i >= 0; i--) padded.Add((byte)(bits >> (8 * i)));

            uint[] state = InitState(data);
            var w = new uint[64];
            for (int off = 0; off < padded.Count; off += 64)
            {
                for (int i = 0; i < 16; i++)
                    w[i] = (uint)((padded[off + 4 * i] << 24) | (padded[off + 4 * i + 1] << 16) | (padded[off + 4 * i + 2] << 8) | padded[off + 4 * i + 3]);
                Expand(w, 16);
                var working = (uint[])state.Clone();
                Rounds(working, w, 0, K);
                unchecked { for (int i = 0; i < 8; i++) state[i] += working[i]; }
            }
            var digest = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                digest[4 * i] = (byte)(state[i] >> 24); digest[4 * i + 1] = (byte)(state[i] >> 16);
                digest[4 * i + 2] = (byte)(state[i] >> 8); digest[4 * i + 3] = (byte)state[i];
            }
            return digest;
        }

        private static (uint, uint) Cs64Digest(byte[] data, byte[] digest)
        {
            unchecked
            {
                uint keyA = BitConverter.ToUInt32(digest, 0) | 1;
                uint keyB = BitConverter.ToUInt32(digest, 4) | 1;
                uint[] factors = { keyA * Cs64Factor, keyB * Cs64Factor };
                var terms = new List<uint>();
                var offsets = new List<uint>();
                for (int i = 0; i < Cs64Coefficients.Length; i++)
                {
                    terms.Add(Cs64Coefficients[i].Coef * factors[i % 2]);
                    offsets.Add(Cs64Coefficients[i].Offset);
                }
                // little-endian words of (0x00 + data), zero-padded, even count
                var bytes = new List<byte> { 0 };
                bytes.AddRange(data);
                while (bytes.Count % 4 != 0) bytes.Add(0);
                var words = new List<uint>();
                for (int i = 0; i < bytes.Count; i += 4)
                    words.Add((uint)(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24)));
                if (words.Count % 2 == 1) words.Add(0);
                for (int i = 0; i < words.Count; i++)
                {
                    uint word = words[i];
                    uint term = word * (i % 2 == 0 ? keyA : keyB);
                    uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FB : 0xC31713DB);
                    if (i == 0) { term += Cs64Adjustment0 * factors[0]; offset += Cs64Adjustment1; }
                    terms.Add(term);
                    offsets.Add(offset);
                }
                uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
                for (int i = 0; i < terms.Count; i += 2)
                {
                    uint x = terms[i], y = terms[i + 1];
                    uint t = Rol16(offsets[i] + x);
                    uint q = t * 0x76F50F34;
                    swapAcc += Rol16(q) * 0xC3EFEA97 + q;
                    t = Rol16(offsets[i + 1] + y);
                    q = t * 0x1881CE3C;
                    swapLast = Rol16(q) * 0x35BD1EC9 + q;
                    swapAcc += swapLast;

                    state = state * keyA + x;
                    state = Rol16(state) * 0xCF98B111;
                    state = Rol16(state) * 0x87085B9F;
                    state = Rol16(state) * 0x12CEB96D;
                    state = Rol16(state) * 0x257E1D83;
                    acc += state;
                    t = state * keyB + y;
                    t = Rol16(t) * 0xA27416F5;
                    t = Rol16(t) * 0xD38396FF;
                    state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
                    acc += state;
                }
                return (swapLast ^ state, swapAcc ^ acc);
            }
        }
    }
}
