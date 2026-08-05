using System.Runtime.InteropServices;

namespace Toki.Interop;

/// <summary>
/// Push-to-talk detector for Ctrl+Alt+Space: fires <c>onStart</c> when the chord goes DOWN and
/// <c>onStop</c> when it's released. RegisterHotKey only reports the press, never the release, so
/// hold-to-talk needs a low-level keyboard hook (WH_KEYBOARD_LL) — run on its OWN thread with a
/// message loop so a UI stall can never lag global keystrokes. The callback does pure key logic,
/// then marshals to the UI thread. It does NOT swallow the keys (Ctrl+Alt+Space isn't a common
/// combo), and it de-dupes auto-repeat via the <c>_active</c> latch.
/// </summary>
public sealed class PushToTalkHook : IDisposable
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    const int WmQuit = 0x0012;
    const int VkSpace = 0x20, VkControl = 0x11, VkMenu = 0x12; // VK_MENU = Alt
    const int VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;
    const ushort KeyDownMask = 0x8000;

    readonly LowLevelProc _proc;   // kept alive against GC
    readonly Action _onStart, _onStop;
    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;
    bool _active;

    public PushToTalkHook(Action onStart, Action onStop)
    {
        _onStart = onStart;
        _onStop = onStop;
        _proc = KeyHook;
        _thread = new Thread(Run) { IsBackground = true, Name = "Toki.PttHook" };
        _thread.Start();
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        using var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(mod.ModuleName), 0);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                int vk = (int)info.vkCode;
                int w = (int)wParam;
                bool down = w is WmKeyDown or WmSysKeyDown;
                bool up = w is WmKeyUp or WmSysKeyUp;

                if (down && vk == VkSpace && !_active && Held(VkControl) && Held(VkMenu))
                {
                    _active = true;
                    _onStart();
                }
                else if (up && _active && vk == VkSpace)
                {
                    // End the utterance only when the TALK key (Space) is released. Once the chord has
                    // engaged, SPACE is what you hold to talk — you can relax Ctrl/Alt without cutting the
                    // mic. (The old "any chord key ends it" cut recording the instant a finger drifted off
                    // Ctrl or Alt mid-sentence — the "waveform disappears while I'm still listening" bug.)
                    _active = false;
                    _onStop();
                }
            }
        }
        catch { /* a hook must never throw into the message pump */ }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & KeyDownMask) != 0;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct KbdLlHookStruct { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
