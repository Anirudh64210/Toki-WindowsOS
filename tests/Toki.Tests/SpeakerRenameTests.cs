using System;
using System.IO;
using System.Linq;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Renaming a speaker follows through to the to-dos. Written AFTER a mutation probe proved the
/// owner-update half of RenameSpeaker (added in the 2026-08-18 diarisation audit) had no test:
/// breaking it left the whole suite green. A rename that fixes the transcript but leaves a task
/// owned by "Speaker 2" is exactly the stale-attribution the audit flagged.
/// </summary>
public sealed class SpeakerRenameTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-rename-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;

    public SpeakerRenameTests() => _store = new MeetingStore(_dbPath);
    public void Dispose() { try { File.Delete(_dbPath); } catch { } }

    [Fact]
    public void Renaming_a_speaker_renames_their_todos_and_nobody_elses()
    {
        var m = new MeetingRecord
        {
            Kind = MeetingKind.Meeting,
            Title = "Sync",
            StartedAt = DateTimeOffset.Now,
            Transcript =
            [
                new TranscriptSegment(Speaker.Them, 0, 4000, "I'll benchmark the replication lag.") { Tag = "Speaker 2" },
            ],
            Notes = new NoteSet
            {
                Actions =
                [
                    new ActionItem { Text = "Benchmark replication lag", Owner = "Speaker 2" },
                    new ActionItem { Text = "Send the deck", Owner = "You" },
                ],
            },
        };
        _store.Save(m);

        _store.RenameSpeaker(m.Id, "Speaker 2", "Priya");

        var back = _store.Get(m.Id)!;
        Assert.Equal("Priya", back.Transcript.Single().Tag);
        Assert.Equal("Priya", back.Notes.Actions.Single(a => a.Text.Contains("Benchmark")).Owner);
        Assert.Equal("You", back.Notes.Actions.Single(a => a.Text.Contains("deck")).Owner);   // untouched
    }
}
