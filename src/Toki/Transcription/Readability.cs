using System.Text.RegularExpressions;

namespace Toki.Transcription;

/// <summary>
/// The READABILITY pass: deterministic post-ASR fixes, all rule-based (no model, no invention).
///   1. Spoken numbers that are DATES, TIMES, YEARS or PERCENTAGES become digits; ordinary
///      counts stay as words ("two questions" is correct English).
///   2. Pure hesitation fillers (uh/um family) are dropped. "Mm-hmm" stays - it is an ANSWER.
///   3. Stuttered function-word doubles collapse; "had had"/"that that" are real English, kept.
/// Number conversions are recorded as fixes for hover; filler/stutter removals are not (nothing
/// was added). Deliberately LAST in the repair chain - cosmetics never feed the riskier passes.
/// </summary>
public static class Readability
{
    public sealed record Tidy(string From, string To);

    // ── vocabulary ──

    const string Units = "one|two|three|four|five|six|seven|eight|nine";
    const string Teens = "ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen";
    const string Tens = "twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety";
    const string OrdUnits = "first|second|third|fourth|fifth|sixth|seventh|eighth|ninth";
    const string OrdTeens = "tenth|eleventh|twelfth|thirteenth|fourteenth|fifteenth|sixteenth|seventeenth|eighteenth|nineteenth";
    const string OrdTens = "twentieth|thirtieth";
    const string Months = "january|february|march|april|may|june|july|august|september|october|november|december";

    // A spoken day-of-month: "twenty second", "twenty-second", "third", "eleventh", "thirtieth".
    const string OrdDay = $"(?:(?:{Tens})[ -](?:{OrdUnits})|{OrdTens}|{OrdTeens}|{OrdUnits})";
    // A spoken cardinal 1-99: "twenty one", "seventeen", "five".
    const string Cardinal99 = $"(?:(?:{Tens})(?:[ -](?:{Units}))?|{Teens}|{Units})";

    static readonly Dictionary<string, int> Value = BuildValues();

    static Dictionary<string, int> BuildValues()
    {
        var v = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var units = Units.Split('|'); var ordUnits = OrdUnits.Split('|');
        for (var i = 0; i < units.Length; i++) { v[units[i]] = i + 1; v[ordUnits[i]] = i + 1; }
        var teens = Teens.Split('|'); var ordTeens = OrdTeens.Split('|');
        for (var i = 0; i < teens.Length; i++) { v[teens[i]] = i + 10; v[ordTeens[i]] = i + 10; }
        var tens = Tens.Split('|');
        for (var i = 0; i < tens.Length; i++) v[tens[i]] = (i + 2) * 10;
        v["twentieth"] = 20; v["thirtieth"] = 30;
        return v;
    }

