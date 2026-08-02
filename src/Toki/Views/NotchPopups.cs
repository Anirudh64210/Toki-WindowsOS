using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Toki.Views;

/// <summary>
/// A tiny registry of dropdown popups that logically belong to the notch. A WPF Popup is its OWN
/// top-level window, so when a model/provider list drops BELOW the notch, the notch's click-outside
/// dismiss + idle-tuck would treat the cursor as "off the notch" and retract it — right as you try to
/// pick an option. PillWindow consults this so an open dropdown counts as PART of the notch: scroll
/// and click freely, the notch stays put. (Harmless when a dropdown lives in another window — the
/// pill only reacts while it is the one on screen.)
/// </summary>
public static class NotchPopups
{
    static readonly List<Popup> _open = new();

    public static void Register(Popup p) { if (!_open.Contains(p)) _open.Add(p); }
    public static void Unregister(Popup p) => _open.Remove(p);

    /// <summary>True while any notch dropdown is open — the notch must not tuck/dismiss meanwhile.</summary>
    public static bool AnyOpen => _open.Count > 0;

    /// <summary>Is a PHYSICAL-pixel screen point inside an open dropdown's on-screen rectangle?
    /// Used by the low-level click hook (which reports device pixels) so a click in the list never
    /// reads as "outside the notch".</summary>
    public static bool ContainsScreenPoint(int px, int py)
    {
        foreach (var p in _open)
        {
            if (!p.IsOpen || p.Child is not FrameworkElement fe || !fe.IsVisible || fe.ActualWidth <= 0) continue;
            try
            {
                var tl = fe.PointToScreen(new Point(0, 0));
                var br = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
                // A few px of slack so the very edge of the popup still counts as inside.
                if (px >= tl.X - 2 && px <= br.X + 2 && py >= tl.Y - 2 && py <= br.Y + 2) return true;
            }
            catch { /* popup not yet arranged — ignore this frame */ }
        }
        return false;
    }
}
