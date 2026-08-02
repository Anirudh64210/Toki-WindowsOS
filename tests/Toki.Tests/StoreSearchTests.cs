using System.IO;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>FTS5 retrieval + the open-tasks query used by "Ask your notes".</summary>
public sealed class StoreSearchTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-fts-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;

    public StoreSearchTests() => _store = new MeetingStore(_dbPath);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    MeetingRecord Meeting(string title, string summary, params string[] transcript) => new()
    {
        Kind = MeetingKind.Meeting,
        Title = title,
        StartedAt = DateTimeOffset.Now,
        Notes = new NoteSet { Tldr = summary },
        Transcript = transcript.Select((t, i) => new TranscriptSegment(Speaker.You, i * 1000, i * 1000 + 900, t)).ToList(),
    };

    [Fact]
    public void FtsRanksTheRelevantMeetingFirst()
    {
        _store.Save(Meeting("Budget planning", "We set the marketing budget for Q3.", "The budget spreadsheet is ready."));
        _store.Save(Meeting("Design review", "We reviewed the new onboarding screens.", "The pink mascot looks great."));

        var ids = _store.SearchRelevant("marketing budget spreadsheet", 5);

        Assert.NotEmpty(ids);
        var top = _store.Get(ids[0]);
        Assert.Equal("Budget planning", top!.Title);
    }

    [Fact]
    public void FtsFindsWordsFromTheTranscript()
    {
        _store.Save(Meeting("Sync", "General catch-up.", "We should migrate to Postgres next sprint."));
        var ids = _store.SearchRelevant("postgres", 5);
        Assert.Contains(ids, id => _store.Get(id)!.Title == "Sync");
    }

    [Fact]
    public void OpenActionsReturnsOnlyUndoneItems()
    {
        var m = Meeting("Standup", "Daily standup.", "Let's split the work.");
        m.Notes.Actions.Add(new ActionItem { Text = "Ship the installer", Owner = "Sid", Done = false });
        m.Notes.Actions.Add(new ActionItem { Text = "Already merged", Done = true });
        _store.Save(m);

        var open = _store.OpenActions(10);

        Assert.Single(open);
        Assert.Equal("Ship the installer", open[0].Text);
        Assert.Equal("Sid", open[0].Owner);
        Assert.Equal("Standup", open[0].Meeting);
    }

    [Fact]
    public void DeletedMeetingDropsOutOfSearch()
    {
        var id = _store.Save(Meeting("Ephemeral", "Talked about kangaroos.", "Kangaroos hop."));
        Assert.NotEmpty(_store.SearchRelevant("kangaroos", 5));
        _store.Delete(id);
        Assert.Empty(_store.SearchRelevant("kangaroos", 5));
    }
}
