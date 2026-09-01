using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Dev;

/// <summary>
/// --evidencetest [meetingId]: does the audio behind an evidence link actually SAY the line the
/// link points at?
///
/// The owner reported that the clip beside a to-do plays the wrong moment. There are three
/// candidate causes and reasoning cannot separate them: the LINK could point at the wrong
/// transcript line, the TIMESTAMPS on that line could be wrong, or the clip WINDOW around those
/// timestamps could be cut badly. This measures it end to end: cut the clip the app would play,
/// run it back through the real recogniser, and compare what came out with the line the link
/// claims. High overlap means the timing is sound and any wrongness is in the match; low overlap
/// on a line that reads fine means the times are wrong.
///
/// READ-ONLY. It never writes to the store and never touches the recordings.
/// </summary>
static class EvidenceTest
{
    public static int Run(string[] args)
    {
        App.Headless = true;
        var i = Array.IndexOf(args, "--evidencetest");
        long? id = i >= 0 && i + 1 < args.Length && long.TryParse(args[i + 1], out var n) ? n : null;

        var store = App.Store;
        var meeting = id is { } mid ? store.Get(mid) : LatestWithAudio(store);
        if (meeting is null) return Fail("no meeting with a transcript and kept audio was found");

        Console.WriteLine($"[--evidencetest] meeting {meeting.Id}: \"{meeting.Title}\"");
        Console.WriteLine($"  {meeting.Transcript.Count} transcript lines, {meeting.Notes.Actions.Count} action item(s)");

        // Mirror what the note renderer plays: build every note line in the SAME order (all section
        // bullets, then all actions) and resolve the DISJOINT ranges, so this measures the clips the
        // app actually cuts, overlaps and all.
        var lines = new List<(string Kind, string Text, SourceSpan? Source)>();
        foreach (var s in meeting.Notes.EffectiveSections())
        {
            var bi = 0;
            foreach (var b in s.Bullets) lines.Add((s.Heading.ToLowerInvariant(), b, s.SourceFor(bi++)));
        }
        foreach (var a in meeting.Notes.Actions) lines.Add(("todo", a.Text, a.Source));
        if (lines.Count == 0) return Fail("this meeting's note has no bullets or to-dos to check");

        if (!ModelManager.IsInstalled(ModelManager.Parakeet))
            return Fail("Parakeet is not installed - run --asrtest once to fetch it");

        var checkedCount = 0;
        var linked = 0;
        var good = 0;
        var scores = new List<double>();
        // Every clip window actually cut, per speaker, to check that no two share audio.
        var clips = new List<(Speaker Speaker, int StartMs, int EndMs, string Kind)>();

        using var asr = new Transcriber();
        for (var li = 0; li < lines.Count; li++)
        {
            var (kind, text, source) = lines[li];
            checkedCount++;
            // Each point resolves on its OWN (no cross-point disjoint pass), the way the note renderer
            // now does it - the point's text then narrows the clip to its phrase.
            var range = Evidence.BestRange(text, meeting.Transcript, source);
            if (range is null)
            {
                Console.WriteLine($"  [{kind}] no link      «{Short(text)}»");
                continue;
            }
            linked++;

            var (first, last) = range.Value;
            var span = meeting.Transcript.Skip(first).Take(last - first + 1).ToList();
            var speaker = span[0].Speaker;
            var wav = EvidenceAudio.WavFor(meeting, speaker);
            if (wav is null || !File.Exists(wav))
            {
                Console.WriteLine($"  [{kind}] no audio kept for this meeting - stopping");
                break;
            }

            var (startMs, endMs) = EvidenceAudio.Window(span, meeting.Transcript, text);
            clips.Add((speaker, startMs, endMs, kind));
            var clip = EvidenceAudio.Clip(wav, startMs, endMs);
            if (clip is null) { Console.WriteLine($"  [{kind}] clip failed at {startMs}ms"); continue; }

            var tmp = Path.Combine(Path.GetTempPath(), $"toki-evidence-{Guid.NewGuid():N}.wav");
            string heard;
            try
            {
                File.WriteAllBytes(tmp, clip);
                var samples = AudioLoader.To16kMono(tmp);
                heard = string.Join(" ", asr.Transcribe(samples, speaker).Select(s => s.Text));
            }
            catch (Exception ex) { Console.WriteLine($"  [{kind}] clip decode failed: {ex.Message}"); continue; }
            finally { try { File.Delete(tmp); } catch { } }

            var claimed = string.Join(" ", span.Select(s => s.Text));
            var score = Overlap(claimed, heard);
            scores.Add(score);
            var ok = score >= 0.5;
            if (ok) good++;

            Console.WriteLine($"  [{kind}] line {first}{(last != first ? $"-{last}" : "")} " +
                              $"{startMs / 1000.0:0.0}s-{endMs / 1000.0:0.0}s  match {score:P0}  {(ok ? "" : "<-- MISMATCH")}");
            if (!ok)
            {
                var rawStart = span.Min(x => x.StartMs);
                var rawEnd = span.Max(x => x.EndMs);
                Console.WriteLine($"        raw span {rawStart}ms..{rawEnd}ms ({(rawEnd - rawStart) / 1000.0:0.0}s)" +
                                  (rawEnd < rawStart ? "  <-- ENDS BEFORE IT STARTS" : ""));
                Console.WriteLine($"        bullet   «{Short(text)}»");
                Console.WriteLine($"        claims   «{Short(claimed)}»");
                Console.WriteLine($"        audio is «{Short(heard)}»");
            }
        }

        // CLEAN CUT is PRECISION, not non-overlap. Two clips may share a moment when their points
        // genuinely share words (a Venn intersection); the DEFECT is CONTAINMENT - one clip's window
        // wholly covering another's, so the shorter point's moment plays inside the longer one and the
        // longer one is really "the whole stretch". That, plus over-long clips, is what the owner saw.
        var contained = 0;
        var partial = 0;
        for (var a = 0; a < clips.Count; a++)
            for (var b = a + 1; b < clips.Count; b++)
            {
                if (clips[a].Speaker != clips[b].Speaker) continue;
                var lo = Math.Max(clips[a].StartMs, clips[b].StartMs);
                var hi = Math.Min(clips[a].EndMs, clips[b].EndMs);
                if (hi - lo <= 0) continue;   // disjoint: ideal
                var aIn = clips[a].StartMs >= clips[b].StartMs && clips[a].EndMs <= clips[b].EndMs;
                var bIn = clips[b].StartMs >= clips[a].StartMs && clips[b].EndMs <= clips[a].EndMs;
                if (aIn || bIn)
                {
                    contained++;
                    Console.WriteLine($"  CONTAINS [{clips[a].Kind}] {clips[a].StartMs / 1000.0:0.0}-{clips[a].EndMs / 1000.0:0.0}s " +
                                      $"and [{clips[b].Kind}] {clips[b].StartMs / 1000.0:0.0}-{clips[b].EndMs / 1000.0:0.0}s " +
                                      $"<-- one plays inside the other");
                }
                else partial++;
            }

        var longest = clips.Count == 0 ? 0 : clips.Max(c => c.EndMs - c.StartMs);

        Console.WriteLine();
        Console.WriteLine($"RESULT: {checkedCount} note lines, {linked} linked, {good} of {linked} play the words they claim.");
        if (scores.Count > 0)
            Console.WriteLine($"        median clip match {Median(scores):P0}. Below ~50% the clip is not the moment the link names.");
        Console.WriteLine($"        longest clip {longest / 1000.0:0.0}s (a moment, not the whole utterance); {partial} genuine partial overlap(s).");
        Console.WriteLine(contained == 0
            ? "        clean-cut: no clip plays inside another - each is its own point."
            : $"        {contained} clip pair(s) CONTAINED - one point's moment plays inside another's.");
        Console.WriteLine("        A low score with a sensible transcript line means the TIMES are wrong;");
        Console.WriteLine("        a high score on a bullet that reads unrelated means the MATCH is wrong.");
        return contained == 0 ? 0 : 1;
    }

    static MeetingRecord? LatestWithAudio(MeetingStore store)
    {
        foreach (var s in store.List(null).Take(40))
        {
            var m = store.Get(s.Id);
            if (m is { Transcript.Count: > 0 } && EvidenceAudio.WavFor(m, Speaker.Them) is { } w && File.Exists(w))
                return m;
        }
        return null;
    }

    /// <summary>Share of the claimed line's content words that the clip's own decode also produced.
    /// Word overlap, not WER: the clip is re-recognised audio, so exact wording will differ.</summary>
    static double Overlap(string claimed, string heard)
    {
        var a = Words(claimed);
        var b = Words(heard);
        if (a.Count == 0) return 0;
        return a.Count(b.Contains) / (double)a.Count;
    }

    static HashSet<string> Words(string s) =>
        new((s ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '.', '?', '!', ':', ';', '-', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2), StringComparer.Ordinal);

    static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    static string Short(string s) => s.Length <= 90 ? s : s[..88] + "..";

    static int Fail(string why) { Console.WriteLine($"[--evidencetest] {why}"); return 1; }
}
