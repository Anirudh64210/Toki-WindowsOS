using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE TITLE ARRIVES BEFORE THE NOTE DOES.
///
/// The final pass generates TITLE first, then SUMMARY, then the sections - and it runs for ~12s.
/// Buffering the whole generation meant the notch showed "Writing your notes" for all of it, then
/// everything at once. It now streams, parses the title the moment its LINE closes, and shows it.
/// Measured on meeting 128: the title was known 9.1 seconds before the note was ready.
///
/// The rule this file protects: STREAMING IS A DISPLAY CONCERN AND MUST NOT CHANGE THE ANSWER. The
/// note produced with an observer attached has to be identical to the one produced without.
///
/// It also pins the ILlm contract that made this possible. `ScriptedLlm` in NoteQualityTests used to
/// stub StreamAsync as `yield return ""`, which was invisible until something streamed and then
/// silently emptied six notes. A fake that does not honour its interface is a test that measures the
/// fake.
///
/// MUTATION PROBE: firing the observer per TOKEN instead of per completed line fails
/// A_half_written_title_is_never_shown (it would report "AI-Dri"); dropping the observer entirely
/// fails The_title_is_known_before_the_note_is.
/// </summary>
public sealed class TitleRevealTests
{
    const string Note =
        "TITLE\nAI-Driven CI/CD Optimization\n\nSUMMARY\nThe team agreed to automate runner choices.\n\n"
        + "KEY POINTS\n- Clear if-then rules enable automation\n- Secrets move to company level\n\n"
        + "ACTION ITEMS\n- Priyanka to analyse user workflows";

    static IReadOnlyList<TranscriptSegment> Transcript() =>
        Enumerable.Range(0, 8).Select(i => new TranscriptSegment(
            i % 2 == 0 ? Speaker.You : Speaker.Them, i * 5000, i * 5000 + 4000,
            i == 3 ? "Priyanka can you analyse the user workflows for if-then rules"
                   : $"we should automate the runner and build decisions, point {i}")).ToList();

    [Fact]
    public async Task The_title_is_known_before_the_note_is()
    {
        var llm = new PiecewiseLlm(Note);
        App.SummarizeTitle = null;

        var note = await new LlmNoteWriter(llm).WriteAsync(Transcript());

        Assert.NotNull(note);
        // Set DURING generation, not after: the whole point is that the wait stops being blank.
        Assert.Equal("AI-Driven CI/CD Optimization", App.SummarizeTitle);
        Assert.True(llm.TitleSeenAtPiece > 0, "the title was never observed mid-stream");
        Assert.True(llm.TitleSeenAtPiece < llm.TotalPieces,
            $"the title only appeared on the last piece ({llm.TitleSeenAtPiece}/{llm.TotalPieces}) - that is not early");
        App.SummarizeTitle = null;
    }

    [Fact]
    public async Task A_half_written_title_is_never_shown()
    {
        // The stream delivers several pieces inside one line. Reporting on every piece would put a
        // truncated title on screen and then correct it, which reads as a glitch.
        var llm = new PiecewiseLlm(Note, splitMidLine: true);
        App.SummarizeTitle = null;

        await new LlmNoteWriter(llm).WriteAsync(Transcript());

        Assert.Equal("AI-Driven CI/CD Optimization", App.SummarizeTitle);
        App.SummarizeTitle = null;
    }

    [Fact]
    public async Task Streaming_does_not_change_the_note()
    {
        // The observer is a display concern. The note it produces must match the buffered one exactly.
        App.SummarizeTitle = null;
        var streamed = await new LlmNoteWriter(new PiecewiseLlm(Note)).WriteAsync(Transcript());
        App.SummarizeTitle = null;
        var buffered = await new LlmNoteWriter(new WholeAnswerLlm(Note)).WriteAsync(Transcript());
        App.SummarizeTitle = null;

        Assert.NotNull(streamed);
        Assert.NotNull(buffered);
        Assert.Equal(buffered!.Title, streamed!.Title);
        Assert.Equal(buffered.Tldr, streamed.Tldr);
        Assert.Equal(buffered.Actions.Select(a => a.Text), streamed.Actions.Select(a => a.Text));
        Assert.Equal(buffered.Sections.SelectMany(s => s.Bullets), streamed.Sections.SelectMany(s => s.Bullets));
    }

    // ── fakes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Answers everything with <paramref name="note"/>, streamed in pieces. Records when the
    /// title first became visible so a test can assert it was EARLY, not merely eventual.</summary>
    sealed class PiecewiseLlm(string note, bool splitMidLine = false) : ILlm
    {
        public bool Ready => true;
        public int TitleSeenAtPiece { get; private set; }
        public int TotalPieces { get; private set; }

        string Answer(string user) =>
            user.Contains("Pick the single best note template") ? "General" : note;

        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default)
            => Task.FromResult(Answer(u));

        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<string>? stop = null)
        {
            var whole = Answer(u);
            var pieces = Pieces(whole).ToList();
            if (whole == note) TotalPieces = pieces.Count;
            for (var i = 0; i < pieces.Count; i++)
            {
                if (whole == note && TitleSeenAtPiece == 0 && App.SummarizeTitle is { Length: > 0 })
                    TitleSeenAtPiece = i;
                yield return pieces[i];
            }
            if (whole == note && TitleSeenAtPiece == 0 && App.SummarizeTitle is { Length: > 0 })
                TitleSeenAtPiece = pieces.Count;
            await Task.CompletedTask;
        }

        IEnumerable<string> Pieces(string whole)
        {
            var lines = whole.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = i < lines.Length - 1 ? lines[i] + "\n" : lines[i];
                if (!splitMidLine || line.Length < 4) { yield return line; continue; }
                // two pieces per line, so a per-token observer would see a half title
                yield return line[..(line.Length / 2)];
                yield return line[(line.Length / 2)..];
            }
        }
    }

    /// <summary>Streams the answer as ONE piece - the buffered baseline.</summary>
    sealed class WholeAnswerLlm(string note) : ILlm
    {
        public bool Ready => true;
        string Answer(string user) =>
            user.Contains("Pick the single best note template") ? "General" : note;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default)
            => Task.FromResult(Answer(u));
        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<string>? stop = null)
        { yield return Answer(u); await Task.CompletedTask; }
    }
}
