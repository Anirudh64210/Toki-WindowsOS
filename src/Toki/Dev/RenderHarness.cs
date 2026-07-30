using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toki.State;
using Toki.Views;

namespace Toki.Dev;

/// <summary>
/// Renders each view to a PNG via RenderTargetBitmap — captures the visual tree
/// directly, so it works even though the live overlay is a transparent (layered)
/// window that plain screen-capture can't see. Run: <c>Toki.exe --render &lt;dir&gt;</c>.
/// </summary>
static class RenderHarness
{
    // (state, width, capHeight). Height is MEASURED from the view's content (matching the runtime's
    // content-tight auto-height) and capped — so these PNGs reflect what you actually see.
    static readonly (PillState State, double W, double Cap)[] Shots =
    [
        (PillState.MeetingPrompt, 288, 300),
        (PillState.RecordingOpen, 288, 300),
        (PillState.Summarizing,   232, 300),
        (PillState.Notes,         360, 320),
        (PillState.NoSpeech,      300, 300),
        (PillState.Home,          320, 400),
        (PillState.Library,       404, 440),
        (PillState.Settings,      380, 440),
    ];

    public static void RenderAll(string dir)
    {
        Directory.CreateDirectory(dir);
        // Give the Notes screen a real meeting to render (latest in the store).
        try
        {
            var latest = new Storage.MeetingStore().List().FirstOrDefault(x => x.Kind == Storage.MeetingKind.Meeting);
            if (latest is not null) App.CurrentMeeting = new Storage.MeetingStore().Get(latest.Id);
        }
        catch { /* no store yet — Notes renders its fallback */ }
        foreach (var (state, w, cap) in Shots)
        {
            var view = Build(state);
            // Measure the view's real content height at this width, cap it — mirrors the runtime.
            view.Measure(new Size(w - 2, double.PositiveInfinity));
            var h = Math.Min(cap, Math.Ceiling(view.DesiredSize.Height) + 1);
            var pill = new Border
            {
                Background = (Brush)Application.Current.Resources["Surface"],
                CornerRadius = new CornerRadius(26),
                Width = w,
                Height = h,
                Child = view,
            };
            // Neutral backdrop so the pill's rounded edges are visible in the PNG.
            var canvas = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                Padding = new Thickness(20),
                Child = pill,
            };

            var totalW = w + 40;
            var totalH = h + 40;
            canvas.Measure(new Size(totalW, totalH));
            canvas.Arrange(new Rect(0, 0, totalW, totalH));
            canvas.UpdateLayout();

            const double scale = 2.0;
            var rtb = new RenderTargetBitmap(
                (int)(totalW * scale), (int)(totalH * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(canvas);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var path = Path.Combine(dir, $"{state}.png");
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    /// <summary>
    /// Renders the Summarizing view twice, ~550ms apart, so the two PNGs can be
    /// compared to confirm the loading animation actually moves (constructor-based
    /// animations advance with the real clock even offscreen).
    /// </summary>
    public static void RenderAnim(string dir)
    {
        Directory.CreateDirectory(dir);
        const double w = 340, h = 164;
        var view = new SummarizingView();
        var pill = new Border
        {
            Background = (Brush)Application.Current.Resources["Surface"],
            CornerRadius = new CornerRadius(26),
            Width = w, Height = h, Child = view,
        };
        var canvas = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            Padding = new Thickness(20), Child = pill,
        };
        canvas.Measure(new Size(w + 40, h + 40));
        canvas.Arrange(new Rect(0, 0, w + 40, h + 40));
        canvas.UpdateLayout();

        Save(canvas, w + 40, h + 40, Path.Combine(dir, "Summarizing_t0.png"));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            canvas.UpdateLayout();
            Save(canvas, w + 40, h + 40, Path.Combine(dir, "Summarizing_t550.png"));
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    static void Save(Visual visual, double w, double h, string path)
    {
        const double scale = 2.0;
        var rtb = new RenderTargetBitmap(
            (int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static UserControl Build(PillState s) => s switch
    {
        PillState.MeetingPrompt => new MeetingPromptView(),
        PillState.RecordingBar  => new RecordingBarView(),
        PillState.RecordingOpen => new RecordingOpenView(),
        PillState.Summarizing   => new SummarizingView(),
        PillState.Notes         => new NotesView(),
        PillState.NoSpeech      => new NoSpeechView(),
        PillState.Home          => new HomeView(),
        PillState.Library       => new LibraryView(),
        PillState.Settings      => new SettingsView(),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
