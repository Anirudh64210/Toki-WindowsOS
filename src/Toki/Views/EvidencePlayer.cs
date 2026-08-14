using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// The small play control that sits beside an evidence link, and the one output device behind it.
///
/// Only ONE clip is ever audible. Starting a second stops the first - two overlapping snippets of the
/// same meeting are unintelligible, and a play button that leaves a mess behind it stops being trusted.
/// The device is torn down when the clip ends, so nothing holds the sound card while Toki idles.
///
/// IT IS A REAL BUTTON, and the first version was not - it was a Border with a MouseLeftButtonUp
/// handler, which is the precise trap <see cref="NotePicker"/>'s own comment warns about: this control
/// lives inside a read-only RichTextBox, whose text-selection machinery captures the mouse on the DOWN
/// and eats the UP, so the handler never fired. The owner pressed play and nothing happened -
/// "hardcoded bullshit" - and they were right that it had never worked in the product; it had only
/// been proven against the clip engine underneath. A Button's Click is raised from the capture logic
/// itself, which is why every other clickable thing in this document (the ticks, the ↳ links) already
/// worked. The rule was written down twice before this and ignored a third time: on a notch or in a
/// document, a clickable thing is a Button, never a Border with a mouse handler.
///
/// See <see cref="EvidenceAudio"/> for why playing is free and exporting is the only thing that warns.
/// </summary>
public sealed class EvidencePlayer : Button
{
    static WaveOutEvent? _device;
    static EvidencePlayer? _playing;

    readonly MeetingRecord _meeting;
    readonly IReadOnlyList<TranscriptSegment> _range;
    readonly TextBlock _glyph;
    readonly Border _surface;
    DispatcherTimer? _progress;

    /// <summary>
    /// Null unless playing is GUARANTEED to work - a deleted recording, a file the moment falls off
    /// the end of, a range crossing streams, or a fragment below the minimum length all render
    /// nothing at all. A play button that can fail teaches people not to press play buttons.
    /// </summary>
    public static EvidencePlayer? For(MeetingRecord meeting, IReadOnlyList<TranscriptSegment> range, double fontSize) =>
        EvidenceAudio.Available(meeting, range, meeting.Transcript) ? new EvidencePlayer(meeting, range, fontSize) : null;

    public static EvidencePlayer? For(MeetingRecord meeting, TranscriptSegment segment, double fontSize) =>
        For(meeting, new[] { segment }, fontSize);

