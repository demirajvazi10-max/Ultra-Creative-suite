namespace UltraVideoEditor
{
    /// <summary>
    /// Reads the current theme's brushes live from Application resources.
    /// Use this instead of hardcoding colors in code-behind (e.g. when building
    /// list rows or other UI dynamically), so that content built in C# also
    /// responds to Dark / High Contrast / Light theme switching, the same way
    /// XAML's DynamicResource does.
    /// </summary>
    public static class ThemeBrushes
    {
        private static System.Windows.Media.Brush Get(string key, string fallbackHex)
        {
            var res = System.Windows.Application.Current?.Resources[key] as System.Windows.Media.Brush;
            if (res != null) return res;
            try { return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(fallbackHex); }
            catch { return System.Windows.Media.Brushes.Gray; }
        }

        public static System.Windows.Media.Brush MainWindowBg    => Get("MainWindowBg", "#0A0A0A");
        public static System.Windows.Media.Brush WindowBg        => Get("WindowBg", "#1E1E1E");
        public static System.Windows.Media.Brush PanelBg         => Get("PanelBg", "#1E1E1E");
        public static System.Windows.Media.Brush PanelBg2        => Get("PanelBg2", "#2A2A2A");
        public static System.Windows.Media.Brush ControlBg       => Get("ControlBg", "#2D2D2D");
        public static System.Windows.Media.Brush TextPrimary     => Get("TextPrimary", "#FFFFFF");
        public static System.Windows.Media.Brush TextSecondary   => Get("TextSecondary", "#AAAAAA");
        public static System.Windows.Media.Brush AccentBrush     => Get("AccentBrush", "#00E676");
        public static System.Windows.Media.Brush ApplyBrush      => Get("ApplyBrush", "#2E7D32");
        public static System.Windows.Media.Brush CancelBrush     => Get("CancelBrush", "#B71C1C");
        public static System.Windows.Media.Brush WarningBrush    => Get("WarningBrush", "#FF9800");
        public static System.Windows.Media.Brush PurpleBrush     => Get("PurpleBrush", "#4A148C");
        public static System.Windows.Media.Brush ButtonTextBrush => Get("ButtonTextBrush", "#FFFFFF");
        public static System.Windows.Media.Brush ThemeBorderBrush=> Get("ThemeBorderBrush", "#333333");
    }
}
