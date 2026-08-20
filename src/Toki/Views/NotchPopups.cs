using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Toki.Views;

/// <summary>
/// Registry of dropdown popups that belong to the notch. A WPF Popup is its own top-level window,
/// so PillWindow consults this to treat an open dropdown as part of the notch (no tuck/dismiss).
/// THREADING: consumers are low-level mouse-hook callbacks on the hook thread, where touching any
/// DependencyObject throws; screen rects are computed on the UI thread and the hook thread only
/// reads a plain snapshot array.
/// </summary>
public static class NotchPopups
{
    static readonly List<Popup> _open = new();
    static volatile Rect[] _rects = Array.Empty<Rect>();   // physical px; hook-thread reads
    static volatile int _openCount;

    public static void Register(Popup p)
    {
        if (_open.Contains(p)) return;
        _open.Add(p);
        p.Opened += (_, _) =>
        {
            Refresh();
            // Opened fires before arrange - re-measure once layout has actually happened.
            p.Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Loaded);
        };
        p.Closed += (_, _) => Refresh();
    }

    public static void Unregister(Popup p)
    {
        _open.Remove(p);
        Refresh();
    }

    /// <summary>True while any notch dropdown is open - the notch must not tuck/dismiss meanwhile.
    /// Plain volatile read, safe from the hook thread.</summary>
    public static bool AnyOpen => _openCount > 0;

    /// <summary>Recompute the open dropdowns' physical-pixel screen rects. UI thread only.</summary>
    static void Refresh()
    {
        var rects = new List<Rect>();
        var count = 0;
        foreach (var p in _open)
        {
            if (!p.IsOpen) continue;
            count++;
            if (p.Child is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0) continue;
            try
            {
                var tl = fe.PointToScreen(new Point(0, 0));
                var br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
                rects.Add(new Rect(tl, br));
            }
            catch { /* not yet arranged - the Loaded-priority re-refresh will catch it */ }
        }
        _openCount = count;
        _rects = rects.ToArray();
    }

    /// <summary>Is a PHYSICAL-pixel screen point inside an open dropdown's on-screen rectangle?
    /// Reads only the pre-computed snapshot - callable from the low-level click hook.</summary>
    public static bool ContainsScreenPoint(int px, int py)
    {
        foreach (var r in _rects)
        {
            // A few px of slack so the very edge of the popup still counts as inside.
            if (px >= r.Left - 2 && px <= r.Right + 2 && py >= r.Top - 2 && py <= r.Bottom + 2) return true;
        }
        return false;
    }
}
