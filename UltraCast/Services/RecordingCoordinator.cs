using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UltraCast.Models;

namespace UltraCast.Services
{
    /// <summary>
    /// Runs ScreenCaptureService (video-only MP4) and AudioLoopbackMixer
    /// (WAV) side by side during a recording, then muxes them into one
    /// final MP4 once recording stops. Two independent pipelines combined
    /// at the end, rather than one shared ffmpeg process - simpler to
    /// reason about and to debug from a build error report.
    /// </summary>
    public class RecordingCoordinator : IDisposable
    {
        private readonly ScreenCaptureService _video = new();
        private readonly AudioLoopbackMixer _audio = new();
        private string? _tempVideoPath;
        private string? _tempAudioPath;
        private string? _finalPath;

        public string FfmpegExePath
        {
            get => _video.FfmpegExePath;
            set => _video.FfmpegExePath = value;
        }

        public bool IsRecording { get; private set; }
        public bool IsPaused { get; private set; }

        public event Action<string>? Error;

        public void TogglePause()
        {
            if (!IsRecording) return;
            IsPaused = !IsPaused;
            _video.IsPaused = IsPaused;
            _audio.IsPaused = IsPaused;
        }

        public void Start(RecordingOptions options)
        {
            if (IsRecording) return;
            if (string.IsNullOrWhiteSpace(options.OutputFolder))
                throw new InvalidOperationException("No output folder chosen.");

            Directory.CreateDirectory(options.OutputFolder);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _finalPath = Path.Combine(options.OutputFolder, $"UltraCast_{stamp}.mp4");
            _tempVideoPath = Path.Combine(Path.GetTempPath(), $"ultracast_video_{stamp}.mp4");
            _tempAudioPath = Path.Combine(Path.GetTempPath(), $"ultracast_audio_{stamp}.wav");

            AppLogger.Log($"RecordingCoordinator: starting session. temp video={_tempVideoPath}, temp audio={_tempAudioPath}, final={_finalPath}");

            _video.CaptureError += msg => Error?.Invoke("Video: " + msg);
            _audio.CaptureError += msg => Error?.Invoke("Audio: " + msg);

            _video.Start(_tempVideoPath, options.FrameRate);
            _audio.Start(_tempAudioPath, options.CaptureSystemAudio, options.CaptureMicrophone);

            IsRecording = true;
            IsPaused = false;
        }

        /// <summary>Stops both pipelines and muxes them into the final MP4. Returns the finished file path.</summary>
        public async Task<string> StopAsync()
        {
            if (!IsRecording || _finalPath == null || _tempVideoPath == null || _tempAudioPath == null)
                throw new InvalidOperationException("Not currently recording.");

            IsRecording = false;
            IsPaused = false;

            await _video.StopAsync();
            await _audio.StopAsync();

            LogTempFileSize("video", _tempVideoPath);
            LogTempFileSize("audio", _tempAudioPath);

            await MuxAsync(_tempVideoPath, _tempAudioPath, _finalPath);

            TryDelete(_tempVideoPath);
            TryDelete(_tempAudioPath);

            AppLogger.Log("RecordingCoordinator: saved " + _finalPath);
            return _finalPath;
        }

        private static void LogTempFileSize(string label, string path)
        {
            try
            {
                var info = new FileInfo(path);
                AppLogger.Log(info.Exists
                    ? $"RecordingCoordinator: {label} temp file = {info.Length / 1024} KB"
                    : $"RecordingCoordinator: {label} temp file is missing! ({path})");
            }
            catch { /* diagnostic only */ }
        }

        private async Task MuxAsync(string videoPath, string audioPath, string outPath)
        {
            var args = $"-y -loglevel warning -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -b:a 192k -shortest \"{outPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            AppLogger.Log("RecordingCoordinator: muxing video+audio -> " + outPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start ffmpeg.exe to combine video and audio.");

            // IMPORTANT: must drain stderr WHILE the process runs, not after
            // WaitForExitAsync(). ffmpeg writes a lot to stderr (progress,
            // stream info); if that pipe's OS buffer fills up because
            // nothing is reading it, ffmpeg blocks trying to write to it -
            // and since we're simultaneously blocked waiting for ffmpeg to
            // exit, the two sides deadlock forever. This is exactly what
            // was happening: the log showed the mux step starting and then
            // nothing else, ever - the process was stuck, not slow.
            var stderrLines = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrLines.AppendLine(e.Data);
            };
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync();

            AppLogger.Log($"RecordingCoordinator: mux ffmpeg exited with code {proc.ExitCode}");

            if (proc.ExitCode != 0 || !File.Exists(outPath))
            {
                throw new InvalidOperationException("Combining video and audio failed: " + stderrLines);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
        }

        public void Dispose()
        {
            _video.Dispose();
            _audio.Dispose();
        }
    }
}
