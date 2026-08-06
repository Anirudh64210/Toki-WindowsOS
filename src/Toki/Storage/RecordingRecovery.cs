using System.IO;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Startup sweep over %APPDATA%\Toki\Recordings: any folder no saved meeting references is an
/// orphan - a crash or quit mid-recording (the WAVs survive thanks to periodic header flushes),
/// or an old discarded take from before cleanup existed. Real audio is RECOVERED into a saved
/// meeting (transcribe → heuristic notes → Library), so a crash converts to "your meeting is
/// still here" instead of silent data loss; tiny/silent leftovers are deleted so no invisible
/// voice data accumulates on disk. Runs on the warm-up thread, never at meeting time.
/// </summary>
public static class RecordingRecovery
{
    static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "Recordings");

    /// <summary>Anything smaller is an accidental tap's worth of audio - not a meeting.</summary>
    const long MinRecoverBytes = 100_000;

    public static void Sweep()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var known = new HashSet<string>(App.Store.AllFolders().Select(Path.GetFullPath),
                                            StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(Root))
            {
                try { SweepOne(dir, known); }
                catch (Exception ex) { App.Log($"recovery: '{dir}' failed (kept): {ex.Message}"); }
            }
        }
        catch (Exception ex) { App.Log($"recovery sweep failed (non-fatal): {ex.Message}"); }
    }

    static void SweepOne(string dir, HashSet<string> known)
    {
        if (known.Contains(Path.GetFullPath(dir))) return;

        // Never touch a folder that's still being written - an active recording's WAVs
        // update continuously, so a recent file write means live capture, not an orphan.
        var wavs = Directory.GetFiles(dir, "*.wav");
        if (wavs.Any(w => DateTime.UtcNow - File.GetLastWriteTimeUtc(w) < TimeSpan.FromMinutes(2)))
            return;

        var bytes = wavs.Sum(w => new FileInfo(w).Length);
        if (bytes < MinRecoverBytes)
        {
            Directory.Delete(dir, true);
            App.Log($"recovery: deleted tiny orphan folder {dir}");
            return;
        }

        // Real audio with no Library entry. Without the ASR model we can't judge it - keep it
        // for a later launch rather than ever deleting a meeting's only artifact.
        if (!TranscriptionService.ModelReady) return;

        App.Log($"recovery: transcribing orphaned recording {dir} ({bytes / 1024}KB)");
        var transcript = TranscriptionService.Instance.Process(dir);
        var words = transcript?.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) ?? 0;
        if (transcript is null) return;                       // ASR error - keep the audio
        if (transcript.Count == 0 || words < 12)
        {
            Directory.Delete(dir, true);                      // genuinely nothing said
            App.Log($"recovery: orphan {dir} was silent/trivial - deleted");
            return;
        }

        var startedAt = DateTimeOffset.TryParseExact(Path.GetFileName(dir), "yyyyMMdd-HHmmss",
                null, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : new DateTimeOffset(Directory.GetCreationTime(dir));
        var notes = NoteExtractor.Extract(transcript);
        var meeting = new MeetingRecord
        {
            Kind = MeetingKind.Meeting,
            StartedAt = startedAt,
            DurationMs = transcript.Max(s => s.EndMs),
            Folder = dir,
            Transcript = transcript.ToList(),
            Notes = notes,
            Title = NoteExtractor.Title(notes, null, startedAt),
        };
        App.Store.Save(meeting);
        MarkdownMirror.Write(meeting);
        LibraryWindow.MarkDirty();
        App.Log($"recovery: saved recovered meeting '{meeting.Title}' (id {meeting.Id})");
    }
}
