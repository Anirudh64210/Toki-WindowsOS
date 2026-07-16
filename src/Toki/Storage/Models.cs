using Toki.Transcription;

namespace Toki.Storage;

public enum MeetingKind { Meeting, Manual }

public sealed class ActionItem
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public string? Owner { get; set; }
    public string? Due { get; set; }
    public bool Done { get; set; }
}

/// <summary>The structured notes for a meeting (heuristic floor now; LLM-written in M6).</summary>
public sealed class NoteSet
{
    /// <summary>A concise, context-aware meeting title (LLM-written when available).</summary>
    public string? Title { get; set; }
    public string Tldr { get; set; } = "";
    /// <summary>Key discussion points (stored in the legacy "decisions" table).</summary>
    public List<string> Decisions { get; set; } = [];
    public List<ActionItem> Actions { get; set; } = [];
    /// <summary>Retired from the UI (M6.1); kept for older saved meetings.</summary>
    public List<string> Questions { get; set; } = [];
}

/// <summary>A full meeting (or manual note) with its transcript and notes.</summary>
public sealed class MeetingRecord
{
    public long Id { get; set; }
    public MeetingKind Kind { get; set; } = MeetingKind.Meeting;
    public string Title { get; set; } = "";
    public string? App { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public int DurationMs { get; set; }
    public string? Folder { get; set; }          // recording folder (audio)
    public string? Body { get; set; }            // freeform markdown for manual notes
    public NoteSet Notes { get; set; } = new();
    public List<TranscriptSegment> Transcript { get; set; } = [];
}

/// <summary>Lightweight row for the Library list.</summary>
public sealed record MeetingSummary(
    long Id, MeetingKind Kind, string Title, string? App,
    DateTimeOffset StartedAt, int DurationMs, string Tldr, int OpenActions, int PeopleCount);
