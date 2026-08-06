using System.Runtime.InteropServices;
using System.Windows;

namespace Toki.Interop;

/// <summary>
/// A global low-level MOUSE hook (click-outside-to-dismiss) running on its OWN
/// dedicated thread with its own message loop. Low-level hooks are serviced on
/// the thread that installed them; if that were the UI thread, any render/GC
/// stall there would lag ALL mouse input system-wide. A dedicated thread keeps
/// the pointer buttery no matter what the UI is doing.
///
/// The callback does only pure arithmetic against a cached rect + thread-safe
/// reads, then marshals the actual dismissal to the UI thread.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    const int WhMouseLl = 14;
    const int WmLButtonDown = 0x0201;
    const int WmRButtonDown = 0x0204;
    const int WmMouseWheel = 0x020A;
    const int WmQuit = 0x0012;

    readonly LowLevelProc _mouseProc;   // kept alive against GC
    readonly Func<bool> _isArmed;
    readonly Func<int, int, bool> _isInsidePanel;
    readonly Action _dismiss;
    readonly Func<int, int, int, bool>? _onWheel;   // (x, y, delta) → true to CONSUME (block the window behind)
    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;

    public InputMonitor(Func<bool> isArmed, Func<int, int, bool> isInsidePanel, Action dismiss,
                        Func<int, int, int, bool>? onWheel = null)
    {
        _isArmed = isArmed;
        _isInsidePanel = isInsidePanel;
        _dismiss = dismiss;
        _onWheel = onWheel;
        _mouseProc = MouseHook;
        _thread = new Thread(Run) { IsBackground = true, Name = "Toki.MouseHook" };
        _thread.Start();
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        using var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(mod.ModuleName), 0);

        // Pump messages so the low-level hook is serviced on this thread.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var w = (int)wParam;
            if (code >= 0)
            {
                if ((w == WmLButtonDown || w == WmRButtonDown) && _isArmed())
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (!_isInsidePanel(data.pt.x, data.pt.y)) _dismiss();
                }
                else if (w == WmMouseWheel && _onWheel is not null)
                {
                    // The wheel goes to the FOCUSED window by default (or the hovered one only if the OS
                    // "scroll inactive windows" setting is on) - so scrolling the notch fell through to the
                    // app behind it. We catch every wheel here: if it's over the notch's scrollable content,
                    // scroll the notch AND return non-zero to CONSUME it, so the window behind never moves.
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var delta = (short)(data.mouseData >> 16);
                    if (_onWheel(data.pt.x, data.pt.y, delta)) return (IntPtr)1;
                }
            }
        }
        catch { /* a hook must never throw into the message pump */ }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
