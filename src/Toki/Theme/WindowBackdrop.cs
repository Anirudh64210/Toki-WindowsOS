using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Toki.Theme;

/// <summary>
/// REAL glass, from the OS, on the windows that can take it.
///
/// Toki's notch fakes its material with layered brushes, and a note in this codebase said real
/// blur-behind was impossible because the notch runs in ghost mode (WDA_EXCLUDEFROMCAPTURE).
/// RESEARCHED AND SUPERSEDED 2026-09-03: capture exclusion is not the blocker and there is no
/// evidence it ever was. Microsoft's own reference for SetWindowDisplayAffinity lists exactly one
/// caveat - that DWM must be composing - and stealth-overlay apps ship content protection and
/// acrylic together routinely. One project even needs the exclusion FOR its blur, because a window
/// that captures the screen behind itself without excluding itself samples its own output.
///
/// The notch's actual blockers are two different things, and both are real:
///   1. AllowsTransparency=True makes it a per-pixel LAYERED window, which bypasses the frame
///      surface DWM paints a backdrop into.
///   2. It is never activated (ShowActivated=False). Microsoft documents that acrylic and mica
///      "replace translucency and texture with a solid color" when a window deactivates, so a
///      backdrop there would render its flat fallback essentially all of the time - the same flat
///      plate it already has, for the price of a rewrite.
///
/// The LIBRARY window has neither blocker: it is an ordinary activatable window with no
/// transparency. So it gets the real thing, and it is the honest A/B for judging how much the
/// notch's fake version still needs.
///
/// Pattern adapted from WPF-UI's WindowBackdrop (MIT, lepoco/wpfui). No dependency taken: it is
/// ~40 lines of P/Invoke and pulling the package would drag in a Fluent style system that fights
/// Toki's own design language.
/// </summary>
public static class WindowBackdrop
{
    const int DwmwaSystemBackdropType = 38;
    /// <summary>DWMSBT_TRANSIENTWINDOW - Desktop Acrylic. The transient (popover/palette) material
    /// rather than Mica, because the Library is summoned and dismissed, not a document window that
    /// lives on the desktop all day.</summary>
    const int DwmsbtTransientWindow = 3;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Win11 22H2. The attribute exists from 22000 but the backdrop-type enum only does
    /// what you want from 22621, and asking for it below that is a silent no-op rather than an
    /// error - which would leave a transparent window with nothing behind it.</summary>
    static bool Supported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// Ask DWM to paint acrylic behind this window. Returns false when the OS cannot, so the caller
    /// keeps its opaque background rather than ending up see-through with no material.
    ///
    /// Two things have to be true for DWM to actually paint it, and both are easy to miss: the WPF
    /// window's Background must be transparent, AND the interop composition target's
    /// BackgroundColor must be transparent too. Setting only the first leaves WPF clearing the
    /// surface to opaque black before DWM ever gets there.
    /// </summary>
    public static bool TryApply(Window window)
    {
        if (!Supported) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;   // call after SourceInitialized

        var kind = DwmsbtTransientWindow;
        if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref kind, sizeof(int)) != 0) return false;

        window.Background = Brushes.Transparent;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
        return true;
    }
}
