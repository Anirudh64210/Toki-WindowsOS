using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Toki.Controls
{
    /// <summary>
    /// Smooth mouse-wheel scrolling for any ScrollViewer: animates the vertical offset with a short
    /// ease-out and accumulates fast spins. Attach with <c>controls:SmoothScroll.Enabled="True"</c>.
    /// Only the wheel is intercepted, so thumb dragging is untouched.
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

        /// <summary>Animate the viewer by one wheel delta (120 = one notch). Shared by the viewer's
        /// own wheel handler AND the global mouse-hook path that scrolls the never-focused notch.</summary>
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

        /// <summary>
        /// Glide the viewer so <paramref name="target"/> sits near the top. Never BringIntoView for
        /// this: arriving somewhere new should show the travel, not teleport the page.
        /// </summary>
        public static void GlideTo(ScrollViewer sv, FrameworkElement target, double topPad = 24, int ms = 520)
        {
            if (sv.ScrollableHeight <= 0) return;
            double y;
            try { y = target.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
            catch { return; }
            var to = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset + y - topPad));
            sv.SetValue(TargetProperty, to);
            // Longer and softer than a wheel notch: this is a journey across the page, not a nudge.
            var anim = new DoubleAnimation(sv.VerticalOffset, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            sv.BeginAnimation(OffsetProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}
