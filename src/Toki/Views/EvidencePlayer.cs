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
/// The play control beside an evidence link, and the one output device behind it. Only ONE clip is
/// ever audible: starting a second stops the first, and the device is torn down when the clip ends.
/// Must be a real Button: it lives inside a read-only RichTextBox, whose text-selection machinery
/// captures the mouse on the down and eats the up, so only Click fires.
/// See <see cref="EvidenceAudio"/> for why playing is free and exporting is the only thing that warns.
/// </summary>
public sealed class EvidencePlayer : Button
{
    static WaveOutEvent? _device;
    static EvidencePlayer? _playing;

    readonly MeetingRecord _meeting;
    readonly IReadOnlyList<TranscriptSegment> _range;
    DispatcherTimer? _progress;

    /// <summary>
    /// Null unless playing is GUARANTEED to work: a deleted recording, an out-of-range moment,
    /// a range crossing streams, or a fragment below the minimum length all render nothing.
    /// </summary>
    public static EvidencePlayer? For(MeetingRecord meeting, IReadOnlyList<TranscriptSegment> range, double fontSize) =>
        EvidenceAudio.Available(meeting, range, meeting.Transcript) ? new EvidencePlayer(meeting, range, fontSize) : null;

    public static EvidencePlayer? For(MeetingRecord meeting, TranscriptSegment segment, double fontSize) =>
        For(meeting, new[] { segment }, fontSize);

    EvidencePlayer(MeetingRecord meeting, IReadOnlyList<TranscriptSegment> range, double fontSize)
    {
        _meeting = meeting;
        _range = range;

        // The glyph is a PATH, never a font character: fallback-font metrics (size, bearings,
        // vertical position) are outside our control.
        Content = PillParts.PlayGlyph("TextDim");
        Style = (Style)Application.Current.Resources["EvidenceChip"];
        ToolTip = "Hear this moment";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(5, 0, 0, 0);

        Click += (_, _) => Toggle();
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(); };
        Unloaded += (_, _) => { if (ReferenceEquals(_playing, this)) StopAll(); };
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
            var device = new WaveOutEvent();
            _device = device;
            _device.Init(reader);
            // Guard against the OLD device's stop event: it dispatches AFTER the new device has
            // started and would otherwise tear the new one down.
            _device.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_device, device)) StopAll();
            }));
            _device.Play();
            _playing = this;
            Content = PillParts.StopGlyph("Pink");
            ToolTip = "Stop";

            // Backstop timer so the control can never get stuck showing "stop".
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
        Content = PillParts.PlayGlyph("TextDim");
        ToolTip = "Hear this moment";
    }

    /// <summary>Right-click offers the export; deliberately not a visible button, so sharing
    /// someone's voice never has equal prominence with listening.</summary>
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
    /// One-time acknowledgement that an exported clip carries other people's voices.
    /// Asked once only: asking every time trains people to click through it.
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
