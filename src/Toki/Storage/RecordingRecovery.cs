using System.IO;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Startup sweep over %APPDATA%\Toki\Recordings: folders no saved meeting references are orphans.
/// Real audio is recovered into a saved meeting; tiny/silent leftovers are deleted so no invisible
/// voice data accumulates. Runs on the warm-up thread, never at meeting time.
/// </summary>
public static class RecordingRecovery
{
    static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "Recordings");

    /// <summary>Anything smaller is an accidental tap's worth of audio - not a meeting.</summary>
    const long MinRecoverBytes = 100_000;

    /// <summary>Dropped into a folder by DiscardMeeting before its row is deleted. A marked folder
    /// is a grave, not an orphan: the sweep deletes it and must NEVER transcribe it back to life.</summary>
    public const string DiscardMarker = ".discarded";

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

        // A leftover claim from a sweep that crashed mid-recovery: give it its real name back and
        // let the NEXT pass judge it fresh. Claiming a claim would stack suffixes forever.
        if (dir.EndsWith(".recovering", StringComparison.OrdinalIgnoreCase))
        {
            var original = dir[..^".recovering".Length];
            try { if (!Directory.Exists(original)) Directory.Move(dir, original); }
            catch (Exception ex) { App.Log($"recovery: could not restore crashed claim {dir}: {ex.Message}"); }
            return;
        }

        // A tombstoned folder is a DELETED meeting whose folder cleanup failed (file in use, or a
        // crash between row-delete and folder-delete). Deleting it finishes the user's discard;
        // recovering it would resurrect what they explicitly removed.
        if (File.Exists(Path.Combine(dir, DiscardMarker)))
        {
            try { Directory.Delete(dir, true); App.Log($"recovery: finished discarding {dir}"); }
            catch (Exception ex) { App.Log($"recovery: tombstoned folder still locked, next launch: {ex.Message}"); }
            return;
        }

        // A fresh folder may not have its WAVs yet (Start() creates them on a background task);
        // without this age gate the "tiny" rule would delete a LIVE recording's folder.
        if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < TimeSpan.FromMinutes(2)) return;

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

        // A live meeting owns the recognizer: a whole-file pass would hold the shared decode gate
        // for minutes and starve the live transcriber. Defer to the next launch.
        if (App.IsRecording) { App.Log($"recovery: meeting in progress - {dir} deferred to next launch"); return; }

        // The `known` snapshot predates a possibly minutes-long sweep; re-check at claim time or a
        // meeting saved meanwhile becomes two Library entries sharing one folder.
        if (App.Store.AllFolders().Select(Path.GetFullPath)
            .Contains(Path.GetFullPath(dir), StringComparer.OrdinalIgnoreCase)) return;

        // Claim by rename (the repo rule for one-shot jobs): a live session holding its WAVs open
        // makes the rename fail, which is the right answer. Renamed back so Folder stays true.
        var claimed = dir + ".recovering";
        try { Directory.Move(dir, claimed); }
        catch (Exception ex) { App.Log($"recovery: could not claim {dir} (in use?): {ex.Message}"); return; }
        try
        {
            RecoverClaimed(claimed, dir);
        }
        finally
        {
            if (Directory.Exists(claimed))
                try { Directory.Move(claimed, dir); }
                catch (Exception ex) { App.Log($"recovery: could not un-claim {claimed}: {ex.Message}"); }
        }
    }

    static void RecoverClaimed(string dir, string finalDir)
    {
        var wavs = Directory.GetFiles(dir, "*.wav");
        var bytes = wavs.Sum(w => new FileInfo(w).Length);
        App.Log($"recovery: transcribing orphaned recording {finalDir} ({bytes / 1024}KB)");
        var transcript = TranscriptionService.Instance.Process(dir);
        var words = transcript?.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) ?? 0;
        if (transcript is null) return;                       // ASR error - keep the audio
        if (transcript.Count == 0 || words < 12)
        {
            Directory.Delete(dir, true);                      // genuinely nothing said
            App.Log($"recovery: orphan {dir} was silent/trivial - deleted");
            return;
        }

        // Times and the saved Folder path come from the FINAL name - the claim suffix is
        // temporary and the row must point at where the folder lives after un-claiming.
        var startedAt = DateTimeOffset.TryParseExact(Path.GetFileName(finalDir), "yyyyMMdd-HHmmss",
                null, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : new DateTimeOffset(Directory.GetCreationTime(dir));
        var notes = NoteExtractor.Extract(transcript);
        var meeting = new MeetingRecord
        {
            Kind = MeetingKind.Meeting,
            StartedAt = startedAt,
            DurationMs = transcript.Max(s => s.EndMs),
            Folder = finalDir,
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
