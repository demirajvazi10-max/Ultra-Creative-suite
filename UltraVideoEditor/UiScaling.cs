using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UltraVideoEditor
{
    /// <summary>
    /// Application-wide text/UI scaling for Low Vision mode.
    ///
    /// How it works: every Window (MainWindow and every dialog) calls
    /// UiScaling.Register(this) once, right after InitializeComponent().
    /// Register() moves the window's real content one level deeper, into a
    /// plain Grid whose LayoutTransform is a ScaleTransform. At scale 1.0
    /// this is completely invisible — an extra Grid with an identity
    /// transform changes nothing about layout, so Sighted/Accessibility mode
    /// is unaffected.
    ///
    /// When Low Vision mode sets a scale > 1.0, every registered window
    /// (open now, or opened later) grows its content proportionally, and — for
    /// windows with an explicit design-time Width/Height — the window itself
    /// is resized to match so nothing gets clipped. Windows sized via
    /// SizeToContent need no manual resize: WPF measures around the
    /// transformed content automatically.
    ///
    /// NOTE: this does NOT cover the WindowsFormsHost-hosted native timeline
    /// ListView (WinForms controls ignore WPF LayoutTransform). That control
    /// scales separately — see MainWindow.ApplyNativeListViewScale().
    /// </summary>
    public static class UiScaling
    {
        private const double MinScale = 1.0;
        private const double MaxScale = 2.5;

        private static double _scale = 1.0;
        public static double CurrentScale => _scale;

        private sealed class WindowScaleState
        {
            public ScaleTransform Transform;
            public double BaseWidth;   // double.NaN => window is SizeToContent for width
            public double BaseHeight;  // double.NaN => window is SizeToContent for height
        }

        // ConditionalWeakTable so we never keep a window alive just because it's registered.
        private static readonly ConditionalWeakTable<Window, WindowScaleState> _states
            = new ConditionalWeakTable<Window, WindowScaleState>();

        // Separate weak list so SetScale can enumerate currently-open windows.
        private static readonly List<WeakReference<Window>> _openWindows
            = new List<WeakReference<Window>>();

        /// <summary>
        /// Wraps this window's content in a scaling container. Call once,
        /// immediately after InitializeComponent(). Safe to call multiple
        /// times (no-op after the first).
        /// </summary>
        public static void Register(Window window)
        {
            if (window == null) return;
            if (_states.TryGetValue(window, out _)) return;

            var originalContent = window.Content as UIElement;
            window.Content = null;

            var scaleRoot = new Grid();
            var transform = new ScaleTransform(1.0, 1.0);
            scaleRoot.LayoutTransform = transform;
            if (originalContent != null)
                scaleRoot.Children.Add(originalContent);

            // Wrapped in a ScrollViewer as a safety net: at 1.0x it's inert
            // (content exactly fills the window, so no scrollbars appear),
            // but if a window gets clamped to the screen's work area at high
            // scale (a design-time-1000px-tall dialog at 2.5x won't fit any
            // monitor), the user gets scrollbars instead of silently clipped
            // content.
            var scrollHost = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = scaleRoot
            };
            window.Content = scrollHost;

            var state = new WindowScaleState
            {
                Transform = transform,
                BaseWidth = double.IsNaN(window.Width) ? double.NaN : window.Width,
                BaseHeight = double.IsNaN(window.Height) ? double.NaN : window.Height,
            };
            _states.Add(window, state);
            _openWindows.Add(new WeakReference<Window>(window));

            // Covers dialogs opened while Low Vision mode is already active.
            if (_scale != 1.0)
                ApplyScaleToWindow(window, state, _scale);

            window.Closed += (s, e) =>
            {
                _states.Remove(window);
                _openWindows.RemoveAll(wr => !wr.TryGetTarget(out var w) || w == window);
            };
        }

        /// <summary>
        /// Sets the global scale factor (1.0 = normal) and immediately
        /// re-scales every currently open, registered window. Clamped to
        /// [1.0, 2.5].
        /// </summary>
        public static void SetScale(double scale)
        {
            scale = Math.Max(MinScale, Math.Min(MaxScale, scale));
            _scale = scale;

            _openWindows.RemoveAll(wr => !wr.TryGetTarget(out _));
            foreach (var wr in _openWindows.ToList())
            {
                if (wr.TryGetTarget(out var window) && _states.TryGetValue(window, out var state))
                    ApplyScaleToWindow(window, state, _scale);
            }
        }

        private static void ApplyScaleToWindow(Window window, WindowScaleState state, double scale)
        {
            state.Transform.ScaleX = scale;
            state.Transform.ScaleY = scale;

            var wa = SystemParameters.WorkArea;

            if (!double.IsNaN(state.BaseWidth))
                window.Width = Math.Min(state.BaseWidth * scale, wa.Width - 20);

            if (!double.IsNaN(state.BaseHeight))
                window.Height = Math.Min(state.BaseHeight * scale, wa.Height - 20);

            // Re-center centered windows so growth doesn't push them past the
            // screen edge (only meaningful once the window has a handle/size).
            if (window.WindowStartupLocation == WindowStartupLocation.CenterScreen
                && window.IsLoaded
                && (!double.IsNaN(state.BaseWidth) || !double.IsNaN(state.BaseHeight)))
            {
                window.Left = Math.Max(wa.Left, wa.Left + (wa.Width - window.ActualWidth) / 2);
                window.Top = Math.Max(wa.Top, wa.Top + (wa.Height - window.ActualHeight) / 2);
            }
        }
    }
}
