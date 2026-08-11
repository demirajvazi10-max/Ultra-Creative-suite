namespace UltraCast.Models
{
    /// <summary>
    /// Everything the capture pipeline needs for one recording session.
    /// Kept as a plain data holder - MainViewModel owns the live/observable
    /// copies, this is just what gets handed to the services when
    /// recording starts.
    /// </summary>
    public class RecordingOptions
    {
        public string OutputFolder { get; set; } = "";

        /// <summary>
        /// Frames per second for the screen capture. Deliberately modest
        /// (not 60) - this is a tutorial/walkthrough recorder, not a game
        /// capture tool, and a lower FPS keeps the raw-frame pipe to
        /// ffmpeg light enough to stay reliable on modest hardware.
        /// </summary>
        public int FrameRate { get; set; } = 12;

        /// <summary>
        /// Capture "what you hear" - system output via WASAPI loopback.
        /// This is what naturally picks up JAWS/NVDA speech and any other
        /// app audio, since screen-reader speech is just audio routed to
        /// the default output device like anything else - no special
        /// screen-reader API/hook needed.
        /// </summary>
        public bool CaptureSystemAudio { get; set; } = true;

        /// <summary>
        /// Also mix in the microphone, for spoken narration on top of
        /// whatever the screen reader is saying.
        /// </summary>
        public bool CaptureMicrophone { get; set; } = true;
    }
}
