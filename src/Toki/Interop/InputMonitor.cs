using System.Runtime.InteropServices;
using System.Windows;

namespace Toki.Interop;

/// <summary>
/// Global low-level mouse + keyboard hooks so a panel can be dismissed by a real
/// click anywhere outside it, or by Esc — without Toki ever stealing focus
/// (keeps the ghost/overlay feel). Deliberately does NOT rely on window
/// activation, which fires on unrelated focus changes and made Toki flicker shut.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    const int WhMouseLl = 14;
    const int WhKeyboardLl = 13;
    const int WmLButtonDown = 0x0201;
    const int WmRButtonDown = 0x0204;
    const int WmKeyDown = 0x0100;
    const int VkEscape = 0x1B;

    readonly LowLevelProc _mouseProc;   // kept alive against GC
    readonly LowLevelProc _keyProc;
    readonly Func<bool> _isDismissable;
    readonly Func<Point, bool> _isInsidePanel; // screen (device) point
    readonly Action _dismiss;
    IntPtr _mouseHook, _keyHook;

    public InputMonitor(Func<bool> isDismissable, Func<Point, bool> isInsidePanel, Action dismiss)
    {
        _isDismissable = isDismissable;
        _isInsidePanel = isInsidePanel;
        _dismiss = dismiss;
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        using var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        var hModule = GetModuleHandle(mod.ModuleName);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hModule, 0);
        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, hModule, 0);
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (wParam == WmLButtonDown || wParam == WmRButtonDown) && _isDismissable())
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(data.pt.x, data.pt.y);
                if (!_isInsidePanel(pt)) _dismiss();
            }
        }
        catch (Exception ex) { App.Log($"mouse hook (non-fatal): {ex.Message}"); }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && wParam == WmKeyDown && _isDismissable())
            {
                var vk = Marshal.ReadInt32(lParam);
                if (vk == VkEscape) _dismiss();
            }
        }
        catch (Exception ex) { App.Log($"key hook (non-fatal): {ex.Message}"); }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }

    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetModuleHandle(string lpModuleName);
}
