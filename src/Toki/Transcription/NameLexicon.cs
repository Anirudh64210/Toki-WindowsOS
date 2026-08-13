using Toki.Storage;

namespace Toki.Transcription;

/// <summary>
/// Who Toki knows about, and how sure it is.
///
/// Two tiers, and the difference between them is who did the typing.
///
///   TRUSTED  the user's own name from Settings, a calendar attendee's display name, or a term the user
///            pinned. A human typed these, so they are simply correct, and a correction TOWARDS one of
///            them is allowed to reach further (see <see cref="NameCorrector"/>).
///
///   CORPUS   how often each spelling appears across the whole library. Nobody vouched for these, so
///            they only get a vote - but the vote is decisive where a single meeting cannot be. The
///            case that drove this: one recording contained "Craig" (from the mic) and "Greg" (from the
///            loopback), once each, which is unresolvable; across the library Craig appears many times
///            and Greg once, which is not.
///
/// Notes and titles are counted alongside transcripts on purpose: a name the ASR mangled in one
/// recording is often spelled correctly in another meeting's summary.
/// </summary>
public static class NameLexicon
{
    /// <summary>Names a human supplied. Order does not matter; all are equally trusted.</summary>
    public static List<string> Trusted(IEnumerable<string>? attendees = null)
    {
        var names = new List<string>();
        try
        {
            var settings = Llm.AppSettings.Current;
            if (!string.IsNullOrWhiteSpace(settings.UserName)) names.Add(settings.UserName!);
            if (!string.IsNullOrWhiteSpace(settings.PinnedNames))
                names.AddRange(settings.PinnedNames!.Split(
                    new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) { App.Log($"name lexicon settings: {ex.Message}"); }

        if (attendees is not null) names.AddRange(attendees);
        return names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The library's vote on how each name is spelled.</summary>
    public static Dictionary<string, int> Corpus(MeetingStore store)
    {
        try
        {
            var lines = store.TranscriptLines();
            lines.AddRange(store.NoteText());
            // Action-item owners are worth more than a passing mention: the note writer already judged
            // them to be a person. Count each several times so they outweigh a one-off mis-hearing.
            foreach (var owner in store.ActionOwners())
                for (var i = 0; i < 3; i++) lines.Add($"and {owner} agreed");
            return NameCorrector.CountNames(lines);
        }
        catch (Exception ex)
        {
            App.Log($"name lexicon corpus: {ex.Message}");
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
