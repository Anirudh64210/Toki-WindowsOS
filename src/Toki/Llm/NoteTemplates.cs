namespace Toki.Llm;

/// <summary>One note template: a name, a one-line "when it applies" (for auto-detection), and the
/// middle section headings it produces. SUMMARY (always) and ACTION ITEMS (when tasks exist) are
/// implicit and added by the writer - a template only defines the sections in between.</summary>
public sealed record NoteTemplate(string Name, string When, string[] Sections);

/// <summary>
/// The fixed set of note shapes Toki auto-detects between. The writer picks the best fit,
/// the user can override it. General is the safe default when nothing else clearly fits.
/// </summary>
public static class NoteTemplates
{
    public static readonly NoteTemplate General = new(
        "General", "any meeting that doesn't clearly fit a more specific type",
        new[] { "Key points" });

    public static readonly IReadOnlyList<NoteTemplate> All = new[]
    {
        General,
        new NoteTemplate("Standup", "a team status check-in where people report progress on their work",
            new[] { "Updates", "Blockers" }),
        new NoteTemplate("1:1", "a one-on-one between two people (e.g. manager & report, mentor & mentee)",
            new[] { "Discussion", "Feedback" }),
        new NoteTemplate("Interview", "a job or candidate interview",
            new[] { "Background", "Strengths", "Concerns", "Recommendation" }),
        new NoteTemplate("Brainstorm", "an idea-generation, planning, or design session",
            new[] { "Ideas", "Themes" }),
        new NoteTemplate("Lecture", "a class, lecture, talk, seminar, or training session",
            new[] { "Key concepts", "Definitions", "To review" }),
        new NoteTemplate("Sales call", "a sales, prospect, or client call",
            new[] { "Client needs", "Objections" }),
        new NoteTemplate("Decision log", "a meeting whose main purpose is to make a decision",
            new[] { "Options considered", "Decision", "Rationale" }),
    };

    public static NoteTemplate ByName(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), System.StringComparison.OrdinalIgnoreCase))
        ?? General;

    /// <summary>The classification menu shown to the model: "Name - when it applies".</summary>
    public static string Menu() => string.Join("\n", All.Select(t => $"- {t.Name}: {t.When}"));
}
