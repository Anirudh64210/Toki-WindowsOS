using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Toki.Views;

/// <summary>
/// Three pulsing dots with a caption - the app's single "still working" indicator. Shown in the gap
/// while speaker names resolve so the second wave doesn't look like the note silently changing.
/// </summary>
public sealed class ThinkingDots : StackPanel
{
    readonly List<Border> _dots = new();

    public ThinkingDots(string caption)
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 2, 0, 10);

        for (var i = 0; i < 3; i++)
        {
            var dot = new Border
            {
                Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5),
                Margin = new Thickness(0, 0, 5, 0), Opacity = 0.3,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(Border.BackgroundProperty, "Pink");
            _dots.Add(dot);
            Children.Add(dot);
        }

        var text = new TextBlock
        {
            Text = caption, FontSize = 12, Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        Children.Add(text);

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    void Start()
    {
        if (App.Headless) return;   // a render harness must not leave animations running forever
        for (var i = 0; i < _dots.Count; i++)
            _dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(560))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 170),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    void Stop()
    {
        foreach (var d in _dots) { d.BeginAnimation(OpacityProperty, null); d.Opacity = 0.3; }
    }
}