    EvidencePlayer(MeetingRecord meeting, IReadOnlyList<TranscriptSegment> range, double fontSize)
    {
        _meeting = meeting;
        _range = range;

        // A quiet pill, not a bare triangle: the glyph sits in a filled chip like the other inline
        // affordances in a note, so it reads as a designed control rather than a stray character.
        _glyph = new TextBlock
        {
            Text = "▶", FontSize = 8.5, Margin = new Thickness(1, 0, 0, 0),   // the triangle is optically left-heavy
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

        _surface = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Child = _glyph,
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "Chip");

        Content = _surface;
        Template = BareTemplate();
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Focusable = false;
        Cursor = Cursors.Hand;
        ToolTip = "Hear this moment";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(5, 0, 0, 0);

        MouseEnter += (_, _) => { if (!ReferenceEquals(_playing, this)) _surface.SetResourceReference(Border.BackgroundProperty, "HoverOverlay"); };
        MouseLeave += (_, _) => { if (!ReferenceEquals(_playing, this)) _surface.SetResourceReference(Border.BackgroundProperty, "Chip"); };
        Click += (_, _) => Toggle();
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(); };
        Unloaded += (_, _) => { if (ReferenceEquals(_playing, this)) StopAll(); };
    }

    /// <summary>Renders exactly the content - the pill IS the button, no chrome around it.</summary>
    static ControlTemplate BareTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        return t;
    }

    void Toggle()
    {
        if (ReferenceEquals(_playing, this)) { StopAll(); return; }
        StopAll();

        var (start, end) = EvidenceAudio.Window(_range, _meeting.Transcript);
        if (EvidenceAudio.WavFor(_meeting, _range[0].Speaker) is not { } wav) return;
        if (EvidenceAudio.Clip(wav, start, end) is not { } bytes) return;

        try
        {
            var reader = new WaveFileReader(new MemoryStream(bytes));
            _device = new WaveOutEvent();
            _device.Init(reader);
            _device.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(new Action(StopAll));
            _device.Play();
            _playing = this;
            _glyph.Text = "■";
            _glyph.Margin = new Thickness(0);
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Pink");
            _surface.SetResourceReference(Border.BackgroundProperty, "Chip");   // the pink glyph carries the state
            ToolTip = "Stop";

            // A backstop: PlaybackStopped is reliable, but a control that can get stuck showing "stop"
            // is a control people stop pressing.
            _progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(end - start + 600) };
            _progress.Tick += (_, _) => StopAll();
            _progress.Start();
        }
        catch (Exception ex)
        {
            App.Log($"evidence playback failed: {ex.Message}");
            StopAll();
        }
    }

    static void StopAll()
    {
        try { _device?.Stop(); _device?.Dispose(); } catch { }
        _device = null;
        _playing?.Reset();
        _playing = null;
    }

    void Reset()
    {
        _progress?.Stop();
        _progress = null;
        _glyph.Text = "▶";
        _glyph.Margin = new Thickness(1, 0, 0, 0);
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _surface.SetResourceReference(Border.BackgroundProperty, "Chip");
        ToolTip = "Hear this moment";
    }

    /// <summary>Right-click offers the export. Deliberately not a visible button: listening is the
    /// everyday act and sending someone's voice to another person is not, so they do not deserve equal
    /// prominence on the page.</summary>
    void ShowMenu() => NotePicker.Show(this, new List<(string, Action?)>
    {
        ("Save this clip…", Export),
    });

    void Export()
    {
        if (!ConsentToExport()) return;

        var (start, end) = EvidenceAudio.Window(_range, _meeting.Transcript);
        if (EvidenceAudio.WavFor(_meeting, _range[0].Speaker) is not { } wav) return;
        if (EvidenceAudio.Clip(wav, start, end) is not { } bytes) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = EvidenceAudio.SuggestedFileName(_meeting, start),
            DefaultExt = ".wav",
            Filter = "Audio clip (*.wav)|*.wav",
            Title = "Save this moment",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
            App.Log($"evidence clip exported ({bytes.Length} bytes) from meeting {_meeting.Id}");
        }
        catch (Exception ex)
        {
            App.Log($"evidence clip export failed: {ex.Message}");
            ConfirmDialog.Show(Window.GetWindow(this), "Could not save the clip",
                ex.Message, "OK", danger: false);
        }
    }

    /// <summary>
    /// Said once, the first time, in plain words.
    ///
    /// Not a legal notice and not a scare - the user recorded this meeting and it is their file. What
    /// they may not have thought about is that the clip carries someone else's voice, and that unlike
    /// everything else Toki does, a file that has been sent cannot be unsent. That is worth one
    /// sentence, once. Asking every time would train people to click through it.
    /// </summary>
    bool ConsentToExport()
    {
        var s = Llm.AppSettings.Current;
        if (s.AudioSharingAcknowledged) return true;

        var ok = ConfirmDialog.Show(Window.GetWindow(this),
            "This clip has other people's voices in it",
            "Saving it puts a piece of the recording outside Toki. Once you send it on, there is no way "
            + "to take it back - so share it the way you would share a quote from someone.",
            "Save the clip", danger: false);
        if (!ok) return false;

        try { s.AudioSharingAcknowledged = true; s.Save(); }
        catch (Exception ex) { App.Log($"save audio-sharing acknowledgement: {ex.Message}"); }
        return true;
    }
}
