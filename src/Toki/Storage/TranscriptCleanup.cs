using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Re-runs today's transcript repairs over meetings recorded before those repairs existed.
///
/// WHY IT IS A BUTTON AND NOT A STARTUP JOB. Every improvement to turn-merging or name correction
/// makes old transcripts look worse by comparison, and the temptation is to quietly fix them on the
/// next launch. The owner's rule is the opposite: OFFER it, never do it. A transcript is a record of
/// what people said, and silently rewriting a record the user has already read - possibly quoted,
/// possibly acted on - is not a favour. So this runs when someone presses a button, says how many
/// meetings it changed, and stops.
///
/// WHAT IT WILL NOT TOUCH. Summaries, sections and to-dos are left alone even when the transcript
/// underneath them changes. Re-writing them would need the model, would take minutes, and would throw
/// away edits and tick states a person owns. A slightly-stale summary over a cleaner transcript is the
/// right trade; the reverse is not.
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

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done, ids.Count);
            done++;

            MeetingRecord? m = null;
            try { m = store.Get(id); }
            catch (Exception ex) { App.Log($"re-clean: could not read meeting {id}: {ex.Message}"); }
            if (m is null || m.Transcript.Count == 0) continue;

            try
            {
                var (cleaned, fixes) = Clean(m.Transcript, trustedNames);
                before += m.Transcript.Count;
                after += cleaned.Count;
                if (!Differs(m.Transcript, cleaned)) continue;

                store.ReplaceTranscript(id, cleaned, fixes);
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
    {
        // Merge wants the two streams separately - it is what decides which turn a backchannel
        // interrupts - so split the saved transcript back into the streams it came from.
        var you = transcript.Where(s => s.Speaker == Speaker.You).ToList();
        var them = transcript.Where(s => s.Speaker == Speaker.Them).ToList();
        var merged = TranscriptMerger.Merge(you, them);

        var record = new List<TextFix>();

        var trusted = NameLexicon.Trusted(trustedNames);
        var corpus = NameLexicon.Corpus(App.Store);
        var (named, nameFixes) = NameCorrector.Correct(merged, trusted, corpus);
        record.AddRange(nameFixes.Select(f => new TextFix(f.From, f.To, "name")));

        var (repaired, repairs) = SelfCorrection.Clean(named);
        record.AddRange(repairs.Select(r => new TextFix(r.From, r.To, "repair")));

        var distinct = record
            .GroupBy(f => (f.From.ToLowerInvariant(), f.To.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        return (repaired, distinct);
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