    /// <summary>"twenty second" → 22; any word combo the vocabularies above cover.</summary>
    static int Number(string phrase)
    {
        var total = 0;
        foreach (var w in phrase.Split(' ', '-'))
            if (Value.TryGetValue(w, out var n)) total += n;
        return total;
    }

    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st", 2 => "nd", 3 => "rd", _ => "th",
    });

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // ── the rules, most specific first ──

    // "august twenty first" / "august twenty-first" / "august twenty one" → "August 21st".
    static readonly Regex MonthDay = new(
        $@"\b(?<m>{Months}) (?<d>{OrdDay}|{Cardinal99})\b(?![-\w])", Opts);

    // "twenty second of july" / "22nd of july" style: "twenty second of july" → "22nd of July".
    static readonly Regex DayOfMonth = new(
        $@"\b(?<d>{OrdDay}) of (?<m>{Months})\b", Opts);

    // A standalone spoken ordinal of 10 or more: "the twenty first" → "the 21st". Below ten the
    // written convention is words ("the third option"), and "second"/"first" alone are ordinary
    // English far more often than they are dates - so small ordinals only convert next to a month.
    static readonly Regex BigOrdinal = new(
        $@"\b(?<d>(?:{Tens})[ -](?:{OrdUnits})|{OrdTens}|{OrdTeens})\b(?![-\w])", Opts);

    // "fifty percent" → "50%"; "a hundred percent" → "100%".
    static readonly Regex Percent = new(
        $@"\b(?<n>{Cardinal99}|a hundred|one hundred) percent\b", Opts);

    // "three thirty pm" → "3:30 pm"; "three pm" → "3 pm"; "eleven o'clock" → "11 o'clock".
    // Dots only in the fully-dotted form: "p\.?m\.?" would swallow the period ending "at 3 pm."
    static readonly Regex ClockTime = new(
        $@"\b(?<h>{Units}|ten|eleven|twelve)(?: (?<mm>fifteen|thirty|forty[ -]?five))? ?(?<suffix>[ap]\.m\.|[ap]m|o'?clock)(?![\w])", Opts);

    // Spoken years: "twenty twenty six" → "2026", "nineteen ninety five" → "1995".
    static readonly Regex SpokenYear = new(
        $@"\b(?<c>nineteen|twenty) (?<t>{Tens})(?:[ -](?<u>{Units}))?\b(?![-\w])", Opts);

    // Hesitation fillers. NOT "hmm"/"mm-hmm" (answers), NOT "like"/"you know" (too often literal).
    static readonly Regex Filler = new(
        @"(?<lead>^|[.!?] |, | )(?:uh|um|uhh|umm|uhm|erm)(?:,? +|(?<tail>[.!?,])|$)", Opts);

    // Stuttered doubles of words that are never legitimately doubled in English.
    static readonly Regex Stutter = new(
        @"\b(?<w>the|a|an|to|of|in|on|and|but|so|if|at|it|is|i|we|you|they|he|she|was|were|my|your|for|with|do|did|can|will|would|new|just|really)\s+\k<w>\b", Opts);

    // ── entry points ──

    /// <summary>Clean a whole transcript. A line the rules would empty is returned unchanged - a
    /// segment that was pure hesitation still marks that somebody made a sound.</summary>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<Tidy> Tidies) Clean(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var all = new List<Tidy>();
        var output = new List<TranscriptSegment>(segments.Count);
        foreach (var s in segments)
        {
            var (text, tidies) = Clean(s.Text);
            // A line reduced to punctuation ("Um." → ".") was pure hesitation: keep the original.
            if (!text.Any(char.IsLetterOrDigit)) { output.Add(s); continue; }
            all.AddRange(tidies);
            output.Add(text == s.Text ? s : s with { Text = text });
        }
        return (output, all);
    }

    /// <summary>Clean one line.</summary>
    public static (string Text, IReadOnlyList<Tidy> Tidies) Clean(string? text)
    {
        var tidies = new List<Tidy>();
        if (string.IsNullOrWhiteSpace(text)) return (text ?? "", tidies);
        var t = text;

        string Record(Match m, string to)
        {
            if (!string.Equals(m.Value, to, StringComparison.Ordinal)) tidies.Add(new Tidy(m.Value, to));
            return to;
        }

        // Years FIRST: "march twenty twenty seven" must become "march 2027", and if the month
        // rule ran first it would grab "march twenty" as March 20 and strand "twenty seven".
        // ("august twenty first" is safe here: "first" is not a tens word, so the year pattern
        // cannot match it.)
        t = SpokenYear.Replace(t, m =>
        {
            var century = m.Groups["c"].Value.Equals("nineteen", StringComparison.OrdinalIgnoreCase) ? 1900 : 2000;
            var year = century + Number(m.Groups["t"].Value) + (m.Groups["u"].Success ? Number(m.Groups["u"].Value) : 0);
            return Record(m, year.ToString());
        });

        // Dates before bare ordinals, so "august twenty first" is consumed as one date and the
        // ordinal rule never sees its tail.
        t = MonthDay.Replace(t, m =>
        {
            var d = Number(m.Groups["d"].Value);
            if (d is < 1 or > 31) return m.Value;
            // "may" is also a verb: "you may second that motion" must not become "you May 2nd
            // that motion". A lowercase "may" with a small single-word day only counts when the
            // recogniser itself capitalised May.
            if (m.Groups["m"].Value.Equals("may", StringComparison.Ordinal) && d < 10 && !m.Groups["d"].Value.Contains(' '))
                return m.Value;
            var ordinal = Regex.IsMatch(m.Groups["d"].Value, $@"(?:{OrdUnits}|{OrdTens}|{OrdTeens})$", Opts);
            return Record(m, $"{Cap(m.Groups["m"].Value)} {(ordinal ? Ordinal(d) : d.ToString())}");
        });
        t = DayOfMonth.Replace(t, m =>
        {
            var d = Number(m.Groups["d"].Value);
            return d is < 1 or > 31 ? m.Value : Record(m, $"{Ordinal(d)} of {Cap(m.Groups["m"].Value)}");
        });
        t = BigOrdinal.Replace(t, m => Record(m, Ordinal(Number(m.Groups["d"].Value))));
        // A month that now sits beside digits is being used as a DATE - give it its capital
        // ("march 2027" → "March 2027"). Months in running prose are left exactly as heard.
        t = Regex.Replace(t, $@"\b(?<m>{Months})(?= \d)", m => Cap(m.Groups["m"].Value), Opts);
        t = Percent.Replace(t, m =>
        {
            var raw = m.Groups["n"].Value;
            var n = raw.Contains("hundred", StringComparison.OrdinalIgnoreCase) ? 100 : Number(raw);
            return Record(m, $"{n}%");
        });
        t = ClockTime.Replace(t, m =>
        {
            var h = Number(m.Groups["h"].Value);
            var mm = m.Groups["mm"].Success ? $":{Number(m.Groups["mm"].Value):00}" : "";
            // The am/pm marker keeps the style it was heard in - "a.m." stays dotted.
            var suffix = m.Groups["suffix"].Value.ToLowerInvariant();
            return Record(m, $"{h}{mm} {suffix}");
        });

        // Fillers. When one opened a sentence the next word inherits the capital, so "Uh hi
        // sorry." becomes "Hi sorry." and not "hi sorry." The re-capitalize pass runs ONLY when a
        // sentence-opening filler was actually removed - blanket sentence-capitalising would
        // damage words that legitimately start lowercase.
        var capNeeded = false;
        t = Filler.Replace(t, m =>
        {
            var lead = m.Groups["lead"].Value;
            if (lead is "" || lead.EndsWith(". ") || lead.EndsWith("! ") || lead.EndsWith("? ")) capNeeded = true;
            var tail = m.Groups["tail"].Success ? m.Groups["tail"].Value : "";
            return lead + tail;
        });
        if (capNeeded)
            t = Regex.Replace(t, @"(^|[.!?] )(?<c>[a-z])", m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups["c"].Value[0]));

        // Stutters, twice so "the the the" fully collapses.
        t = Stutter.Replace(t, m => m.Groups["w"].Value);
        t = Stutter.Replace(t, m => m.Groups["w"].Value);

        // Tidy the seams the removals leave.
        t = Regex.Replace(t, @"  +", " ");
        t = Regex.Replace(t, @" ([,.!?;:])", "$1");
        t = Regex.Replace(t, @"([,.!?;:])\1+", "$1");
        t = t.Trim();

        return (t, tidies);
    }
}
