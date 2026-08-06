using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace Toki.Llm;

/// <summary>What kind of thing the user is asking for - decided from the QUERY (never from note
/// titles/content, which is what caused the "a title containing 'follow up' triggered the task list" bug).
/// <see cref="Analyze"/> is the REASONING route: questions that need thinking/synthesis across meetings
/// ("what should I focus on?", "themes across my meetings", "how is X progressing?") rather than a
/// direct lookup - they get broader retrieval and a reasoning prompt (the Adaptive-RAG pattern:
/// route by question complexity, don't force everything through one narrow pipeline).</summary>
public enum QueryIntent { SummarizeOne, LookupFact, ListTasks, ListMeetings, Count, Analyze }

public enum RecencyDir { Latest, Oldest }

/// <summary>
/// The deterministic "understand the question first" layer. Before any retrieval or LLM call, this
/// pulls out (a) any TIME expression → a concrete date range or a recency rank ("most recent"), and
/// (b) the INTENT. Exact questions ("my most recent meeting", "meeting on July 30", "how many
/// meetings last week") then get resolved by SQL, so the model never has to guess dates or recency.
/// Fully local, ~1ms, no model. Past-preference: meetings are history, so an ambiguous date resolves
/// to the most recent PAST reading.
/// </summary>
public sealed record QueryPlan
{
    public QueryIntent Intent { get; init; }
    public DateTimeOffset? RangeStart { get; init; }   // inclusive
    public DateTimeOffset? RangeEnd { get; init; }     // exclusive
    public RecencyDir? Rank { get; init; }             // set for "most recent / latest / oldest"
    public int RankN { get; init; } = 1;
    public string Residual { get; init; } = "";        // query minus the time expression (the topical part)
    public string Original { get; init; } = "";
    /// <summary>When a follow-up ("what about the action items?") resolves to specific meetings from the
    /// prior turn, they're pinned here so resolution uses them directly instead of re-searching.</summary>
    public List<long>? PinnedMeetingIds { get; init; }

    public bool HasDateRange => RangeStart.HasValue && RangeEnd.HasValue;
    public bool HasTemporal => HasDateRange || Rank.HasValue;
}

/// <summary>Carries the last turn's resolved state so follow-ups ("before that", "what about its action
/// items?", bare "last week") make sense. Held per Ask conversation thread by the UI.</summary>
public sealed class ConversationContext
{
    public QueryIntent? LastIntent;
    public List<long> LastMeetingIds = new();
    public DateTimeOffset? LastRangeStart;
    public DateTimeOffset? LastRangeEnd;

    /// <summary>The conversation transcript so far - ("user"/"toki", text) pairs, appended by the
    /// dialogue brain after every answered turn. This is the WORKING MEMORY that lets the general
    /// conversation path hold a real multi-turn chat (the model sees the recent turns), which the
    /// old pipeline never had - every turn was answered as if it were the first.</summary>
    public List<(string Role, string Text)> Turns = new();

    /// <summary>A calendar event the user asked to create, awaiting their confirmation ("yes"/"no").
    /// Non-null ONLY for the single turn after a confirm/"when?" prompt - cleared before any branch so a
    /// stray later "yes" can never create an event, and a create never happens without confirmation.</summary>
    public PendingCalendarCreate? PendingCreate;

    /// <summary>Facts the user stated THIS session ("Works at Contoso") - echoed into the general
    /// prompt so "I work at X" → "where do I work?" holds within the conversation. Durable facts
    /// also persist via <see cref="UserProfile"/>.</summary>
    public List<string> SessionFacts = new();

    /// <summary>Record an answered turn (both roles), keeping the transcript small.</summary>
    public void RecordTurn(string question, string answer)
    {
        if (!string.IsNullOrWhiteSpace(question)) Turns.Add(("user", question.Trim()));
        if (!string.IsNullOrWhiteSpace(answer)) Turns.Add(("toki", answer.Trim()));
        while (Turns.Count > 20) Turns.RemoveAt(0);
    }
}

