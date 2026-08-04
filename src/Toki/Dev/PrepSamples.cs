using Toki.Calendar;
using Toki.Storage;

namespace Toki.Dev;

/// <summary>Seeded <see cref="PrepContent"/> for the render harness / --state demo (no DB, no network).</summary>
static class PrepSamples
{
    public static PrepContent Matched()
    {
        var e = CalendarSamples.Events().First(x => x.Id == "s1");   // Design review — PixelFlow (+ agenda)
        var m = new MeetingRecord
        {
            Title = "Design review — PixelFlow", Kind = MeetingKind.Meeting,
            StartedAt = DateTimeOffset.Now.AddDays(-2),
            Notes = new NoteSet
            {
                Tldr = "Tool is code-complete; blockers are brand tokens and the Make webhook. Ship moved to Thursday.",
                Actions =
                {
                    new ActionItem { Id = 1, Text = "Confirm brand font + hex.", Owner = "Priya" },
                    new ActionItem { Id = 2, Text = "Wire the Make webhook & /health slug.", Owner = "Sid" },
                },
            },
        };
        var open = m.Notes.Actions.Where(a => !a.Done).ToList();
        return new PrepContent(e, m, open, PrepBuilder.ParseAgenda(e.Description));
    }

    public static PrepContent FirstTime()
    {
        var e = CalendarSamples.Events().First(x => x.Id == "s3");   // Pricing sync — Maydit (no prior notes)
        return new PrepContent(e, null, Array.Empty<ActionItem>(), new[] { "Q3 tier proposal", "Renewal terms" });
    }
}
