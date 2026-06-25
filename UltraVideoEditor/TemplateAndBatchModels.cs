using System;
using System.Collections.Generic;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // PROJECT TEMPLATE — stores settings without clips
    // ═══════════════════════════════════════════════════════════════

    public class ProjectTemplate
    {
        public string  Name            { get; set; } = "Novi template";
        public string  Description     { get; set; } = "";
        public string  Version         { get; set; } = "1.0";
        public DateTime CreatedAt      { get; set; } = DateTime.Now;

        // Export settings
        public string  ExportFormat    { get; set; } = "MP4";   // MP4, YouTube, Reels, MP3
        public string  ExportQuality   { get; set; } = "Medium";
        public string  ExportResolution{ get; set; } = "1920x1080";
        public int     ExportWidth     { get; set; } = 1920;
        public int     ExportHeight    { get; set; } = 1080;
        public int     ExportBitrate   { get; set; } = 8000;
        public int     ExportFrameRate { get; set; } = 30;
        public bool    ExportYouTube   { get; set; } = true;
        public bool    ExportReels     { get; set; } = false;
        public bool    ExportMP3       { get; set; } = false;
        public bool    ExportTxt       { get; set; } = false;

        // AI settings
        public string  ColorGradePreset{ get; set; } = "Auto";  // GradePreset.ToString()
        public string  Language        { get; set; } = "sr";
        public bool    UseGPU          { get; set; } = false;
        public bool    FastRender      { get; set; } = false;
        public bool    EnableSubtitles { get; set; } = false;

        // Audio
        public string  DefaultMusicPath{ get; set; } = "";
        public double  MusicVolume     { get; set; } = 1.0;

        // Timeline settings
        public double  ZoomLevel       { get; set; } = 1.0;
        public int     TrackFilter     { get; set; } = -1;
    }

    // ═══════════════════════════════════════════════════════════════
    // BATCH EXPORT — lista poslova za sekvencijalni render
    // ═══════════════════════════════════════════════════════════════

    public enum BatchJobStatus
    {
        Pending,
        Running,
        Done,
        Error,
        Skipped,
    }

    public class BatchExportJob
    {
        public string         ProjectPath  { get; set; } = "";   // .iskra fajl
        public string         OutputFolder { get; set; } = "";
        public string         FormatId     { get; set; } = "youtube"; // youtube/reels/mp3
        public BatchJobStatus Status       { get; set; } = BatchJobStatus.Pending;
        public string         ErrorMessage { get; set; } = "";
        public double         ProgressPct  { get; set; } = 0;
        public string         OutputFile   { get; set; } = "";   // output file

        // Computed
        public string ProjectName => System.IO.Path.GetFileNameWithoutExtension(ProjectPath);
        public string StatusLabel => Status switch
        {
            BatchJobStatus.Pending  => "⏳ Pending",
            BatchJobStatus.Running  => "▶ Render…",
            BatchJobStatus.Done     => "✅ Gotovo",
            BatchJobStatus.Error    => "❌ Error",
            BatchJobStatus.Skipped  => "⏭ Skipped",
            _                       => "?"
        };
    }
}