public static class QueryUnderstanding
{
    public static QueryPlan Parse(string query, DateTimeOffset now)
    {
        query = (query ?? "").Trim();
        var (start, end, afterDates) = ExtractDateRange(query, now);
        var (rank, rankN, afterRank) = ExtractRank(afterDates);
        var residual = Clean(afterRank);
        var intent = Classify(query, residual, start.HasValue || rank.HasValue);

        return new QueryPlan
        {
            Intent = intent, RangeStart = start, RangeEnd = end,
            Rank = rank, RankN = rankN, Residual = residual, Original = query,
        };
    }

    // ── follow-up resolution (conversational memory) ──
    static readonly Regex BeforeRx = new(@"\b(before that|the (one|week|month) before|(one|week|month) before that|the previous|previous one|earlier|prior)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AfterRx = new(@"\b(after that|the (one|week|month) after|(one|week|month) after that|the next|next one|the following)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AspectTasksRx = new(@"^(and\s+|so\s+|ok\s+)?(what\s+(about|were|are)\s+)?(its?\s+|the\s+|my\s+|their\s+)?(action\s+items?|tasks?|to-?dos?|open\s+items?)\??$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AspectSummaryRx = new(
        @"^(and\s+|so\s+|ok\s+)?(what\s+(was|were)\s+(it|that|those|they)\s+about|what\s+about\s+(the\s+)?(summary|gist|overview|details?|main\s+points?)|(can\s+you\s+)?summ\w*|tell\s+me\s+more|more\s+detail|the\s+(summary|gist|overview)|what\s+happened|recap)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>If the query only makes sense as a follow-up to the previous turn, build a plan from the
    /// conversation state; otherwise null (parse it fresh). Handles "before that / after that", switching
    /// aspect on the same meeting ("what about the action items?", "summarize it"), and bare "this week".</summary>
    public static QueryPlan? ResolveFollowUp(string question, ConversationContext ctx, DateTimeOffset now)
    {
        if (ctx.LastIntent is null) return null;
        var q = (question ?? "").Trim();
        var lower = q.ToLowerInvariant();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Shift the previous DATE RANGE earlier/later ("before that" after "last week").
        if (ctx.LastRangeStart is { } ps && ctx.LastRangeEnd is { } pe)
        {
            var dur = pe - ps; if (dur <= TimeSpan.Zero) dur = TimeSpan.FromDays(7);
            if (words <= 5 && BeforeRx.IsMatch(lower)) return Carry(ctx.LastIntent.Value, ps - dur, ps, q);
            if (words <= 5 && AfterRx.IsMatch(lower)) return Carry(ctx.LastIntent.Value, pe, pe + dur, q);
        }

        // Switch aspect on the SAME meeting(s) from the last turn.
        if (ctx.LastMeetingIds.Count > 0)
        {
            if (AspectTasksRx.IsMatch(lower))
                return new QueryPlan { Intent = QueryIntent.ListTasks, PinnedMeetingIds = new(ctx.LastMeetingIds), Original = q };
            if (AspectSummaryRx.IsMatch(lower))
                return new QueryPlan { Intent = QueryIntent.SummarizeOne, PinnedMeetingIds = new(ctx.LastMeetingIds), Original = q };
            // NB: "this" is deliberately NOT here - it's PROXIMAL (points at the on-screen note), handled by
            // the screen-aware layer. Only anaphora ("it/that/those/them") points back at the last turn.
            if (words <= 8 && StartsQuestion(lower) && Regex.IsMatch(lower, @"\b(it|that|those|them|the meeting)\b", RegexOptions.IgnoreCase))
                return new QueryPlan { Intent = QueryIntent.LookupFact, PinnedMeetingIds = new(ctx.LastMeetingIds), Residual = q, Original = q };
        }

        // Bare temporal continuation ("this week", "in March") → keep the previous intent, new window.
        if (words <= 4)
        {
            var (s, e, residual) = ExtractDateRange(q, now);
            if (s.HasValue && e.HasValue && ResidualIsThin(residual)) return Carry(ctx.LastIntent.Value, s.Value, e.Value, q);
        }
        return null;
    }

    static QueryPlan Carry(QueryIntent intent, DateTimeOffset s, DateTimeOffset e, string q) =>
        new() { Intent = intent, RangeStart = s, RangeEnd = e, Original = q };

    static bool StartsQuestion(string lower) =>
        lower.StartsWith("who") || lower.StartsWith("when") || lower.StartsWith("why")
        || lower.StartsWith("where") || lower.StartsWith("how") || lower.StartsWith("what") || lower.StartsWith("did");

    // ── temporal: absolute + relative dates via Microsoft.Recognizers, past-preferred ──
    static (DateTimeOffset? start, DateTimeOffset? end, string residual) ExtractDateRange(string query, DateTimeOffset now)
    {
        List<ModelResult> results;
        try { results = DateTimeRecognizer.RecognizeDateTime(query, Culture.English, refTime: now.DateTime); }
        catch { return (null, null, query); }

        DateTimeOffset? bestStart = null, bestEnd = null;
        var spansToStrip = new List<(int start, int len)>();

        foreach (var r in results)
        {
            if (r.Resolution is null || !r.Resolution.TryGetValue("values", out var raw)) continue;
            if (raw is not IList<Dictionary<string, string>> values || values.Count == 0) continue;

            (DateTimeOffset s, DateTimeOffset e)? picked = null;
            foreach (var v in values)
            {
                var range = ToRange(v);
                if (range is null) continue;
                // Past-preference: prefer a reading that has already happened (meetings are history).
                if (range.Value.s <= now)
                {
                    if (picked is null || range.Value.s > picked.Value.s) picked = range;
                }
                else picked ??= range;   // only a future reading exists - keep it as a last resort
            }
            if (picked is null) continue;

            // Keep the most specific / most recent past range found in the query.
            if (bestStart is null || picked.Value.s > bestStart)
            {
                bestStart = picked.Value.s;
                bestEnd = picked.Value.e;
            }
            if (r.Start >= 0 && r.End >= r.Start) spansToStrip.Add((r.Start, r.End - r.Start + 1));
        }

        var residual = StripSpans(query, spansToStrip);
        return (bestStart, bestEnd, residual);
    }

    // Turn one Recognizers "value" dict into a [start,end) range (day granularity).
    static (DateTimeOffset s, DateTimeOffset e)? ToRange(Dictionary<string, string> v)
    {
        v.TryGetValue("type", out var type);
        switch (type)
        {
            case "date":
            case "datetime":
                if (v.TryGetValue("value", out var val) && DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    var day = d.Date;
                    return (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)));
                }
                return null;
            case "daterange":
            case "datetimerange":
                if (v.TryGetValue("start", out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ds)
                    && v.TryGetValue("end", out var e) && DateTime.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.None, out var de))
                    return (new DateTimeOffset(ds.Date), new DateTimeOffset(de.Date <= ds.Date ? ds.Date.AddDays(1) : de.Date));
                return null;
            default:
                return null;
        }
    }

    // ── recency rank: "most recent / latest / last meeting / oldest / first meeting" (NOT dates) ──
    // Run AFTER the date parser, so "last week" is already consumed as a range and only "last meeting"
    // remains here.
    static readonly Regex LatestRx = new(
        @"\b(most\s+recent|latest|newest|(?:the\s+)?last|previous|prior|recent)\b(\s+(meeting|call|one|note|recording|conversation|chat))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex OldestRx = new(
        @"\b(oldest|earliest|(?:the\s+)?first)\b(\s+(meeting|call|one|note|recording|conversation))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static (RecencyDir? rank, int n, string residual) ExtractRank(string text)
    {
        // "recent/last/latest" only count as a RANK when clearly about a meeting, or a strong superlative,
        // to avoid stealing words from normal questions.
        var oldest = OldestRx.Match(text);
        if (oldest.Success && (oldest.Groups[2].Success || oldest.Groups[1].Value.ToLowerInvariant() is "oldest" or "earliest"))
            return (RecencyDir.Oldest, 1, text.Remove(oldest.Index, oldest.Length));

        var latest = LatestRx.Match(text);
        if (latest.Success)
        {
            var word = latest.Groups[1].Value.ToLowerInvariant().Replace("the ", "").Trim();
            var hasNoun = latest.Groups[2].Success;
            var strong = word is "most recent" or "latest" or "newest";
            if (hasNoun || strong)
                return (RecencyDir.Latest, 1, text.Remove(latest.Index, latest.Length));
        }
        return (null, 1, text);
    }

    // ── intent: rules over the QUERY text (whole-query shape), fixing the "title word" false-trigger ──
    static readonly Regex CountRx = new(@"\bhow\s+many\b|\bnumber\s+of\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SummarizeRx = new(@"\b(summar(y|ise|ize)|recap|tl;?dr|gist|overview|brief\s+me|what\s+happened\s+(in|at|on|during))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Clear task-REQUESTS only. NOTE: "follow up/follow-up" is deliberately NOT a cue - it lives inside
    // meeting titles and caused the false task-list trigger. Requires an actual task noun.
    static readonly Regex TasksRx = new(
        @"\b(action\s+items?|open\s+items?|to-?dos?|tasks?)\b|\bwhat\s+(do|did)\s+i\s+(need\s+to\s+do|have\s+to\s+do|commit|agree)\b|\bassigned\s+to\s+me\b|\bwhat\s+are\s+my\b.*\b(tasks?|to-?dos?|items?)\b"
        // Deadline questions are task questions: "what's due Friday", "anything due tomorrow",
        // "what's overdue", "am I behind on anything". "due"/"overdue" strongly imply a deadline.
        + @"|\b(over\s?due|past\s+due)\b|\b(what|anything|something|whats?)\b[^?]*\bdue\b|\bdue\s+(today|tomorrow|this|next|by|on|before)\b|\bbehind\s+on\b"
        // Ownership/plate phrasings without a task noun: "what's on my plate", "what does Ana owe me",
        // "what do I still owe", "what am I on the hook for".
        + @"|\bon\s+(my|his|her|their|\w+'s)\s+plate\b|\bowe[sd]?\b|\bon\s+the\s+hook\b"
        // Self-assignment shapes without a task noun ("did I get anything?", "was I assigned?",
        // "anything for me?", "did they mention me?") - these are task questions about the USER and
        // must reach the deterministic self-filtered task route, not the LLM (P9.1).
        + @"|\bdid\s+i\s+get\s+(any(thing)?|some(thing)?|assigned)\b|\bwas\s+i\s+(assigned|given|tasked|mentioned)\b|\b(anything|something)\s+for\s+me\b|\b(give|gave|given)\s+me\s+anything\b|\bmention(ed)?\s+me\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Asking for a LIST of meetings - requires the PLURAL "meetings" (singular "the most recent meeting
    // about…" is a summary of ONE meeting, not a list - that was the over-trigger bug).
    static readonly Regex ListMeetingsRx = new(
        @"\b(what|which|list|show|all\s+(my|the))\b.*\bmeetings\b|\bmy\s+meetings\b|\bmeetings\s+(did\s+i\s+have|this|last|today|yesterday)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The "chrome" of a list request - every word a pure "list my meetings (in <window>)" can be made
    // of. If the query has ANY word outside this set, the user named a TOPIC ("what was discussed
    // about Kai in my meetings?") and wants a LOOKUP about it, not an unfiltered 10-item dump - that
    // was the screenshot bug where a person question was answered with "Your 10 most recent meetings".
    static readonly HashSet<string> ListChrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "what","which","list","show","all","my","our","the","a","an","and","of","in","on","for","me","i","we","you",
        "meetings","meeting","calls","call","notes","recordings","conversations","chats",
        "do","did","does","have","had","has","are","is","was","were","there","any","many","much",
        "give","tell","please","recent","latest","new","newest","old","past","over","recently","far","so",
        "this","that","these","week","month","year","today","yesterday",
        "discussed","discuss","talked","talk","about","happened","happening","going","went","covered","attend","attended",
    };
    static bool OnlyListChrome(string residual)
    {
        foreach (var w in residual.Split(new[] { ' ', '?', '.', ',', '!', ';', ':', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries))
            if (w.Length > 1 && !ListChrome.Contains(w)) return false;
        return true;
    }

    // ── the ANALYTICAL route: questions that need thinking/synthesis, not a lookup ──
    // Fires on explicit reasoning-demand cues ("should I…", "risks", "themes", "compare", "progress",
    // "what do you think"). Kept PHRASE-level where a bare word would over-trigger ("focus on", not
    // "focus" - so "the focus group meeting" stays a lookup/summary). A who/when/where lead is always
    // a FACTUAL question ("who was worried about X" is a lookup, not analysis) - guarded out.
    static readonly Regex AnalyticalRx = new(
        @"\bshould\s+(i|we)\b|\bfocus\s+on\b|\bpriorit(y|ies|i[sz]e[ds]?)\b|\brisks?\b|\bblockers?\b"
        + @"|\bconcerns?\b|\bworr(y|ied|ies|ying)\b|\bthemes?\b|\bpatterns?\b|\btrends?\b|\binsights?\b"
        + @"|\bcompare\b|\bcomparison\b|\bversus\b|\bvs\.?\b|\bdifference\s+between\b"
        + @"|\bprogress(ing|ed)?\b|\bcoming\s+along\b|\bunresolved\b|\bundecided\b|\bopen\s+questions?\b"
        + @"|\bacross\b|\bbiggest\b|\bmost\s+important\b|\boverall\b"
        + @"|\badvice\b|\brecommend\w*\b|\bsuggest\w*\b|\banaly[sz]\w*\b"
        + @"|\bwhat\s+do\s+you\s+think\b|\byour\s+(take|read|thoughts|opinion)\b|\bam\s+i\s+missing\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex FactualLeadRx = new(@"^\s*(who|whom|whose|when|where)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static QueryIntent Classify(string query, string residual, bool hasTemporal)
    {
        if (CountRx.IsMatch(query) && Regex.IsMatch(query, @"\b(meetings?|calls?|syncs?|stand-?ups?|1[:.]1s?|notes?|recordings?|conversations?|chats?)\b", RegexOptions.IgnoreCase))
            return QueryIntent.Count;
        if (TasksRx.IsMatch(query)) return QueryIntent.ListTasks;
        // A list request must be ONLY list-chrome (+ an already-stripped date). Any topical word means
        // the user is asking ABOUT something and "in my meetings" is just scope → LookupFact below.
        if (ListMeetingsRx.IsMatch(query) && !SummarizeRx.IsMatch(query) && OnlyListChrome(residual)) return QueryIntent.ListMeetings;
        // Analysis beats Summarize on purpose: "summarize the themes across my meetings" is a
        // multi-meeting synthesis, not a one-meeting recap. Plain summaries have no analytical cue
        // and are untouched.
        if (AnalyticalRx.IsMatch(query) && !FactualLeadRx.IsMatch(query)) return QueryIntent.Analyze;
        if (SummarizeRx.IsMatch(query)) return QueryIntent.SummarizeOne;

        // A temporal query whose residual is basically empty ("my most recent meeting", "July 30 meeting")
        // is a request for THAT meeting → summarize it.
        if (hasTemporal && ResidualIsThin(residual)) return QueryIntent.SummarizeOne;

        return QueryIntent.LookupFact;   // safe default
    }

    static bool ResidualIsThin(string residual)
    {
        var words = residual.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Filler.Contains(w.ToLowerInvariant())).ToList();
        return words.Count == 0;
    }

    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    { "meeting", "meetings", "call", "note", "notes", "the", "and", "for", "about", "with", "what", "was", "did", "have", "had", "one", "that", "this" };

    static string StripSpans(string text, List<(int start, int len)> spans)
    {
        if (spans.Count == 0) return text;
        var chars = text.ToCharArray();
        foreach (var (s, len) in spans)
            for (var i = s; i < s + len && i < chars.Length && i >= 0; i++) chars[i] = ' ';
        return Clean(new string(chars));
    }

    static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim(' ', ',', '.', '?', '!', ';', ':');
}
