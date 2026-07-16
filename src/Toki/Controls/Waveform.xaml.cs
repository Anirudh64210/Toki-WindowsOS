using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Toki.Controls;

/// <summary>
/// Decorative animated waveform (fixed bar pattern, looping scale animation).
/// Animates only while visible so a cached-but-hidden view costs nothing.
/// M4 replaces this with bars driven by real audio levels.
/// </summary>
public partial class Waveform : UserControl
{
    static readonly double[] Heights = [6, 12, 8, 16, 10, 18, 7, 14, 9, 16, 6, 11];
    readonly List<ScaleTransform> _scales = new();

    public Waveform()
    {
        InitializeComponent();
        for (var i = 0; i < Heights.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 2.5,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Height = Heights[i],
                Fill = Brushes.White,
                Margin = new Thickness(0, 0, 2.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var scale = new ScaleTransform(1, 1);
            bar.RenderTransform = scale;
            _scales.Add(scale);
            Bars.Children.Add(bar);
        }
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Run(); else Stop(); };
        Unloaded += (_, _) => Stop();
    }

    void Run()
    {
        for (var i = 0; i < _scales.Count; i++)
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(300 + i * 37))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    void Stop()
    {
        foreach (var s in _scales)
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }
}
