using Toki.Calendar;

namespace Toki.Dev;

/// <summary>
/// Seeded calendar events for the offscreen render harness (<c>--render</c>) so the Home "COMING UP"
/// list and the Calendar page render realistically WITHOUT touching the network. Times are relative to
/// now so the "in 9 min" imminent treatment and the "now" line actually appear. Used only when
/// <see cref="App.Headless"/> - the real app always talks to <see cref="UpcomingCache"/>/Google.
/// </summary>
static class CalendarSamples
{
    public static IReadOnlyList<CalEvent> Events()
    {
        var now = DateTimeOffset.Now;
        var startOfDay = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        CalEvent Ev(string id, string title, DateTimeOffset start, int mins, bool meet, bool allDay,
                    string? loc, params (string name, string email, bool self, bool org)[] people)
        {
            var att = people.Select(p => new CalAttendee(p.email, p.name, "accepted", p.self, p.org)).ToList();
            return new CalEvent(id, title, start.ToUniversalTime(),
                start.AddMinutes(allDay ? 24 * 60 : mins).ToUniversalTime(), allDay, loc,
                meet ? "https://meet.google.com/abc-defg-hij" : null,
                "https://calendar.google.com/event", att);
        }

        return new List<CalEvent>
        {
            // Imminent (drives the amber "in N min" + Join treatment) - 9 min from now.
            Ev("s1", "Design review - PixelFlow", now.AddMinutes(9), 30, meet: true, allDay: false, null,
                ("You", "sid@studiomaydit.com", true, true), ("Priya", "priya@x.com", false, false), ("Rounak", "rounak@x.com", false, false))
                with { Description = "- Confirm launch blockers & owners\n- Lock the ship date" },
            // Later today, with a Meet link.
            Ev("s2", "Weekly 1:1 - Rounak", startOfDay.AddHours(14), 30, meet: true, allDay: false, null,
                ("You", "sid@studiomaydit.com", true, true), ("Rounak", "rounak@x.com", false, false)),
            // Tomorrow, no Meet link (in-person / no call).
            Ev("s3", "Pricing sync - Maydit", startOfDay.AddDays(1).AddHours(16), 30, meet: false, allDay: false, "Room 4",
                ("You", "sid@studiomaydit.com", true, false), ("Ana", "ana@x.com", false, true)),
            // A couple of days out, all-day.
            Ev("s4", "Company offsite", startOfDay.AddDays(2), 0, meet: false, allDay: true, null),
            // Later in the week, with a Meet link.
            Ev("s5", "Roadmap planning", startOfDay.AddDays(4).AddHours(11), 60, meet: true, allDay: false, null,
                ("You", "sid@studiomaydit.com", true, true), ("Team", "team@x.com", false, false)),
        };
    }
}
