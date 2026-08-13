using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Toki.Views;

/// <summary>
/// The Library's meeting list, built to touch only the rows you can actually see.
///
/// It used to be a plain <see cref="StackPanel"/>, which realises EVERY child: it creates the visual,
/// measures it, arranges it and keeps it alive whether or not it is anywhere near the viewport. About
/// six rows fit on screen. At 54 meetings a rebuild cost 424ms, roughly 343ms of which was WPF
/// arranging cards nobody could see - and the cost grows linearly forever, so a full library becomes a
/// hang (~3.9s at 500 meetings). Measured, not guessed: see <c>docs/ui-performance.md</c> and the
/// <c>--uitest</c> self-test.
///
/// Two things here are load-bearing:
///
///   • The rows are built in C# (<c>LibraryWindow.Card</c>) - drag handles, hover states, chips, a date
///     badge - and a <see cref="DataTemplate"/> cannot call that. So instead of templating the item, we
///     hand the builder in as <see cref="Build"/> and call it from
///     <see cref="PrepareContainerForItemOverride"/>, which fires ONLY for rows WPF actually realises.
///     The existing card-building code is reused untouched.
///
///   • <see cref="ClearContainerForItemOverride"/> drops the old visual. With
///     <c>VirtualizationMode="Recycling"</c> the same container is handed back for a different meeting,
///     and a stale card left in <c>Content</c> is exactly the bug that makes a recycled list show the
///     wrong row.
///
/// The ScrollViewer lives in this control's TEMPLATE (see LibraryWindow.xaml), not around it. An
/// ItemsControl wrapped in an outer ScrollViewer is measured with infinite height, every row is
/// realised, and virtualization silently does nothing at all.
/// </summary>
public sealed class MeetingList : ItemsControl
{
    /// <summary>Builds the visual for one item. Called only for rows that get realised.</summary>
    internal Func<object, FrameworkElement>? Build;

    // A ContentPresenter is the lightest thing that can host a ready-made visual. IsItemItsOwnContainer
    // must say false or WPF would try to use the MeetingSummary itself as the container.
    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();
    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ContentPresenter cp) cp.Content = Build?.Invoke(item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        if (element is ContentPresenter cp) cp.Content = null;
    }

    /// <summary>
    /// The rows currently realised, for repainting selection without rebuilding anything.
    /// Deliberately NOT every row: off-screen meetings have no visual to paint, which is why selection
    /// has to live in <c>_selected</c> and be re-applied when a row is built, not stored in the visual.
    /// </summary>
    internal IEnumerable<FrameworkElement> Realised()
    {
        for (var i = 0; i < Items.Count; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter { Content: FrameworkElement fe })
                yield return fe;
    }
}
