using System.Globalization;
using System.Text.RegularExpressions;
using Toki.Calendar;

namespace Toki.Llm;

/// <summary>A calendar create the user asked for by voice/text, held on <see cref="ConversationContext"/>
/// until they confirm. <see cref="Start"/> is null while we're still asking "when?".</summary>
public sealed record PendingCalendarCreate(
    string Title, DateTimeOffset? Start, DateTimeOffset? End,
    IReadOnlyList<string> Emails, string? PersonMentioned);

/// <summary>
/// Deterministic "create a meeting" command for the dialogue brain: detects a scheduling utterance
/// ("schedule a call with Ana tomorrow at 3"), extracts title/time via the shared
/// <see cref="QuickEventParser"/>, and — crucially — **never creates anything until the user confirms**
/// (a mishearing must not silently book a meeting). Provider-neutral (goes through CalendarService).
/// </summary>
public static class CalendarCommand
{
    // ── test seams (house style, like NotesQa.StoreOverride / LlmOverride) ──
    public static bool? ConnectedOverride;
    public static Func<NewEvent, CancellationToken, Task<CalEvent>>? CreateOverride;

    const RegexOptions Ro = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Event nouns that make a create verb a scheduling command (NOT "note" — that's NewNote's job).
    const string Nouns = @"(meeting|call|event|appointment|sync|catch[- ]?up|check[- ]?in|1[: ]?1|one[- ]on[- ]one|stand[- ]?up|interview|demo|session|review|lunch|coffee|chat)";

