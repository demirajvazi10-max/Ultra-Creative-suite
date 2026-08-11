using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UltraCast.Services
{
    /// <summary>
    /// Registers system-wide hotkeys via RegisterHotKey/WM_HOTKEY, so
    /// recording can be started/stopped/paused while working in whatever
    /// window is being demonstrated - the whole point of a screen recorder
    /// is that the user is NOT expected to keep Ultra Cast focused while
    /// they work.
    /// </summary>
    public class GlobalHotkeyService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;

        // Arbitrary VK codes: R = 0x52, P = 0x50, S handled by the same
        // toggle as R (Start/Stop share one hotkey, simpler to remember).
        private const uint VK_R = 0x52;
        private const uint VK_P = 0x50;

        private const int HOTKEY_ID_TOGGLE = 1;
        private const int HOTKEY_ID_PAUSE = 2;

        private HwndSource? _source;
        private IntPtr _hwnd;

        public event Action? ToggleRequested;
        public event Action? PauseToggleRequested;

        public void Register(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            // Ctrl+Alt+R = start/stop recording, Ctrl+Alt+P = pause/resume.
            RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE, MOD_CONTROL | MOD_ALT, VK_R);
            RegisterHotKey(_hwnd, HOTKEY_ID_PAUSE, MOD_CONTROL | MOD_ALT, VK_P);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_ID_TOGGLE)
                {
                    ToggleRequested?.Invoke();
                    handled = true;
                }
                else if (id == HOTKEY_ID_PAUSE)
                {
                    PauseToggleRequested?.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE);
                    UnregisterHotKey(_hwnd, HOTKEY_ID_PAUSE);
                }
                _source?.RemoveHook(WndProc);
            }
            catch (InvalidOperationException)
            {
                // Harmless if the window/HwndSource is already mid-teardown -
                // there's nothing left to unhook from at that point.
            }
        }
    }
}
