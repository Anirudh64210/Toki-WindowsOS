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

/// <summary>One template section of a note - a heading and its bullets (e.g. "Blockers", "Ideas",
/// "Key concepts"). Which sections appear depends on the meeting's template (see NoteTemplates).</summary>
public sealed class NoteSection
{
    public string Heading { get; set; } = "";
    public List<string> Bullets { get; set; } = [];
}

/// <summary>The structured notes for a meeting. Summary + Action items are always the same shape;
/// the middle <see cref="Sections"/> are TEMPLATE-driven (standup → Updates/Blockers, interview →
/// Strengths/Concerns, …). Older/heuristic notes leave Sections empty and use the legacy
/// <see cref="Decisions"/>/<see cref="Questions"/> lists - <see cref="EffectiveSections"/> unifies both.</summary>
public sealed class NoteSet
{
    /// <summary>A concise, context-aware meeting title (LLM-written when available).</summary>
    public string? Title { get; set; }
    /// <summary>The template Toki wrote this note in (e.g. "Standup", "1:1"); null for legacy/heuristic.</summary>
    public string? Template { get; set; }
    /// <summary>True when the LLM actually produced this note; false for the heuristic floor.
    /// NOT persisted - it exists so a caller holding a good saved note can refuse to overwrite it
    /// with a silent heuristic fallback.</summary>
    public bool FromModel { get; set; }
    public string Tldr { get; set; } = "";
    /// <summary>Template-driven middle sections (heading + bullets). New notes populate this.</summary>
    public List<NoteSection> Sections { get; set; } = [];
    /// <summary>Legacy key points (heuristic floor + old saved meetings). Surfaced via EffectiveSections.</summary>
    public List<string> Decisions { get; set; } = [];
    public List<ActionItem> Actions { get; set; } = [];
    /// <summary>Retired from the UI (M6.1); kept for older saved meetings.</summary>
    public List<string> Questions { get; set; } = [];

    /// <summary>The middle sections to render, unifying the new template model with legacy notes:
    /// uses <see cref="Sections"/> when present, else synthesises "Key points" from
    /// <see cref="Decisions"/> and "Open questions" from <see cref="Questions"/>.</summary>
    public IReadOnlyList<NoteSection> EffectiveSections()
    {
        if (Sections.Count > 0) return Sections;
        var synth = new List<NoteSection>();
        if (Decisions.Count > 0) synth.Add(new NoteSection { Heading = "Key points", Bullets = Decisions });
        if (Questions.Count > 0) synth.Add(new NoteSection { Heading = "Open questions", Bullets = Questions });
        return synth;
    }
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

    /// <summary>
    /// What Toki quietly rewrote in this transcript: mis-heard names it unified, and self-repairs it
    /// dropped. Kept so the transcript can SHOW the change (underlined, original on hover) rather than
    /// silently presenting an edited record of what somebody said. The owner's rule: mark it in the
    /// transcript only - the notes stay clean.
    /// </summary>
    public List<TextFix> Corrections { get; set; } = [];
}

/// <summary>One word Toki changed, and what it changed it from.</summary>
/// <param name="From">What the recogniser produced.</param>
/// <param name="To">What the transcript now says.</param>
/// <param name="Reason">"name" (unified against the library) or "repair" (a spoken self-correction).</param>
public sealed record TextFix(string From, string To, string Reason);

/// <summary>Lightweight row for the Library list.</summary>
public sealed record MeetingSummary(
    long Id, MeetingKind Kind, string Title, string? App,
    DateTimeOffset StartedAt, int DurationMs, string Tldr, int OpenActions, int PeopleCount);

/// <summary>An open action item with the source meeting's id, title and date - for due-date-aware
/// task scoping (the meeting date is the reference for resolving a free-text due like "Friday").</summary>
public sealed record OpenAction(
    long MeetingId, string MeetingTitle, DateTimeOffset MeetingAt, string Text, string? Owner, string? Due);
