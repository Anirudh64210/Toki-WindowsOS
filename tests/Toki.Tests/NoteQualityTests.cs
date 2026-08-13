using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Toki.Llm;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The note-quality failures found in two REAL meetings recorded 2026-08-11, locked down so they cannot
/// come back. Both were the kind of failure that makes the whole product look broken to the person
/// reading it, while every existing test stayed green.
/// </summary>
public sealed class NoteQualityTests
{
    static IReadOnlyList<TranscriptSegment> Transcript(params string[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(
            i % 2 == 0 ? Speaker.You : Speaker.Them, i * 5000, i * 5000 + 4000, l)).ToList();

    // ── the summary must never be raw transcript ──

    [Fact]
    public async Task A_model_that_skips_the_summary_is_asked_again_not_papered_over()
    {
        // Exactly what happened: good sections, no SUMMARY. The old code pasted in the heuristic's
        // summary, which is the opening words of the transcript.
        var llm = new ScriptedLlm(
            "TITLE\nAttendance automation\n\nDECISION\n- Propose automated notifications",  // no SUMMARY
            "Decision log",
            "The team discussed automating attendance notifications and agreed to propose it.");

        var note = await new LlmNoteWriter(llm).WriteAsync(
            Transcript("Hello.", "Uh hi Sai, you are actually on mute.", "Hey Davy.",
                       "So Abhishek is one of the supervisors and he wanted a proposal."));

        Assert.DoesNotContain("you are actually on mute", note.Tldr);
        Assert.Contains("automating attendance", note.Tldr);
    }

    [Fact]
    public async Task If_the_retry_also_fails_the_summary_is_empty_not_transcript()
    {
        // An empty section is honest. Raw transcript labelled "Summary" is not.
        var llm = new ScriptedLlm(
            "TITLE\nSomething\n\nDECISION\n- do the thing",
            "Decision log",
            "NOTHING");

        var note = await new LlmNoteWriter(llm).WriteAsync(
            Transcript("Hello.", "Uh hi Sai, you are actually on mute.", "Right, so about the pricing."));

        Assert.True(string.IsNullOrWhiteSpace(note.Tldr), $"expected no summary, got: {note.Tldr}");
        Assert.DoesNotContain("on mute", note.Tldr ?? "");
    }

    // ── a name is not a to-do ──

    [Fact]
    public async Task A_register_being_read_out_does_not_become_a_to_do_list()
    {
        // The real one: eleven "tasks" that were just the names of everyone on the call.
        var llm = new ScriptedLlm(
            "TITLE\nTeam reports\n\nSUMMARY\nThe chair asked every team lead to share their tracker.\n\n"
            + "ACTION ITEMS\n- Priyanka\n- Abhishek\n- Yashina\n- Dakshita Ganesh\n"
            + "- share your team's tracker in the folder | owner: Priyanka",
            "Decision log");

        var note = await new LlmNoteWriter(llm).WriteAsync(
            Transcript("Priyanka, Abhishek.", "Dakshita, Ganesh, come on someone."));

        Assert.All(note.Actions, a => Assert.True(a.Text.Split(' ').Length > 2, $"bare name kept: {a.Text}"));
        var kept = Assert.Single(note.Actions);
        Assert.Contains("tracker", kept.Text);
        Assert.Equal("Priyanka", kept.Owner);
    }

    [Fact]
    public async Task The_same_task_said_twice_is_one_line()
    {
        var llm = new ScriptedLlm(
            "TITLE\nT\n\nSUMMARY\nS.\n\nACTION ITEMS\n"
            + "- connect Abhishek with the team member responsible for automated updates\n"
            + "- connect Abhishek with the person responsible for the automated updates",
            "Decision log");

        var note = await new LlmNoteWriter(llm).WriteAsync(Transcript("a", "b"));
        Assert.Single(note.Actions);
    }

    [Fact]
    public async Task Real_tasks_are_never_dropped_by_the_name_filter()
    {
        var llm = new ScriptedLlm(
            "TITLE\nT\n\nSUMMARY\nS.\n\nACTION ITEMS\n"
            + "- share viewing access to the attendance sheet | owner: You\n"
            + "- Ask Priyanka to make the folder | owner: Craig",
            "Decision log");

        var note = await new LlmNoteWriter(llm).WriteAsync(Transcript("a", "b"));
        Assert.Equal(2, note.Actions.Count);
    }

    // ── fakes ──

    /// <summary>Answers each call in order, then repeats the last answer. The writer makes several
    /// calls (map / classify / final / summary), so the order is what makes the test readable.</summary>
    sealed class ScriptedLlm : ILlm
    {
        readonly string[] _answers;
        int _i;
        public ScriptedLlm(params string[] answers) => _answers = answers;
        public bool Ready => true;

        public Task<string> CompleteAsync(string system, string user, int max, CancellationToken ct = default)
        {
            // The classify call asks for a template name; hand it the one meant for it wherever it lands.
            if (user.Contains("Pick the single best note template"))
                return Task.FromResult(_answers.FirstOrDefault(a => a == "Decision log") ?? "General");
            var answer = _i < _answers.Length ? _answers[_i] : _answers[^1];
            _i++;
            if (answer == "Decision log") { answer = _i < _answers.Length ? _answers[_i] : _answers[^1]; _i++; }
            return Task.FromResult(answer);
        }

        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        { yield return ""; await Task.CompletedTask; }
    }
}
