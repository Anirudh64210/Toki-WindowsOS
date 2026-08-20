using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Toki.Views;

/// <summary>
/// The Library's virtualizing meeting list. Rows are built in C# via <see cref="Build"/> (a
/// DataTemplate cannot call the card builder); <see cref="ClearContainerForItemOverride"/> must drop
/// the old visual or Recycling shows a stale card on the wrong row. The ScrollViewer lives in this
/// control's TEMPLATE (LibraryWindow.xaml): an outer ScrollViewer measures with infinite height and
/// silently defeats virtualization.
/// </summary>
public sealed class MeetingList : ItemsControl
{
    /// <summary>Builds the visual for one item. Called only for rows that get realised.</summary>
    internal Func<object, FrameworkElement>? Build;

    // IsItemItsOwnContainer must say false or WPF tries to use the MeetingSummary as the container.
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
    /// The rows currently realised, for repainting selection without rebuilding. Off-screen rows have
    /// no visual, so selection must live in <c>_selected</c> and be re-applied when a row is built.
    /// </summary>
    internal IEnumerable<FrameworkElement> Realised()
    {
        for (var i = 0; i < Items.Count; i++)
            if (ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter { Content: FrameworkElement fe })
                yield return fe;
    }
}
