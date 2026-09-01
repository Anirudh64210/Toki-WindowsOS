using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Toki.Storage;

namespace Toki.Llm;

/// <summary>
/// The LAST pass over a WRITTEN note: makes every bullet read like a note instead of like reported
/// speech. Deterministic and rule-based on purpose - a model rewrite can invent, and this runs on
/// both the model note and the heuristic floor so the two cannot drift apart.
///
/// It never touches the transcript, and it only ever REMOVES words: a bullet that resists every
/// rule is left exactly as written rather than truncated into nonsense.
/// </summary>
public static class Brevity
{
    // Length ceilings, in words. A bullet is one line on a card, so past these it wraps and stops
    // being glanceable - which is the whole job of a note. TIGHTENED 2026-08-25 (owner chose the
    // "Tight", Granola/Apple-glanceable style): a bullet was 22 words - a full wrapping line - and an
    // action 14. These are BACKSTOPS that only cut at clause boundaries; the prompt asks for shorter
    // still, and this stops a rambling line from wrapping into a paragraph.
    const int ActionWords = 11;
    const int BulletWords = 15;
    // The summary was the ONE block the concise pass never touched, so it stayed a 3-4 sentence
    // corporate paragraph. Cap it to a glanceable lead: hedges out, first N sentences only.
    const int SummarySentences = 2;

    /// <summary>Tighten every written line in a note, in place, and return it.</summary>
    public static NoteSet Tighten(NoteSet note)
    {
        if (note is null) return note!;
        note.Tldr = TightenSummary(note.Tldr);
        foreach (var s in note.Sections) s.Bullets = Tighten(s.Bullets, BulletWords);
        note.Decisions = Tighten(note.Decisions, BulletWords);
        note.Questions = Tighten(note.Questions, BulletWords);
        foreach (var a in note.Actions) a.Text = TightenAction(a.Text, a.Owner);
        return note;
    }

