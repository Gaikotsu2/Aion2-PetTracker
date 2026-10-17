using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PetTracker.App.Infrastructure;

/// <summary>
/// Win32 calls on OUR OWN windows only: extended styles of the overlay (click-through, no activation) and global hotkeys
/// registered against the overlay's handle. Nothing here ever receives a handle that belongs to another process.
/// </summary>
internal static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    private static void SetExStyle(IntPtr hwnd, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value));
        else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)value);
    }

    /// <summary>Tool window that never takes focus; click-through adds WS_EX_TRANSPARENT (with WS_EX_LAYERED).</summary>
    public static void ApplyOverlayStyles(Window ownWindow, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(ownWindow).EnsureHandle();
        SetExStyle(hwnd, WithOverlayBits(GetExStyle(hwnd), clickThrough));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// WPF rewrites the extended style from its own cache whenever it updates the window (ResizeMode, Topmost, ...),
    /// which silently drops WS_EX_TRANSPARENT. This hook re-applies our bits to every extended-style change of our window.
    /// </summary>
    public static void GuardOverlayStyles(Window ownWindow, Func<bool> clickThrough)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(ownWindow).EnsureHandle());
        source?.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_STYLECHANGING && wParam.ToInt64() == GWL_EXSTYLE)
            {
                var ss = Marshal.PtrToStructure<StyleStruct>(lParam);
                ss.StyleNew = (uint)WithOverlayBits(ss.StyleNew, clickThrough());
                Marshal.StructureToPtr(ss, lParam, false);
            }
            else if (msg == WM_NCHITTEST && clickThrough())
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
            return IntPtr.Zero;
        });
    }

    private static long WithOverlayBits(long style, bool clickThrough)
    {
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        return clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
    }

    private const int WM_STYLECHANGING = 0x007C, WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public uint StyleOld;
        public uint StyleNew;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static bool Register(IntPtr ownHwnd, int id, uint modifiers, uint vk) => RegisterHotKey(ownHwnd, id, modifiers | 0x4000 /* MOD_NOREPEAT */, vk);

    public static void Unregister(IntPtr ownHwnd, int id) => UnregisterHotKey(ownHwnd, id);
}

/// <summary>"Ctrl+Shift+P" text and its modifier / virtual-key form.</summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        uint mods = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (key is not null || !Enum.TryParse<Key>(part, ignoreCase: true, out var k) || k == Key.None)
                        return false;
                    key = k;
                    break;
            }
        }
        if (key is null || mods == 0)
            return false;
        var vk = KeyInterop.VirtualKeyFromKey(key.Value);
        if (vk == 0)
            return false;
        gesture = new HotkeyGesture(mods, (uint)vk);
        return true;
    }
}

/// <summary>Global hotkeys (RegisterHotKey) delivered to our own window's message loop.</summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];

    public HotkeyService(Window ownWindow)
    {
        _hwnd = new WindowInteropHelper(ownWindow).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(Hook);
    }

    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public string? Set(int id, string? gestureText, Action action)
    {
        Native.Unregister(_hwnd, id);
        _actions.Remove(id);
        if (!HotkeyGesture.TryParse(gestureText, out var g))
            return $"\"{gestureText}\" is not a valid hotkey (example: Ctrl+Shift+P).";
        if (!Native.Register(_hwnd, id, g.Modifiers, g.VirtualKey))
            return $"\"{gestureText}\" could not be registered (already used by another program?).";
        _actions[id] = action;
        return null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList())
            Native.Unregister(_hwnd, id);
        _source.RemoveHook(Hook);
    }
}
