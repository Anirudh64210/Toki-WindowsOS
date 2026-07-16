using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Toki.Interop;

/// <summary>
/// Registers the global hotkey (Ctrl+Alt+Space; plain Alt+Space is reserved by
/// Windows) via RegisterHotKey. User-configurable binding comes with Settings (M8).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x0001;
    const uint ModControl = 0x0002;
    const uint VkSpace = 0x20;

    readonly HwndSource _source;
    readonly Action _pressed;
    readonly int _id = 0xA110;
    readonly IntPtr _hwnd;

    public bool Registered { get; }

    public HotkeyManager(Window window, Action pressed)
    {
        _pressed = pressed;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_hwnd, _id, ModControl | ModAlt, VkSpace);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt64() == _id)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(_hwnd, _id);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
