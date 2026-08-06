using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Toki.Controls
{
    /// <summary>
    /// Butter-smooth mouse-wheel scrolling for any ScrollViewer. WPF's default wheel jumps a
    /// coarse three text-lines with no easing, which reads as clunky and stepwise. This animates
    /// the vertical offset with a short ease-out and accumulates fast spins into momentum, so the
    /// Settings / Notes / Library scrollers glide instead of jerk. Attach with
    /// <c>controls:SmoothScroll.Enabled="True"</c> on the ScrollViewer.
    ///
    /// Our scrollers wrap plain StackPanels (CanContentScroll is false by default), so the viewer
    /// already scrolls by pixels - all that was missing was the animation. Kept deliberately tiny
    /// and allocation-light; it never fights the ScrollBar (dragging the thumb still works because
    /// we only intercept the wheel).
    /// </summary>
    public static class SmoothScroll
    {
        // How far one wheel notch (delta 120) glides, in DIP. Tuned to feel like a trackpad flick.
        const double StepPx = 96;
        static readonly Duration Glide = new Duration(TimeSpan.FromMilliseconds(280));

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
                new PropertyMetadata(false, OnEnabledChanged));
        public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

        // Animatable proxy: writing this scrolls the viewer. Lives on the ScrollViewer itself.
        static readonly DependencyProperty OffsetProperty =
            DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
                new PropertyMetadata(0.0, OnOffsetChanged));

        // Where the current glide is heading (NaN = no glide in flight; start fresh from the real offset).
        static readonly DependencyProperty TargetProperty =
            DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll),
                new PropertyMetadata(double.NaN));

        static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;
            if ((bool)e.NewValue)
            {
                sv.PreviewMouseWheel += OnWheel;
                sv.ScrollChanged += OnScrollChanged;
            }
            else
            {
                sv.PreviewMouseWheel -= OnWheel;
                sv.ScrollChanged -= OnScrollChanged;
            }
        }

        static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // When content resizes (notes stream in, a view is swapped in) drop the stale target so
            // the next wheel starts from where we actually are, not a now-invalid destination.
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                ((ScrollViewer)sender).SetValue(TargetProperty, double.NaN);
        }

        static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (sv.ScrollableHeight <= 0) return; // nothing to scroll here - let it bubble to a parent
            e.Handled = true;
            Nudge(sv, e.Delta);
        }

        /// <summary>Animate the viewer by one wheel delta (120 = one notch). Shared by the ScrollViewer's own
        /// wheel handler AND the global mouse-hook path (which scrolls the notch even when it isn't the focused
        /// window - the reason meeting-overlay scrolling otherwise fell through to the window behind).</summary>
        public static void Nudge(ScrollViewer sv, int delta)
        {
            if (sv.ScrollableHeight <= 0) return;
            var target = (double)sv.GetValue(TargetProperty);
            if (double.IsNaN(target)) target = sv.VerticalOffset;
            target -= delta / 120.0 * StepPx;
            target = Math.Max(0, Math.Min(sv.ScrollableHeight, target));
            sv.SetValue(TargetProperty, target);

            var anim = new DoubleAnimation(sv.VerticalOffset, target, Glide)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            sv.BeginAnimation(OffsetProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}
