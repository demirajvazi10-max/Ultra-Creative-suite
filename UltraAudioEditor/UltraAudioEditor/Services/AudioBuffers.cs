using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Services
{
    /// <summary>
    /// Čitanje dela trake u memoriju (stereo, float, frekvencija projekta) i upis
    /// obrađenog zvuka u novi WAV. Osnova svih "Sound Forge" obrada: izabrani deo se
    /// pročita, obradi, upiše u novi fajl i vrati na traku kao jedan klip.
    /// Originalni fajlovi se NIKAD ne menjaju — Undo samo vraća stare putanje.
    /// </summary>
    public static class AudioBuffers
    {
        public static string RendersFolder()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Ultra Audio Editor", "Renders");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Otvara fajl kao stereo float na zadatoj frekvenciji.</summary>
        public static (ISampleProvider provider, IDisposable owner, double totalSeconds) OpenStereo(string path, int sampleRate)
        {
            var reader = new AudioFileReader(path);
            ISampleProvider sp = reader.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(reader),
                2 => reader,
                _ => new MultiplexingSampleProvider(new ISampleProvider[] { reader }, 2)
            };
            if (sp.WaveFormat.SampleRate != sampleRate) sp = new WdlResamplingSampleProvider(sp, sampleRate);
            return (sp, reader, reader.TotalTime.TotalSeconds);
        }

        /// <summary>
        /// Zvuk jedne trake u vremenskom opsegu [from, to] (sekunde na vremenskoj liniji),
        /// bez efekata, mute-a i jačine trake — "sirovi" zvuk klipova. Prazna mesta su tišina.
        /// </summary>
        public static float[] ReadTrackRange(AudioTrack track, double from, double to, int sr)
        {
            int frames = Math.Max(0, (int)Math.Round((to - from) * sr));
            var outBuf = new float[frames * 2];
            foreach (var clip in track.Clips.ToList())
            {
                double s = Math.Max(from, clip.StartTime), e = Math.Min(to, clip.StartTime + clip.Duration);
                if (e <= s || !File.Exists(clip.FilePath)) continue;
                try
                {
                    var (sp, owner, _) = OpenStereo(clip.FilePath, sr);
                    using (owner)
                    {
                        double srcStart = clip.TrimStart + (s - clip.StartTime);
                        if (owner is AudioFileReader afr) afr.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, srcStart));
                        int dstFrame = (int)Math.Round((s - from) * sr);
                        int want = Math.Min(frames - dstFrame, (int)Math.Round((e - s) * sr));
                        var tmp = new float[want * 2];
                        int got = 0, n;
                        while (got < tmp.Length && (n = sp.Read(tmp, got, tmp.Length - got)) > 0) got += n;
                        for (int i = 0; i < got && (dstFrame * 2 + i) < outBuf.Length; i++) outBuf[dstFrame * 2 + i] += tmp[i];
                    }
                }
                catch (Exception ex) { DebugLog.Write($"ReadTrackRange: '{clip.Name}' skipped: {ex.Message}"); }
            }
            return outBuf;
        }

        /// <summary>Upisuje stereo float bafer u novi 32-bit float WAV i vraća putanju.</summary>
        public static string WriteRender(float[] st, int sr, string label)
        {
            string safe = new string(label.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-').ToArray());
            if (safe.Length == 0) safe = "render";
            string path = Path.Combine(RendersFolder(), $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.wav");
            using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sr, 2));
            w.WriteSamples(st, 0, st.Length);
            return path;
        }

        /// <summary>Čita ceo fajl (stereo, frekvencija projekta) u memoriju — za grupnu konverziju.</summary>
        public static float[] ReadWholeFile(string path, int sr)
        {
            var (sp, owner, total) = OpenStereo(path, sr);
            using (owner)
            {
                var list = new List<float>((int)(total * sr * 2) + 1024);
                var buf = new float[sr * 2];
                int n;
                while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < n; i++) list.Add(buf[i]);
                return list.ToArray();
            }
        }
    }

    /// <summary>Bafer u memoriji kao ISampleProvider (za propuštanje kroz lanac efekata).</summary>
    public sealed class BufferSampleProvider : ISampleProvider
    {
        private readonly float[] _data; private int _pos;
        public BufferSampleProvider(float[] data, int sr) { _data = data; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sr, 2); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