    /// <summary>The summary: strip the spoken hedges, then keep the first couple of sentences - a lead,
    /// not a transcript retelling. It KEEPS the subject (never strips a reported-speech frame the way a
    /// bullet does, since "The team decided ..." is the right shape for a summary), and an empty summary
    /// is left empty for the renderer to drop. It only ever removes words - never rephrases.</summary>
    public static string TightenSummary(string raw, int maxSentences = SummarySentences)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        t = StripHedges(t);
        t = Squeeze(t);
        var sentences = Regex.Split(t, @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0).ToArray();
        if (sentences.Length > maxSentences) t = string.Join(" ", sentences.Take(maxSentences)).Trim();
        return Capitalise(t);
    }

    static List<string> Tighten(List<string> lines, int cap) =>
        lines.Select(l => Line(l, cap)).Where(l => l.Length > 0).ToList();

    /// <summary>One bullet: drop the reported-speech frame, drop hedges, then cap the length.</summary>
    public static string Line(string raw, int cap)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        t = StripFrame(t);
        t = StripHedges(t);
        t = Squeeze(t);
        t = Cap(t, cap);
        return Capitalise(t);
    }

    /// <summary>An action item: the frame usually names the owner, which the note already carries in
    /// its own field, so "Priya will send the deck" becomes "Send the deck" rather than repeating a
    /// name the row prints beside it.</summary>
    public static string TightenAction(string raw, string? owner)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        t = StripFrame(t);

        // "<Owner> will/should/needs to/is going to <verb> ..." -> "<verb> ...", but ONLY when the
        // name matches the owner the row already shows. A different name is content, not a frame.
        var m = Regex.Match(t, @"^(?<who>[A-Z][\w'-]*(?:\s+[A-Z][\w'-]*)?)\s+(?:will|shall|should|needs?\s+to|has\s+to|(?:is|are)\s+going\s+to|to)\s+(?<rest>.+)$");
        if (m.Success && SameOwner(m.Groups["who"].Value, owner))
            t = m.Groups["rest"].Value;

        t = StripHedges(t);
        t = Squeeze(t);
        t = Cap(t, ActionWords);
        return Capitalise(t);
    }

    static bool SameOwner(string who, string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        if (string.Equals(who, owner, StringComparison.OrdinalIgnoreCase)) return true;
        // "You" covers a first-person commitment; a first name matches its full name.
        var first = owner.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return string.Equals(who, first, StringComparison.OrdinalIgnoreCase);
    }

    // ── the frames ──
    // Reported speech is the single biggest source of length: the note is ALREADY a record of what
    // was said, so "X mentioned that" is a whole clause that carries nothing the note does not.
    static readonly Regex Frame = new(
        @"^(?:" +
        @"(?:[A-Z][\w'-]*(?:\s+[A-Z][\w'-]*)?|they|the\s+team|we|everyone|the\s+group)\s+" +
        @"(?:said|mentioned|noted|stated|explained|pointed\s+out|highlighted|raised|brought\s+up|" +
        @"suggested|proposed|confirmed|clarified|added|shared|reported|flagged|observed|commented)\s+" +
        @"(?:that\s+)?" +
        @"|it\s+was\s+(?:said|noted|mentioned|decided|agreed|discussed|confirmed|clarified)\s+(?:that\s+)?" +
        @"|there\s+was\s+(?:a\s+)?(?:discussion|conversation|debate)\s+(?:about|around|on|regarding)\s+" +
        @"|the\s+(?:team|group)\s+(?:discussed|talked\s+about|went\s+over|covered)\s+" +
        @"|(?:it\s+was\s+)?agreed\s+that\s+" +
        @"|the\s+(?:main\s+)?point\s+(?:made\s+)?was\s+(?:that\s+)?" +
        @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string StripFrame(string t)
    {
        // Once only. Stripping repeatedly would eat real content in a sentence that legitimately
        // begins with one of these words.
        var m = Frame.Match(t);
        return m.Success && m.Length < t.Length - 8 ? t[m.Length..].TrimStart() : t;
    }

    // ── the hedges ──
    // Spoken padding. Every one of these is removable without changing what the line asserts; a
    // hedge that DOES change the claim ("might", "unless", "unclear") is deliberately absent.
    static readonly (Regex Rx, string To)[] Hedges =
    {
        (new(@"\b(?:i\s+think|i\s+guess|i\s+mean|you\s+know|i\s+feel\s+like|to\s+be\s+honest|" +
             @"at\s+the\s+end\s+of\s+the\s+day|as\s+far\s+as\s+i\s+know)\b[,\s]*", RegexOptions.IgnoreCase), ""),
        (new(@"\b(?:basically|essentially|literally|actually|really|just|simply|obviously|clearly|" +
             @"definitely|certainly|totally|honestly|kind\s+of|sort\s+of|a\s+bit\s+of|pretty\s+much|" +
             @"sort\s+of\s+like|type\s+of)\b\s*", RegexOptions.IgnoreCase), ""),
        (new(@"\bin\s+order\s+to\b", RegexOptions.IgnoreCase), "to"),
        (new(@"\b(?:at\s+this\s+point\s+in\s+time|at\s+the\s+moment|right\s+now)\b", RegexOptions.IgnoreCase), "now"),
        (new(@"\b(?:due\s+to\s+the\s+fact\s+that|owing\s+to\s+the\s+fact\s+that)\b", RegexOptions.IgnoreCase), "because"),
        (new(@"\b(?:is|are|am|was|were)\s+going\s+to\b", RegexOptions.IgnoreCase), "will"),
        (new(@"\b(?:make\s+sure\s+to|be\s+sure\s+to|go\s+ahead\s+and)\b\s*", RegexOptions.IgnoreCase), ""),
        (new(@"\bwe\s+(?:need|have)\s+to\s+make\s+a\s+decision\s+(?:about|on)\b", RegexOptions.IgnoreCase), "decide"),
    };

    static string StripHedges(string t)
    {
        foreach (var (rx, to) in Hedges) t = rx.Replace(t, to);
        return t;
    }

    static string Squeeze(string t) =>
        Regex.Replace(t, @"\s{2,}", " ").Replace(" ,", ",").Replace(" .", ".").Trim(' ', ',', ';', '-');

    /// <summary>
    /// Cap the length WITHOUT truncating mid-thought: keep whole sentences while they fit, and if
    /// even the first sentence is over, cut at the last clause boundary before the cap. A line that
    /// offers no clean cut is returned whole - a readable long bullet beats a mangled short one.
    /// </summary>
    static string Cap(string t, int cap)
    {
        if (WordCount(t) <= cap) return t;

        var sentences = Regex.Split(t, @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToArray();
        if (sentences.Length > 1)
        {
            var kept = new List<string>();
            var n = 0;
            foreach (var s in sentences)
            {
                var w = WordCount(s);
                if (kept.Count > 0 && n + w > cap) break;
                kept.Add(s); n += w;
            }
            var joined = string.Join(" ", kept);
            if (WordCount(joined) <= cap) return joined;
            t = joined;
        }

        // Still long: cut at the last clause boundary that fits.
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= cap) return t;
        var head = string.Join(" ", words.Take(cap));
        var cut = head.LastIndexOfAny(new[] { ',', ';', ':' });
        if (cut > head.Length / 2) return head[..cut].TrimEnd();

        // No clean boundary: leave it alone rather than clipping a sentence in half.
        return t;
    }

    static int WordCount(string t) => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    static string Capitalise(string t) =>
        t.Length == 0 ? t : char.IsLower(t[0]) ? char.ToUpper(t[0]) + t[1..] : t;
}
