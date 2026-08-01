using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UltraAudioEditor.Controls
{
    /// <summary>
    /// Win32 native kontekstni meni — JAWS ga čita savršeno (identičan File Explorer meniju).
    /// WPF ContextMenu otvoren programski JAWS ne čita; ovaj uvijek radi.
    /// </summary>
    public class NativeContextMenu : IDisposable
    {
        // Win32 API
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")]
        static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        // uFlags za AppendMenu
        const uint MF_STRING    = 0x00000000;
        const uint MF_SEPARATOR = 0x00000800;
        const uint MF_GRAYED    = 0x00000001;
        const uint MF_POPUP     = 0x00000010;

        // uFlags za TrackPopupMenuEx
        const uint TPM_RETURNCMD    = 0x0100;
        const uint TPM_LEFTALIGN    = 0x0000;
        const uint TPM_LEFTBUTTON   = 0x0000;
        const uint TPM_NONOTIFY     = 0x0080;

        private IntPtr _hMenu;
        private readonly List<NativeMenuItem> _items = new();
        private int _nextId = 1;

        public NativeContextMenu()
        {
            _hMenu = CreatePopupMenu();
        }

        // ── Dodavanje stavki ──────────────────────────────────────────────

        public NativeMenuItem AddItem(string text, Action action)
        {
            var item = new NativeMenuItem(_nextId++, text, action);
            _items.Add(item);
            AppendMenu(_hMenu, MF_STRING, (IntPtr)item.Id, text);
            return item;
        }

        public void AddSeparator()
        {
            AppendMenu(_hMenu, MF_SEPARATOR, IntPtr.Zero, null!);
        }

        public void AddHeader(string text)
        {
            // Header = disabled, bold nije dostupan u Win32 popup bez owner-draw
            // Koristimo disabled stavku sa razmacima
            AppendMenu(_hMenu, MF_STRING | MF_GRAYED, IntPtr.Zero, text);
        }

        public NativeContextMenu AddSubMenu(string text)
        {
            var sub = new NativeContextMenu();
            // Registruj podmeni kao MF_POPUP
            AppendMenu(_hMenu, MF_STRING | MF_POPUP, sub._hMenu, text);
            return sub;
        }

        // ── Prikaži meni i vrati kliknutu akciju ──────────────────────────

        public void Show(Window owner)
        {
            if (_hMenu == IntPtr.Zero) return;

            var hwnd = new WindowInteropHelper(owner).Handle;
            SetForegroundWindow(hwnd);

            GetCursorPos(out POINT pt);

            int cmd = TrackPopupMenuEx(
                _hMenu,
                TPM_RETURNCMD | TPM_LEFTALIGN | TPM_LEFTBUTTON,
                pt.X, pt.Y, hwnd, IntPtr.Zero);

            if (cmd > 0)
            {
                // Pronađi akciju po ID-u (pretraži rekurzivno)
                ExecuteById(cmd);
            }
        }

        /// <summary>Prikaži na specifičnoj poziciji (za keyboard trigger — ispod fokusiranog elementa).</summary>
        public void ShowAtPosition(Window owner, int screenX, int screenY)
        {
            if (_hMenu == IntPtr.Zero) return;

            var hwnd = new WindowInteropHelper(owner).Handle;
            SetForegroundWindow(hwnd);

            int cmd = TrackPopupMenuEx(
                _hMenu,
                TPM_RETURNCMD | TPM_LEFTALIGN | TPM_LEFTBUTTON,
                screenX, screenY, hwnd, IntPtr.Zero);

            if (cmd > 0)
                ExecuteById(cmd);
        }

        private bool ExecuteById(int id)
        {
            foreach (var item in _items)
            {
                if (item.Id == id)
                {
                    item.Action?.Invoke();
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (_hMenu != IntPtr.Zero)
            {
                DestroyMenu(_hMenu);
                _hMenu = IntPtr.Zero;
            }
        }
    }

    public class NativeMenuItem
    {
        public int    Id     { get; }
        public string Text   { get; }
        public Action? Action { get; }

        public NativeMenuItem(int id, string text, Action? action)
        {
            Id = id; Text = text; Action = action;
        }
    }
}
