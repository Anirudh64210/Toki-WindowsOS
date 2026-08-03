using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>The Tier-0 assistant-command router. The load-bearing property: a QUESTION must never be
/// mistaken for a command (a wrong window popping open destroys trust), while short imperatives do act.</summary>
public sealed class CommandRouterTests
{
    [Theory]
    [InlineData("open the library", AppCommand.OpenLibrary)]
    [InlineData("take me to the library", AppCommand.OpenLibrary)]
    [InlineData("go to my library", AppCommand.OpenLibrary)]
    [InlineData("open settings", AppCommand.OpenSettings)]
    [InlineData("take me to settings", AppCommand.OpenSettings)]
    [InlineData("show the templates", AppCommand.OpenTemplates)]
    [InlineData("go home", AppCommand.GoHome)]
    [InlineData("open the notch", AppCommand.GoHome)]
    [InlineData("new note", AppCommand.NewNote)]
    [InlineData("start a note", AppCommand.NewNote)]
    [InlineData("new chat", AppCommand.NewChat)]
    [InlineData("show my tasks", AppCommand.ShowTasks)]
    [InlineData("list my action items", AppCommand.ShowTasks)]
    [InlineData("close", AppCommand.Close)]
    [InlineData("never mind", AppCommand.Close)]
    [InlineData("help", AppCommand.Help)]
    [InlineData("what can you do", AppCommand.Help)]
    public void Matches_commands(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    [Theory]
    [InlineData("hey toki, open the library", AppCommand.OpenLibrary)]
    [InlineData("please take me to settings", AppCommand.OpenSettings)]
    [InlineData("can you open the library", AppCommand.OpenLibrary)]
    [InlineData("toki, new note", AppCommand.NewNote)]
    [InlineData("open the library for me", AppCommand.OpenLibrary)]
    [InlineData("show me the library please", AppCommand.OpenLibrary)]
    public void Strips_politeness_and_address(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    // Natural-language variations of the same intent should all work — not just one hard-coded phrase.
    [Theory]
    [InlineData("pull up my library", AppCommand.OpenLibrary)]
    [InlineData("jump to the library", AppCommand.OpenLibrary)]
    [InlineData("i'd like to see the library", AppCommand.OpenLibrary)]
    [InlineData("can you pull up the library for me", AppCommand.OpenLibrary)]
    [InlineData("let me see my library", AppCommand.OpenLibrary)]
    [InlineData("view settings", AppCommand.OpenSettings)]
    [InlineData("jump to settings now", AppCommand.OpenSettings)]
    [InlineData("head over to settings", AppCommand.OpenSettings)]
    [InlineData("i want to make a note", AppCommand.NewNote)]
    [InlineData("start over", AppCommand.NewChat)]
    [InlineData("pull up my tasks", AppCommand.ShowTasks)]
    public void Natural_phrasings(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    // ── the new command set: recording, engine switch, theme, ghost, assistant page ──
    [Theory]
    [InlineData("start taking notes", AppCommand.StartRecording)]
    [InlineData("start recording", AppCommand.StartRecording)]
    [InlineData("begin recording", AppCommand.StartRecording)]
    [InlineData("take notes", AppCommand.StartRecording)]
    [InlineData("record this meeting", AppCommand.StartRecording)]
    [InlineData("record the call", AppCommand.StartRecording)]
    [InlineData("record", AppCommand.StartRecording)]
    [InlineData("hey toki, start taking notes", AppCommand.StartRecording)]
    [InlineData("can you start recording please", AppCommand.StartRecording)]
    [InlineData("switch to api model", AppCommand.UseApiModel)]
    [InlineData("switch to the api model", AppCommand.UseApiModel)]
    [InlineData("use my api key", AppCommand.UseApiModel)]
    [InlineData("switch to on device model", AppCommand.UseLocalModel)]
    [InlineData("switch to the on-device model", AppCommand.UseLocalModel)]
    [InlineData("switch to the local model", AppCommand.UseLocalModel)]
    [InlineData("use the local model", AppCommand.UseLocalModel)]
    [InlineData("switch to heuristic model", AppCommand.UseHeuristicMode)]
    [InlineData("switch to heuristic", AppCommand.UseHeuristicMode)]
    [InlineData("use the built-in notes", AppCommand.UseHeuristicMode)]
    [InlineData("switch to white theme", AppCommand.ThemeLight)]
    [InlineData("switch to the light theme", AppCommand.ThemeLight)]
    [InlineData("light mode", AppCommand.ThemeLight)]
    [InlineData("switch to dark theme", AppCommand.ThemeDark)]
    [InlineData("dark mode", AppCommand.ThemeDark)]
    [InlineData("go dark", AppCommand.ThemeDark)]
    [InlineData("ghost mode on", AppCommand.GhostOn)]
    [InlineData("turn ghost mode on", AppCommand.GhostOn)]
    [InlineData("enable ghost mode", AppCommand.GhostOn)]
    [InlineData("ghost mode off", AppCommand.GhostOff)]
    [InlineData("turn ghost mode off", AppCommand.GhostOff)]
    [InlineData("disable ghost mode", AppCommand.GhostOff)]
    [InlineData("open assistant", AppCommand.OpenAsk)]
    [InlineData("open the assistant", AppCommand.OpenAsk)]
    [InlineData("open the chat", AppCommand.OpenAsk)]
    public void New_commands_match(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    // "start a note" is the TYPED manual note, not a recording — the two must not collide.
    [Fact]
    public void StartANote_IsStillNewNote()
        => Assert.Equal(AppCommand.NewNote, CommandRouter.TryMatch("start a note"));

    // The whole point: these are QUESTIONS that mention a command target — they must NOT fire an action.
    [Theory]
    [InlineData("what did we decide in the settings review?")]
    [InlineData("what settings did we discuss")]
    [InlineData("who opened the library note")]
    [InlineData("did I open the library")]
    [InlineData("when is my next meeting about the templates")]
    [InlineData("what are the action items in the home renovation meeting")]
    [InlineData("summarize my most recent meeting")]
    [InlineData("what did sarah say about the new note taking app")]
    [InlineData("how do I open the library")]        // a how-to question, not a command
    [InlineData("can you tell me what's in my library")]
    public void Questions_never_match(string input)
        => Assert.Null(CommandRouter.TryMatch(input));

    // Question-veto negatives for the NEW commands: mentioning recording/model/theme/ghost inside a
    // question must never fire the action.
    [Theory]
    [InlineData("what did we decide about the model switch?")]
    [InlineData("did we talk about ghost mode in the standup")]
    [InlineData("is ghost mode on")]
    [InlineData("why is the theme white")]
    [InlineData("how do I start recording")]
    [InlineData("did we record the meeting yesterday")]
    [InlineData("was anything recorded on friday")]
    [InlineData("should we switch to the api model")]
    [InlineData("what notes did we take in the sync")]
    [InlineData("which model are we using")]
    public void New_command_questions_never_match(string input)
        => Assert.Null(CommandRouter.TryMatch(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("open the library and tell me about yesterday's standup")]  // mixed → falls through to Ask
    [InlineData("blah blah open something random please now today")]        // too long / no target
    public void Non_commands_return_null(string input)
        => Assert.Null(CommandRouter.TryMatch(input));

    // ── P9.1: the FUZZY wake-name (push-to-talk ASR mis-hears "Toki" as Doki/Toky/Docie/Toku/Tokyo/
    //    Doky) + spoken article drift ("open A library") + "can you …" imperatives. Every one of
    //    these is the exact shape of a live mis-route that ended in a hallucinated notes answer. ──
    [Theory]
    [InlineData("Hey Doki, can you open a library for me?", AppCommand.OpenLibrary)]
    [InlineData("Hey Toky, can you open the library?", AppCommand.OpenLibrary)]
    [InlineData("hey docie, open the library", AppCommand.OpenLibrary)]
    [InlineData("toku, open the library", AppCommand.OpenLibrary)]
    [InlineData("tokyo, open the library", AppCommand.OpenLibrary)]
    [InlineData("hey doky, open my library", AppCommand.OpenLibrary)]
    [InlineData("open a library", AppCommand.OpenLibrary)]
    [InlineData("open the library, toki", AppCommand.OpenLibrary)]
    [InlineData("hey doki, new note", AppCommand.NewNote)]
    [InlineData("hey toky, start recording", AppCommand.StartRecording)]
    [InlineData("okay doki, switch to dark theme", AppCommand.ThemeDark)]
    public void Fuzzy_wake_name_and_articles_still_command(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    // ── P9.1: the live-run command misses — model switch + note phrasings must be commands. ──
    [Theory]
    [InlineData("switch to api key settings", AppCommand.UseApiModel)]
    [InlineData("can you switch to the api model", AppCommand.UseApiModel)]
    [InlineData("switch to the api key", AppCommand.UseApiModel)]
    [InlineData("use the api key settings", AppCommand.UseApiModel)]
    [InlineData("switch to an api model", AppCommand.UseApiModel)]
    [InlineData("add a new note in the library", AppCommand.NewNote)]
    [InlineData("add a note", AppCommand.NewNote)]
    [InlineData("can you add a new note in the library", AppCommand.NewNote)]
    [InlineData("create a new note in my library", AppCommand.NewNote)]
    public void Live_run_missed_commands_now_match(string input, AppCommand expected)
        => Assert.Equal(expected, CommandRouter.TryMatch(input));

    // The question-shape veto must survive all of the above breadth: a genuine question about a
    // command target — even wake-addressed — still falls through to the Q&A.
    [Theory]
    [InlineData("what did we decide about the library redesign?")]
    [InlineData("hey toki, what did we decide about the library redesign")]
    [InlineData("hey doki, which model are we using")]
    [InlineData("did they give me any tasks in this meeting")]
    [InlineData("can you tell me what we decided about the api")]
    [InlineData("why did we add a new note")]
    public void Wake_addressed_questions_still_fall_through(string input)
        => Assert.Null(CommandRouter.TryMatch(input));
}
