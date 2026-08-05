using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>N6 — what the user typed during the meeting steers the written notes (the Granola model).
/// The rule that matters: a point someone bothered to write down BY HAND must never be lost, whatever
/// the model does or fails to do.</summary>
public sealed class TypedNotesTests
{
    const string Typed =
        "## Pricing\n" +
        "- they want the pilot to run four weeks\n" +
        "- [ ] send Ana the revised deck\n" +
        "- [x] confirmed the renewal date\n";

    static IReadOnlyList<TranscriptSegment> Transcript(params string[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(
            i % 2 == 0 ? Speaker.You : Speaker.Them, i * 5000, i * 5000 + 4000, l)).ToList();

    // ── the prompt actually carries them ──

    [Fact]
    public async Task The_typed_notes_reach_the_model_and_are_framed_as_the_users_own()
    {
        var llm = new CapturingLlm("NOTHING");
        await new LlmNoteWriter(llm).WriteAsync(
            Transcript("we talked about the pilot", "four weeks sounds right"), typed: Typed);

        var prompt = Assert.Single(llm.Prompts.Where(p => p.Contains("send Ana the revised deck")));
        Assert.Contains("typed these notes THEMSELVES", prompt);
        Assert.Contains("Cover EVERY point they wrote", prompt);
        // The tick state survives into the prompt — "done" vs "still a task" is exactly the signal
        // that should reach ACTION ITEMS.
        Assert.Contains("[task] send Ana the revised deck", prompt);
        Assert.Contains("[done] confirmed the renewal date", prompt);
    }

    [Fact]
    public async Task Without_typed_notes_the_prompt_is_unchanged()
    {
        var llm = new CapturingLlm("NOTHING");
        await new LlmNoteWriter(llm).WriteAsync(Transcript("hello", "hi"));
        Assert.All(llm.Prompts, p => Assert.DoesNotContain("typed these notes THEMSELVES", p));
    }

    [Fact]
    public async Task The_typed_block_is_capped_so_it_can_never_overrun_the_context()
    {
        var huge = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"- line number {i} of a very long note"));
        var llm = new CapturingLlm("NOTHING");
        await new LlmNoteWriter(llm).WriteAsync(Transcript("a", "b"), typed: huge);

        var prompt = llm.Prompts.First(p => p.Contains("line number 0"));
        Assert.Contains("…", prompt);                       // truncated
        Assert.DoesNotContain("line number 399", prompt);   // the tail was dropped, not the transcript
    }

    // ── they survive every failure path ──

    [Fact]
    public async Task With_no_model_at_all_the_typed_notes_still_become_the_note()
    {
        var note = await new LlmNoteWriter(new DeadLlm()).WriteAsync(
            Transcript("something was said"), typed: Typed);

        var section = Assert.Single(note.Sections, s => s.Heading == "Your notes");
        Assert.Contains("they want the pilot to run four weeks", section.Bullets);
        Assert.Contains(section.Bullets, b => b.Contains("send Ana the revised deck"));
    }

    [Fact]
    public async Task An_unticked_box_becomes_an_action_item_owned_by_you()
    {
        var note = await new LlmNoteWriter(new DeadLlm()).WriteAsync(Transcript("x"), typed: Typed);

        var action = Assert.Single(note.Actions, a => a.Text == "send Ana the revised deck");
        Assert.Equal("You", action.Owner);
        // A box they already ticked is NOT a follow-up.
        Assert.DoesNotContain(note.Actions, a => a.Text.Contains("renewal date"));
    }

    [Fact]
    public async Task A_meeting_the_model_calls_empty_still_keeps_what_you_wrote()
    {
        // The worst case: Toki decides "nothing substantive was discussed" over a page of your notes.
        var note = await new LlmNoteWriter(new CapturingLlm("NOTHING")).WriteAsync(
            Transcript("hi", "hello"), typed: Typed);

        Assert.Contains(note.Sections, s => s.Heading == "Your notes");
        Assert.Contains(note.Actions, a => a.Text == "send Ana the revised deck");
    }

    [Fact]
    public async Task Nothing_is_added_when_the_user_typed_nothing()
    {
        foreach (var empty in new[] { null, "", "   ", "\n\n" })
        {
            var note = await new LlmNoteWriter(new DeadLlm()).WriteAsync(Transcript("x"), typed: empty);
            Assert.DoesNotContain(note.Sections, s => s.Heading == "Your notes");
        }
    }

    // ── fakes ──

    /// <summary>Records every prompt and answers with a fixed string.</summary>
    sealed class CapturingLlm : ILlm
    {
        readonly string _answer;
        public CapturingLlm(string answer) => _answer = answer;
        public List<string> Prompts { get; } = new();
        public bool Ready => true;

        public Task<string> CompleteAsync(string system, string user, int max, CancellationToken ct = default)
        {
            Prompts.Add(user);
            return Task.FromResult(_answer);
        }

        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        { yield return _answer; await Task.CompletedTask; }
    }

    /// <summary>No model available — the heuristic floor.</summary>
    sealed class DeadLlm : ILlm
    {
        public bool Ready => false;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default) =>
            throw new InvalidOperationException("no model");
        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        { yield break; }
    }
}
