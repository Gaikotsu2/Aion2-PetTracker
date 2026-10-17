using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PetTracker.Research.Support;

public enum Hotkey { F9 = 1, F10 = 2 }

/// <summary>
/// Global F9/F10 hotkeys through RegisterHotKey, delivered to a hidden message-only window owned by this process.
/// Nothing here touches any other window or process.
/// </summary>
public sealed partial class HotkeyListener : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private Exception? _startupError;

    public HotkeyListener()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
            throw _startupError;
    }

    /// <summary>Raised on the hotkey thread with the UTC ticks of the key press.</summary>
    public event Action<Hotkey, long>? Pressed;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var window = CreateWindowExW(0, "STATIC", "pt-research-hotkeys", 0, 0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            Fail("Could not create the hidden hotkey window");
            return;
        }

        var f9 = RegisterHotKey(window, (int)Hotkey.F9, ModNoRepeat, VkF9);
        var f10 = f9 && RegisterHotKey(window, (int)Hotkey.F10, ModNoRepeat, VkF10);
        if (!f10)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            if (f9) UnregisterHotKey(window, (int)Hotkey.F9);
            DestroyWindow(window);
            _startupError = new InvalidOperationException($"Could not register the F9/F10 hotkeys (another program may be using them): {error}");
            _ready.Set();
            return;
        }

        _ready.Set();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmHotkey)
                Pressed?.Invoke((Hotkey)(int)msg.WParam, DateTime.UtcNow.Ticks);
        }

        UnregisterHotKey(window, (int)Hotkey.F9);
        UnregisterHotKey(window, (int)Hotkey.F10);
        DestroyWindow(window);
    }

    private void Fail(string what)
    {
        _startupError = new InvalidOperationException($"{what}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        _ready.Set();
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out Msg message, IntPtr window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