    // Verb directly followed by (article) + an event noun: "schedule a call", "set up a sync",
    // "create a meeting". Anchored so "create a SUMMARY of the meeting" can't fire (summary ≠ noun here).
    static readonly Regex CreateVerbNoun = new($@"^(schedule|book|set\s*up|create|add|make|plan|arrange|new)\s+(a|an|the|another)?\s*{Nouns}\b", Ro);
    // "put lunch on my calendar", "add the demo to my calendar".
    static readonly Regex CalendarPhrase = new(@"^(put|add|drop|throw)\b.*\b(on|onto|to)\s+(my|the)\s+calendar\b", Ro);
    static readonly Regex RemindLead = new(@"^remind\s+me\s+to\b", Ro);
    static readonly Regex NoteVeto = new(@"\bnotes?\b", Ro);
    static readonly Regex LeadChrome = new($@"^(schedule|book|set\s*up|create|add|make|plan|arrange|new|put|drop|throw)\s+(a|an|the|another)?\s*", Ro);
    static readonly Regex TrailCalendar = new(@"\s*(on|onto|to)\s+(my|the)\s+calendar\b.*$", Ro);
    static readonly Regex WithPerson = new(@"\bwith\s+([A-Za-z][A-Za-z'’.-]{1,})\b", Ro);
    static readonly HashSet<string> PersonStop = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "my", "our", "your", "his", "her", "their", "them", "everyone", "team" };

    static readonly Regex Yes = new(@"^(yes|yeah|yep|yup|sure|ok(ay)?|confirm|do it|go ahead|create( it)?|book it|schedule it|sounds good|please do)$", Ro);
    static readonly Regex No = new(@"^(no|nope|nah|don'?t|skip( it)?|leave it|not now|scratch that|never\s*mind|cancel)$", Ro);

    public static bool IsYes(string? t) => Yes.IsMatch(Trim(t));
    public static bool IsNo(string? t) => No.IsMatch(Trim(t));
    static string Trim(string? t) => (t ?? "").Trim().TrimEnd('.', '!', '?').Trim();

    /// <summary>Detect a scheduling command and extract a draft, or null. Pure, ~1ms, no model/network.</summary>
    public static PendingCalendarCreate? TryDetect(string? input, DateTimeOffset now)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return null;

        // Strip ASR/politeness fluff (shared with CommandRouter — one source of truth).
        text = CommandRouter.Fluff.Replace(text, "").Trim();
        text = CommandRouter.TrailFluff.Replace(text, "").Trim();
        if (text.Length == 0) return null;

        // A question is never a create ("when is my call with Ana", "what meetings do I have").
        if (CommandRouter.QuestionLead.IsMatch(text)) return null;
        if (NoteVeto.IsMatch(text)) return null;                                   // "add a note" → NewNote's job
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 16) return null;  // a create is short-ish

        if (!IsCreateShaped(text)) return null;

        var parsed = QuickEventParser.Parse(text, now);
        var (title, person) = ExtractTitle(text, parsed);
        return new PendingCalendarCreate(title, parsed.Start, parsed.End, parsed.Emails, person);
    }

    internal static bool IsCreateShaped(string text) =>
        RemindLead.IsMatch(text) || CreateVerbNoun.IsMatch(text) || CalendarPhrase.IsMatch(text);

    internal static (string Title, string? Person) ExtractTitle(string text, QuickEvent parsed)
    {
        string? person = null;
        var pm = WithPerson.Match(text);
        if (pm.Success && !PersonStop.Contains(pm.Groups[1].Value)) person = Cap(pm.Groups[1].Value);

        // QuickEventParser already stripped the date/time span + emails.
        var body = parsed.Summary.Length > 0 ? parsed.Summary : text;

        if (RemindLead.IsMatch(text))
        {
            body = TrailCalendar.Replace(RemindLead.Replace(body, ""), "").Trim(' ', ',', '-', '·');
            return (body.Length > 0 ? "Reminder: " + body : "Reminder", person);
        }

        body = LeadChrome.Replace(body, "");
        body = TrailCalendar.Replace(body, "").Trim(' ', ',', '-', '·');
        if (body.Length == 0) body = Regex.Match(text, Nouns, Ro) is { Success: true } m ? Cap(m.Value) : "Meeting";
        return (Cap(body), person);
    }

    // ── confirm / result copy (one place; provider name never hard-coded) ──

    static string Provider => CalendarService.Instance.ProviderName;

    public static string ConfirmPrompt(PendingCalendarCreate p)
    {
        // The start keeps its own am/pm whenever it differs from the end's — "11:30–12:00 PM"
        // read as an EVENING slot ("11:30 PM"), on the one line whose job is time verification.
        var when = "sometime";
        if (p.Start is { } s)
        {
            var end = p.End ?? s.AddMinutes(30);
            var sameHalf = s.ToString("tt") == end.ToString("tt");
            when = sameHalf ? $"{DayLabel(s)}, {s:h:mm}–{end:h:mm tt}"
                            : $"{DayLabel(s)}, {s:h:mm tt}–{end:h:mm tt}";
        }
        var msg = $"Create “{p.Title}” — {when} on {Provider}? Say yes to create it, or no to skip.";
        if (p.PersonMentioned is { } who) msg += $" {who}’s in the title — I can only invite people by email.";
        return msg;
    }

    public static string WhenPrompt(PendingCalendarCreate p) =>
        $"When should “{p.Title}” be? Say a day and time, like “tomorrow at 3”.";

    public static string NotConnected() =>
        "I can’t create events yet — connect Google Calendar in Settings and I’ll take care of it.";

    public static string Cancelled() => "Okay, scrapped — nothing was created.";

    static string Success(CalEvent e) =>
        $"Done — “{e.Summary}” is on your {Provider}, {DayLabel(e.StartLocal)} at {e.StartLocal:h:mm tt}.";

    static string Reconnect() =>
        "Your Google Calendar link expired — reconnect in Settings, then ask me again. Nothing was created.";

    static string Failed() =>
        "Couldn’t reach Google Calendar just now — nothing was created. Try again in a moment.";

    /// <summary>Create the event (after the user confirmed). Never throws; returns the spoken/written
    /// result. Honest on not-connected / reconnect / failure. Optimistically updates the cache.</summary>
    public static async Task<string> ExecuteAsync(PendingCalendarCreate p, CancellationToken ct = default)
    {
        if (Toki.App.Headless) return Cancelled();
        if (p.Start is not { } start) return WhenPrompt(p);
        if (!(ConnectedOverride ?? CalendarService.Instance.IsConnected)) return NotConnected();

        var ev = new NewEvent(p.Title, start, p.End ?? start.AddMinutes(30), p.Emails, AddMeet: false,
            Description: p.PersonMentioned is { } who ? $"Mentioned: {who} (no email — invite them from Google Calendar)." : null);
        try
        {
            var created = CreateOverride is { } ov
                ? await ov(ev, ct).ConfigureAwait(false)
                : await CalendarService.Instance.CreateAsync(ev, ct).ConfigureAwait(false);
            if (CreateOverride is null) UpcomingCache.Instance.Upsert(created);
            return Success(created);
        }
        catch (GoogleAuthException gx) when (gx.NeedsReconnect) { return Reconnect(); }
        catch (Exception ex) { Toki.App.Log($"voice create failed: {ex.Message}"); return Failed(); }
    }

    // ── helpers ──

    static string DayLabel(DateTimeOffset d)
    {
        var day = d.Date; var today = DateTimeOffset.Now.Date;
        if (day == today) return "today";
        if (day == today.AddDays(1)) return "tomorrow";
        return d.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
