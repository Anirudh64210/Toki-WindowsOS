using System.IO;
using System.Runtime.InteropServices;

namespace Toki.Dev;

/// <summary>
/// Toki is a WinExe (no console), but the self-test flags must print to a
/// terminal. Attach to the launching console if there is one, else allocate a
/// fresh one, and re-point stdout so Console.WriteLine actually appears.
/// </summary>
static class ConsoleHelper
{
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();

    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
    }
}
