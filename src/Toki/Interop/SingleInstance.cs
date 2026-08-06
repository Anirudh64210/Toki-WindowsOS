using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Toki.Interop;

/// <summary>
/// Single-instance guard + "show me" signal, hardened against local named-object squatting.
///
/// Two problems with a plain <c>new Mutex(true, "Toki.SingleInstance")</c>: (1) the name is global and
/// guessable, so any unprivileged process in the session can pre-create it - with a hostile ACL it can
/// DoS Toki into never starting, or (for the show-signal) drive the UI; (2) treating "the object already
/// exists" as an unconditional "another Toki is running" is exactly what the squatter exploits.
///
/// Fixes: names are salted with the current user's SID (a different user can't even see them); the
/// objects are created with a DACL granting ONLY the current user; and "already exists" is verified
/// against a real running Toki process before we defer to it - a squat with no real Toki present is
/// logged and we take over instead of exiting.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex? _mutex;
    readonly EventWaitHandle? _showSignal;
    Thread? _waiter;
    volatile bool _disposed;

    /// <summary>True when THIS process is the primary instance (owns the app).</summary>
    public bool IsPrimary { get; }

    SingleInstance(Mutex? mutex, EventWaitHandle? signal, bool isPrimary)
    {
        _mutex = mutex; _showSignal = signal; IsPrimary = isPrimary;
    }

    static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? "anon";
    static string MutexName => $@"Local\Toki.SingleInstance.{Sid}";
    static string SignalName => $@"Local\Toki.ShowSignal.{Sid}";

    /// <summary>Try to become the single instance. Returns a guard whose <see cref="IsPrimary"/> tells
    /// the caller whether to run (true) or to signal-and-exit (false).</summary>
    public static SingleInstance Acquire()
    {
        var identity = WindowsIdentity.GetCurrent().User!;
        // DACL: only this user, full control. Nothing for Everyone/Users - closes cross-user squatting.
        var mutexSec = new MutexSecurity();
        mutexSec.AddAccessRule(new MutexAccessRule(identity, MutexRights.FullControl, AccessControlType.Allow));
        var eventSec = new EventWaitHandleSecurity();
        eventSec.AddAccessRule(new EventWaitHandleAccessRule(identity, EventWaitHandleRights.FullControl, AccessControlType.Allow));

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: true, MutexName, out createdNew, mutexSec);
        }
        catch (Exception ex)
        {
            // If we can't create/open the guarded object (e.g. a squatter holds it with a deny ACL),
            // don't be DoS'd into never starting - run without the guard.
            App.Log($"single-instance: mutex unavailable ({ex.Message}) - starting unguarded");
            return new SingleInstance(null, null, isPrimary: true);
        }

        EventWaitHandle signal;
        try
        {
            signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, SignalName, out _, eventSec);
        }
        catch (Exception ex)
        {
            App.Log($"single-instance: show-signal unavailable ({ex.Message})");
            return new SingleInstance(mutex, null, isPrimary: createdNew || !AnotherTokiRunning());
        }

        if (createdNew) return new SingleInstance(mutex, signal, isPrimary: true);

        // The object already existed. Only defer to it if a REAL other Toki process is actually
        // running - otherwise it's a squat (or a stale handle) and we take over as primary.
        if (AnotherTokiRunning())
        {
            try { signal.Set(); } catch { }   // ask the running instance to surface
            return new SingleInstance(mutex, signal, isPrimary: false);
        }
        App.Log("single-instance: guard object existed but no other Toki process is running - taking over");
        return new SingleInstance(mutex, signal, isPrimary: true);
    }

    static bool AnotherTokiRunning()
    {
        try
        {
            var me = Environment.ProcessId;
            using var self = Process.GetCurrentProcess();
            var myName = self.ProcessName;
            var procs = Process.GetProcessesByName(myName);
            try { return procs.Any(p => p.Id != me); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return false; }
    }

    /// <summary>Run <paramref name="onShow"/> whenever a second launch signals us to surface (primary only).</summary>
    public void ListenForShow(Action onShow)
    {
        if (_showSignal is null || !IsPrimary) return;
        _waiter = new Thread(() =>
        {
            try { while (!_disposed && _showSignal.WaitOne()) { if (!_disposed) onShow(); } }
            catch { /* handle disposed on shutdown */ }
        })
        { IsBackground = true, Name = "Toki.ShowSignalWaiter" };
        _waiter.Start();
    }

    /// <summary>Release the mutex so a fresh process becomes the owner (used by Restart).</summary>
    public void Release()
    {
        _disposed = true;
        try { _mutex?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _showSignal?.Set(); } catch { }   // unblock the waiter so the thread can exit
        try { _showSignal?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }

    // Exposed for tests: the names must be per-user and in the session-local namespace.
    public static (string Mutex, string Signal) NamesForTest() => (MutexName, SignalName);
}
