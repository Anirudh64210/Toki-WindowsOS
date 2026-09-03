using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Llm;

/// <summary>
/// Does the note say things the meeting actually said?
///
/// Toki had no way to tell whether a note got better, which made every prompt change a matter of
/// taste. This is the cheap half of the answer: HARD ENTITIES - names, numbers, dates, times, money -
/// checked by string matching against the transcript, with no model in the loop at all.
///
/// Why hard entities and not a similarity score: a wrong number or a person who was not in the room
/// is the failure a user actually notices and never forgives ("I see my name tasked with things I
/// never committed to"). It is also the one class a machine can check exactly. Fuzzy "is this bullet
/// supported" needs entailment, which is a separate and more expensive question.
///
/// PRECISION ALONE IS GAMEABLE by writing less, so recall ships beside it. Without that, every
/// "improvement" measured here would just be the model getting terser.
/// </summary>
public static class Grounding
{
    /// <summary>Words that look like proper nouns because they open a sentence, not because they name
    /// anything. Checking these against the transcript produces false alarms, so they never count.</summary>
    static readonly HashSet<string> SentenceOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "This", "That", "These", "Those", "A", "An", "And", "But", "If", "It", "We", "They",
        "He", "She", "You", "I", "There", "Then", "When", "While", "After", "Before", "During",
        "No", "Not", "All", "Any", "Some", "Each", "Every", "Both", "Team", "Next", "Still", "Open",
    };

    /// <summary>Entity kinds, kept apart because they fail differently: a wrong NUMBER is a hard
    /// error, a wrong NAME is a trust error, and they deserve separate counts.</summary>
    public enum Kind { Name, Number, Date, Money }

    public readonly record struct Entity(string Text, Kind Kind);

    /// <summary>One note's grounding report.</summary>
    public sealed class Report
    {
        public long MeetingId { get; init; }
        public string Title { get; init; } = "";
        public List<Entity> Unsupported { get; init; } = [];
        public int Checked { get; init; }
        public int TranscriptEntities { get; init; }
        public int Recalled { get; init; }

        /// <summary>Share of the note's hard entities that appear in the transcript. 1.0 = nothing invented.</summary>
        public double Precision => Checked == 0 ? 1.0 : (Checked - Unsupported.Count) / (double)Checked;
        /// <summary>Share of the transcript's hard entities the note kept. Guards against terseness.</summary>
        public double Recall => TranscriptEntities == 0 ? 1.0 : Recalled / (double)TranscriptEntities;
    }

    // ── extraction ──

    static readonly Regex NumberRx = new(@"\b\d[\d,]*(?:\.\d+)?%?\b", RegexOptions.Compiled);
    static readonly Regex MoneyRx = new(@"[$£€]\s?\d[\d,]*(?:\.\d+)?\b", RegexOptions.Compiled);
    static readonly Regex TimeRx = new(@"\b\d{1,2}(?::\d{2})?\s?(?:am|pm)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex NameRx = new(@"\b[A-Z][a-z]{2,}\b", RegexOptions.Compiled);

    static readonly string[] DayMonth =
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "january", "february", "march", "april", "may", "june", "july", "august",
        "september", "october", "november", "december", "tomorrow", "tonight",
    };

    /// <summary>Every hard entity in a piece of text, de-duplicated case-insensitively.</summary>
    public static List<Entity> Extract(string? text)
    {
        var found = new List<Entity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return found;

        void Add(string raw, Kind k)
        {
            var t = raw.Trim().Trim('.', ',', ';', ':');
            if (t.Length == 0 || !seen.Add(t)) return;
            found.Add(new Entity(t, k));
        }

        // A model that numbers its own bullets ("1. Ship Friday") is not claiming the meeting said
        // "1". Strip the marker before anything is counted.
        text = string.Join("\n", text.Split('\n').Select(l => ListMarkerRx.Replace(l, "")));

        foreach (Match m in MoneyRx.Matches(text)) Add(m.Value, Kind.Money);
        foreach (Match m in TimeRx.Matches(text)) Add(m.Value, Kind.Date);
        foreach (Match m in NumberRx.Matches(text)) Add(m.Value, Kind.Number);
        // Capitalised, because lowercase "may" and "march" are ordinary verbs and matching them
        // made every note claim a date it never stated.
        foreach (var d in DayMonth)
            if (Regex.IsMatch(text, @"\b" + char.ToUpperInvariant(d[0]) + d[1..] + @"\b")) Add(d, Kind.Date);

        // A PROPER NOUN IS CAPITALISED MID-SENTENCE. A capital at the start of a line, a bullet or a
        // sentence carries no information about whether the word names anything, and counting it made
        // the instrument scream "Identified", "Agreed" and "Rotate" at every note in the Library. The
        // first run of this check reported 67% precision and almost all of the misses were this.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart();
            foreach (Match m in NameRx.Matches(line))
            {
                if (LineInitial(line, m.Index)) continue;
                if (SentenceOpeners.Contains(m.Value)) continue;
                if (DayMonth.Contains(m.Value.ToLowerInvariant())) continue;   // already a date
                Add(m.Value, Kind.Name);
            }
        }
        return found;
    }

    /// <summary>Is this match the first word of the line, of a bullet, or of a sentence? Those
    /// positions capitalise every word regardless of what it is.</summary>
    static bool LineInitial(string line, int idx)
    {
        var before = line[..idx].TrimEnd();
        if (before.Length == 0) return true;
        var last = before[^1];
        if (last is '.' or '!' or '?' or ':' or '-' or '*' or ')' or ']') return true;
        return false;
    }

    static readonly string[] Ones =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen",
    };
    static readonly string[] Tens =
    {
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    };

    /// <summary>How a number would be SPOKEN. People say "seventy percent" and Toki writes "70%", so a
    /// digits-only comparison calls a perfectly grounded number invented. Readability converts spoken
    /// numbers to digits for dates, times and percentages but not everywhere, so the check has to look
    /// for both forms or the instrument manufactures its own failures.</summary>
    static IEnumerable<string> SpokenForms(string digits)
    {
        if (!int.TryParse(digits, out var n) || n < 0) yield break;
        if (n < Ones.Length) { yield return Ones[n]; yield break; }
        if (n < 100)
        {
            var t = Tens[n / 10];
            yield return n % 10 == 0 ? t : t + " " + Ones[n % 10];
            yield return n % 10 == 0 ? t : t + "-" + Ones[n % 10];
            yield break;
        }
        if (n % 1000 == 0 && n < 1_000_000) { yield return (n / 1000) + " thousand"; yield break; }
        if (n % 100 == 0 && n < 1000) { yield return Ones[n / 100] + " hundred"; }
    }

    /// <summary>Does this text carry person names at all, anywhere?
    ///
    /// Deliberately NOT <see cref="Extract"/>: that suppresses line-initial capitals, which is right
    /// when reading a NOTE (a capital after a bullet is grammar, not a name) and wrong when reading a
    /// TRANSCRIPT, where "Priya finished the migration" opens the line and Priya is still a person.
    /// Asking the two sides the same question with the same rule was the bug: the gate concluded a
    /// transcript full of names carried none, and pruned nothing.</summary>
    static bool CarriesNames(string text) =>
        NameRx.Matches(text).Any(m => !SentenceOpeners.Contains(m.Value)
                                   && !DayMonth.Contains(m.Value.ToLowerInvariant()));

    /// <summary>Is this entity present in the transcript? Numbers and money compare on digits, so
    /// "10,000" in the note matches "10000" in the transcript, and fall back to the spoken form.
    /// Names compare whole-word.</summary>
    static bool Present(Entity e, string haystack, string digitsOnly)
    {
        if (e.Kind is Kind.Number or Kind.Money)
        {
            var d = new string(e.Text.Where(char.IsDigit).ToArray());
            if (d.Length == 0) return false;
            if (digitsOnly.Contains(d, StringComparison.Ordinal)) return true;
            return SpokenForms(d).Any(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        return Regex.IsMatch(haystack, @"\b" + Regex.Escape(e.Text) + @"\b", RegexOptions.IgnoreCase);
    }

    /// <summary>Strip a leading list marker ("1.", "2)", "-", "*") from a line. A model that numbers
    /// its own bullets is not claiming the meeting mentioned the number 2.</summary>
    static readonly Regex ListMarkerRx = new(@"^\s*(?:\d{1,2}[.)]|[-*•])\s+", RegexOptions.Compiled);

    /// <summary>
    /// Drop any written line that names a PERSON the meeting never mentioned.
    ///
    /// This is the failure users never forgive - "I see my name tasked with things I never committed
    /// to" is the single hottest complaint about every tool in this category, and a note that invents
    /// a colleague is not slightly wrong, it is untrustworthy in a way that poisons the true lines
    /// beside it. Toki already holds the same rule for evidence links: wrong is worse than absent.
    ///
    /// Deliberately NAMES ONLY. Numbers and dates get paraphrased legitimately ("about a hundred",
    /// "early next week") and pruning them would delete good content to satisfy a string match. A
    /// person either was in the meeting or was not.
    ///
    /// Runs on the model's own output only. The carry-forward section is grounded in other meetings
    /// and is never touched.
    /// </summary>
    public static int PruneUngroundedNames(NoteSet notes, IReadOnlyList<TranscriptSegment> transcript)
    {
        var spokenNames = transcript.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t));
        var source = string.Join("\n", transcript.Select(t => t.Text).Concat(spokenNames!));
        var digits = new string(source.Where(char.IsDigit).ToArray());

        // ONLY PRUNE WHEN THE TRANSCRIPT PROVES IT CARRIES NAMES AT ALL. If it contains none, there
        // is no way to tell an invented person from one the recogniser simply never caught, and
        // deleting on that basis would throw away real tasks - the exact bug
        // Real_tasks_are_never_dropped_by_the_name_filter exists to prevent. Same shape as Toki's
        // evidence rule: below the floor, say nothing rather than guess.
        if (!CarriesNames(source)) return 0;

        bool Ungrounded(string? line) =>
            !string.IsNullOrWhiteSpace(line)
            && Extract(line).Any(e => e.Kind == Kind.Name && !Present(e, source, digits));

        var dropped = 0;
        foreach (var sec in notes.Sections)
        {
            if (sec.Heading.StartsWith(OpenFromLastTime.Heading, StringComparison.OrdinalIgnoreCase)) continue;
            dropped += sec.Bullets.RemoveAll(b => Ungrounded(b));
        }
        notes.Sections.RemoveAll(s => s.Bullets.Count == 0
            && !s.Heading.StartsWith(OpenFromLastTime.Heading, StringComparison.OrdinalIgnoreCase));

        dropped += notes.Actions.RemoveAll(a => Ungrounded(a.Text));
        dropped += notes.Decisions.RemoveAll(d => Ungrounded(d));
        dropped += notes.Questions.RemoveAll(q => Ungrounded(q));

        // The summary is a single sentence carrying the whole note's headline, so losing it to one
        // bad name costs more than it saves. Flagged, not deleted.
        if (Ungrounded(notes.Tldr)) App.Log("notes: summary names someone absent from the transcript");
        return dropped;
    }

    /// <summary>Score one note against the transcript it was written from.</summary>
    public static Report Score(NoteSet notes, IReadOnlyList<TranscriptSegment> transcript,
        long meetingId = 0, string title = "")
    {
        // SPEAKER TAGS ARE PART OF THE SOURCE. Diarisation names a voice ("Craig"), the writer sees
        // that label on every line it reads, and the note quite properly says "Craig" - but the word
        // may never occur in anything anyone SAID. Scoring against spoken text alone made every
        // correctly-attributed note look like it invented a person, which is the single failure this
        // instrument exists to catch, so a false alarm there is the most expensive kind.
        var spokenNames = transcript.Select(t => t.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct();
        var source = string.Join("\n", transcript.Select(t => t.Text).Concat(spokenNames!));
        var digits = new string(source.Where(char.IsDigit).ToArray());

        // The TRANSCRIPT is the only reference. The note's own metadata block (dates Toki knows from
        // the clock, not from speech) is deliberately not scored - it is observed, not claimed.
        //
        // AND NOT THE CARRY-FORWARD. "Still open from last time" is built deterministically from
        // OTHER meetings' action items, so its names and dates are grounded in a transcript this
        // scorer is not holding. Counting it reported Craig, Surabi and Kirtana as invented people in
        // meeting 98 when every one of them was a correctly carried-over task. A precision metric
        // that cries wolf on the product's most trustworthy section is worse than no metric.
        var noteText = string.Join("\n",
            new[] { notes.Tldr }
                .Concat(notes.EffectiveSections()
                    .Where(s => !s.Heading.StartsWith(OpenFromLastTime.Heading, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(s => s.Bullets))
                .Concat(notes.Actions.Select(a => a.Text))
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var claimed = Extract(noteText);
        var unsupported = claimed.Where(e => !Present(e, source, digits)).ToList();

        // Recall counts only entities the transcript states more than once: a name said once in
        // passing is not something a good note is obliged to carry, and counting it would push the
        // writer toward transcription rather than judgement.
        var srcEntities = Extract(source)
            .Where(e => Regex.Matches(source, Regex.Escape(e.Text), RegexOptions.IgnoreCase).Count >= 2)
            .ToList();
        var kept = srcEntities.Count(e => Present(e, noteText, new string(noteText.Where(char.IsDigit).ToArray())));

        return new Report
        {
            MeetingId = meetingId,
            Title = title,
            Checked = claimed.Count,
            Unsupported = unsupported,
            TranscriptEntities = srcEntities.Count,
            Recalled = kept,
        };
    }
}
