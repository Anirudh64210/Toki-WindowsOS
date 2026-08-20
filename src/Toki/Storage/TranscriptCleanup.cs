using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Re-runs today's transcript repairs over meetings recorded before those repairs existed.
/// Runs only from a button, never at startup: a transcript is a record, and rewriting one the user
/// has read must be offered, not done silently. Summaries, sections and to-dos are never touched -
/// they hold edits and tick states a person owns.
/// </summary>
public static class TranscriptCleanup
{
    public sealed record Result(int MeetingsScanned, int MeetingsChanged, int LinesBefore, int LinesAfter);

    /// <summary>
    /// Re-clean every saved meeting. <paramref name="progress"/> is called with (done, total) so a
    /// long library can show movement; <paramref name="ct"/> stops it between meetings, never mid-write.
    /// </summary>
    public static Result RunAll(MeetingStore store, IEnumerable<string>? trustedNames = null,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var ids = store.List().Where(m => m.Kind == MeetingKind.Meeting).Select(m => m.Id).ToList();
        int changed = 0, before = 0, after = 0, done = 0;

        // Compute the corpus ONCE, before anything is rewritten: recomputing inside the loop lets one
        // meeting's rewrites vote in the next one's corrections and cascade across the library.
        var corpus = NameLexicon.Corpus(store);
        var trusted = NameLexicon.Trusted(trustedNames);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done, ids.Count);
            done++;

            // A meeting being diarised is mid-write on another thread; rewriting rows here would race
            // SetSpeakerTags (addresses lines by position) and stamp names onto the wrong lines.
            if (App.IsResolvingSpeakers(id)) { App.Log($"re-clean: meeting {id} is being diarised, skipped"); continue; }

            MeetingRecord? m = null;
            try { m = store.Get(id); }
            catch (Exception ex) { App.Log($"re-clean: could not read meeting {id}: {ex.Message}"); }
            if (m is null || m.Transcript.Count == 0) continue;

            try
            {
                // A meeting must not vote in its own corrections at corpus weight: one that mis-heard
                // a name six times would outvote a library that knows the right spelling five.
                var own = NameCorrector.CountNames(m.Transcript.Select(s => s.Text));
                var others = Subtract(corpus, own);

                var (cleaned, fixes) = Clean(m.Transcript, trusted, others);
                before += m.Transcript.Count;
                after += cleaned.Count;
                if (!Differs(m.Transcript, cleaned)) continue;

                // The audit trail is append-only: overwriting corrections_json with only this run's
                // fixes would erase every earlier fix and its "Also transcribed as..." hover.
                var merged = m.Corrections.Concat(fixes)
                    .GroupBy(f => (f.From.ToLowerInvariant(), f.To.ToLowerInvariant()))
                    .Select(g => g.First())
                    .ToList();

                store.ReplaceTranscript(id, cleaned, merged);
                MarkdownMirror.Write(store.Get(id) ?? m);   // the .md files mirror the DB, so they follow
                changed++;
            }
            catch (Exception ex) { App.Log($"re-clean: meeting {id} skipped: {ex.Message}"); }
        }

        progress?.Invoke(ids.Count, ids.Count);
        App.Log($"re-clean: {changed} of {ids.Count} meetings changed ({before} lines -> {after})");
        return new Result(ids.Count, changed, before, after);
    }

    /// <summary>
    /// The same repairs a new meeting gets, in the same order: join each person's consecutive lines
    /// into turns, unify mis-heard names against the library, then drop the discarded half of spoken
    /// self-corrections.
    /// </summary>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<TextFix> Fixes) Clean(
        IReadOnlyList<TranscriptSegment> transcript, IEnumerable<string>? trustedNames)
        => Clean(transcript, NameLexicon.Trusted(trustedNames), NameLexicon.Corpus(App.Store));

    static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<TextFix> Fixes) Clean(
        IReadOnlyList<TranscriptSegment> transcript, List<string> trusted, Dictionary<string, int> corpus)
    {
        // Merge wants the two streams separately - it is what decides which turn a backchannel
        // interrupts - so split the saved transcript back into the streams it came from.
        var you = transcript.Where(s => s.Speaker == Speaker.You).ToList();
        var them = transcript.Where(s => s.Speaker == Speaker.Them).ToList();
        var merged = TranscriptMerger.Merge(you, them);

        var record = new List<TextFix>();

        var (named, nameFixes) = NameCorrector.Correct(merged, trusted, corpus);
        record.AddRange(nameFixes.Select(f => new TextFix(f.From, f.To, "name")));

        var (repaired, repairs) = SelfCorrection.Clean(named);
        record.AddRange(repairs.Select(r => new TextFix(r.From, r.To, "repair")));

        var (tidied, tidies) = Readability.Clean(repaired);
        record.AddRange(tidies.Select(td => new TextFix(td.From, td.To, "tidy")));
        repaired = tidied;

        var distinct = record
            .GroupBy(f => (f.From.ToLowerInvariant(), f.To.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        return (repaired, distinct);
    }

    /// <summary>The corpus minus one meeting's own counts - never below zero.</summary>
    static Dictionary<string, int> Subtract(Dictionary<string, int> corpus, Dictionary<string, int> own)
    {
        var result = new Dictionary<string, int>(corpus, StringComparer.OrdinalIgnoreCase);
        foreach (var (word, n) in own)
            if (result.TryGetValue(word, out var c))
            {
                if (c - n <= 0) result.Remove(word);
                else result[word] = c - n;
            }
        return result;
    }

    /// <summary>Is this actually a different transcript? An unchanged meeting must not be rewritten -
    /// a no-op write still churns the file mirror and the search index.</summary>
    static bool Differs(IReadOnlyList<TranscriptSegment> a, IReadOnlyList<TranscriptSegment> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal)) return true;
        return false;
    }
}
