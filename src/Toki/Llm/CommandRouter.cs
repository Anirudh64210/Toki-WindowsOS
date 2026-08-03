using System.Text.RegularExpressions;

namespace Toki.Llm;

/// <summary>The little "assistant commands" Toki can do (navigation / actions), distinct from questions.</summary>
public enum AppCommand
{
    OpenLibrary,
    OpenSettings,
    GoHome,
    OpenTemplates,
    NewNote,
    NewChat,
    ShowTasks,        // forwarded into the Ask engine (ListTasks) — the answer IS the response
    Close,
    Help,
    StartRecording,   // "start taking notes" → begin a manual recording (human-in-the-loop by definition)
    OpenAsk,          // "open the assistant" → the hub's Ask page
    UseApiModel,      // "switch to the api model"
    UseLocalModel,    // "switch to the on-device model"
    UseHeuristicMode, // "switch to heuristic" → the no-AI notes floor
    ThemeLight,       // "switch to white/light theme"
    ThemeDark,        // "switch to dark theme"
    GhostOn,          // "ghost mode on" → hidden from screen capture
    GhostOff,         // "ghost mode off" → visible in recordings
}

/// <summary>
/// Tier 0 of the understand-first pipeline: a deterministic router that turns a short imperative like
/// "take me to the library" into an app action — BEFORE any retrieval or model. Runs in ~1ms, no LLM.
///
/// The whole trick (from the shipping assistants — Alexa/App Intents/Voice Access): the WHOLE utterance
/// must BE a command (anchored ^…$, ≤8 words), and a QUESTION always wins a tie. A missed command is
/// cheap (it just falls through to Ask and gets answered); a false positive — Settings popping open
/// while you asked "what did we decide in the settings review?" — destroys trust. So the question-shape
/// veto is unconditional and checked first. Mirrors the "classify from query shape, not content words"
/// rule already in <see cref="QueryUnderstanding"/>.
/// </summary>
public static class CommandRouter
{
    // Starts like a question → never a command. (ASR gives no '?', so this lead-word test is load-bearing.)
    static readonly Regex QuestionLead = new(
        @"^(what|whats|what's|when|who|whos|who's|where|why|how|which|did|do|does|was|were|is|are|has|have|can\s+i|should|could\s+i|tell\s+me|give\s+me|show\s+me\s+(what|the\s+notes))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Politeness / address fluff stripped from the FRONT ("hey toki, please could you open…").
    // The address uses the FUZZY wake-name (WakeName.Pattern): push-to-talk ASR renders "Toki" as
    // Doki/Toky/Docie/Toku/Tokyo/… and a mis-heard name must never turn a command into a notes search.
    static readonly Regex Fluff = new(
        $@"^(hey\s+{WakeName.Pattern}\b[,!.]?|okay\s+{WakeName.Pattern}\b[,!.]?|ok\s+{WakeName.Pattern}\b[,!.]?|{WakeName.Pattern}\b[,!.]?|please\b|can\s+you\b|could\s+you\b|would\s+you\b|will\s+you\b|i\s+want\s+to\b|i\s+wanna\b|i'?d\s+like\s+to\b|i\s+need\s+to\b|let'?s\b|just\b|maybe\b|hey\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Politeness stripped from the END ("open the library please / for me / real quick / …, toki?").
    static readonly Regex TrailFluff = new(
        $@"(\s+(please|now|for\s+me|real\s+quick|quick|thanks|thank\s+you|already|then)|[\s,]+{WakeName.Pattern}\b)+\s*[.!?]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The navigation verbs Toki understands — kept broad so natural phrasings ("pull up", "jump to",
    // "let me see") all work, not just one hard-coded phrase.
    const string V = @"open|show|show\s+me|see|view|pull\s+up|bring\s+up|go\s+to|take\s+me\s+to|switch\s+to|jump\s+to|head\s+(to|over\s+to)|get\s+me\s+to|navigate\s+to|let\s+me\s+see";

    // "help" / "what can you do" is an EXPLICIT command even though it looks like a question — checked first.
    static readonly Regex HelpRx = new(@"^(help|what\s+can\s+you\s+do|what\s+can\s+i\s+say)\s*[.!?]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Every command pattern is ANCHORED — the whole (fluff-stripped) utterance must be the command.
    static readonly (Regex Rx, AppCommand Cmd)[] Commands =
    {
        // Recording: "start taking notes", "start recording", "record this meeting", "take notes".
        // ("start a note" stays NewNote below — that's the typed manual note, not a recording.)
        (Anchor(@"(start|begin)\s+(taking|writing)\s+notes?|(start|begin)\s+(a\s+|the\s+)?record(ing)?|record(\s+(this|the|a|my))?(\s+(meeting|call))?|take\s+notes|start\s+notes?\s*[- ]?\s*taking"), AppCommand.StartRecording),
        // Notes engine: api / on-device / heuristic. "model|mode|notes|engine|ai" tail is optional so
        // "switch to heuristic" alone works too; "key/settings" tails cover the spoken variants the
        // live run produced ("switch to api key settings" fell through to Q&A — a real screenshot bug).
        (Anchor($@"(?:{V}|use|enable)\s+(the\s+|my\s+|an?\s+)?(api|cloud)(\s+key)?(\s+(model|mode|models?|notes|engine|settings?))?"),   AppCommand.UseApiModel),
        (Anchor($@"(?:{V}|use|enable)\s+(the\s+|an?\s+)?(on[\s-]?device|local|offline)(\s+(model|mode|notes|engine|ai))?"), AppCommand.UseLocalModel),
        (Anchor($@"(?:{V}|use|enable)\s+(the\s+|an?\s+)?(heuristics?|basic|no[\s-]?ai|built[\s-]?in)(\s+(model|mode|notes|engine|extraction))?"), AppCommand.UseHeuristicMode),
        // Theme: "switch to white theme", "light mode", "go dark".
        (Anchor($@"(?:{V}|turn\s+on|enable|set)\s+(the\s+)?(white|light)\s+(theme|mode)|(white|light)\s+(theme|mode)|go\s+light"), AppCommand.ThemeLight),
        (Anchor($@"(?:{V}|turn\s+on|enable|set)\s+(the\s+)?(dark|black|night)\s+(theme|mode)|(dark|black|night)\s+(theme|mode)|go\s+dark"), AppCommand.ThemeDark),
        // Ghost (capture-exclusion): on = hidden from screen recordings, off = visible.
        (Anchor(@"(turn\s+|switch\s+|set\s+)?ghost(\s+mode)?\s+on|(turn\s+on|enable|activate)\s+ghost(\s+mode)?|hide\s+(toki\s+)?from\s+(screen\s+)?(recordings?|capture|share|sharing)"), AppCommand.GhostOn),
        (Anchor(@"(turn\s+|switch\s+|set\s+)?ghost(\s+mode)?\s+off|(turn\s+off|disable|deactivate)\s+ghost(\s+mode)?|show\s+(toki\s+)?in\s+(screen\s+)?(recordings?|captures?|shares?|sharing)"), AppCommand.GhostOff),
        // "a library" (not just "the/my") — spoken articles drift ("can you open a library for me?"
        // was a live mis-route to Q&A). Same for templates/settings below.
        (Anchor($@"(?:{V})\s+(the\s+|my\s+|an?\s+)?libr(?:ary|arry|ery)"),                                     AppCommand.OpenLibrary),
        (Anchor($@"(?:{V})\s+(the\s+|my\s+)?settings?|(where\s+are\s+)?(the\s+|my\s+)?settings?"),             AppCommand.OpenSettings),
        (Anchor($@"(?:{V})\s+(the\s+|my\s+|an?\s+)?templates?"),                                               AppCommand.OpenTemplates),
        (Anchor($@"(?:{V})\s+(the\s+)?(assistant|chat\s*bot|chat|ask(\s+(page|view|{WakeName.Pattern}))?)"),   AppCommand.OpenAsk),
        (Anchor($@"(go|take\s+me|head)\s+(back\s+)?home|(?:{V})\s+(the\s+)?(notch|pill|home|{WakeName.Pattern})"), AppCommand.GoHome),
        // "add a new note (in the library)" — the optional "new" and the optional destination tail are
        // what the live run said; both must still be the NewNote command, never a notes search.
        (Anchor(@"(new|create|start|add|make|write)\s+(a\s+|another\s+)?(new\s+)?(manual\s+|custom\s+|quick\s+)?notes?(\s+(in|to|into)\s+(the\s+|my\s+)?library)?"), AppCommand.NewNote),
        (Anchor(@"(new|start|clear|reset|fresh)\s+(the\s+)?(chat|conversation)|start\s+over"),                 AppCommand.NewChat),
        (Anchor($@"(?:{V}|list)\s+(me\s+)?(my\s+|the\s+)?(tasks?|to-?dos?|action\s+items?)"),                  AppCommand.ShowTasks),
        (Anchor(@"(close|hide|dismiss|go\s+away|get\s+out)(\s+(this|it|the\s+\w+|toki))?|never\s+mind|nevermind|cancel"), AppCommand.Close),
    };

    static Regex Anchor(string body) => new($@"^(?:{body})\s*[.!?]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The command this utterance IS, or null to fall through to the Q&amp;A engine.</summary>
    public static AppCommand? TryMatch(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return null;

        // Strip leading and trailing politeness/address ("hey toki, please open the library for me").
        for (var i = 0; i < 3 && Fluff.IsMatch(t); i++) t = Fluff.Replace(t, "").Trim();
        for (var i = 0; i < 3 && TrailFluff.IsMatch(t); i++) t = TrailFluff.Replace(t, "").Trim();

        if (HelpRx.IsMatch(t)) return AppCommand.Help;                 // explicit, before the veto
        // Drop a trailing "?": a POLITE command ("can you take me to the library?") is still a command.
        // Real questions are caught by the lead-word veto below (what/when/did/…), not by the "?".
        var q = t.TrimEnd('?', '.', '!', ' ');
        if (QuestionLead.IsMatch(q)) return null;                     // question SHAPE wins the tie
        // Commands are short — but spoken ones carry a word or two more chrome than typed ones
        // ("add a new note in the library" is 7 words and unambiguous), so the cap is 8. The anchored
        // whole-utterance patterns below are what actually holds precision, not this cap.
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return null;

        foreach (var (rx, cmd) in Commands)
            if (rx.IsMatch(q)) return cmd;
        return null;
    }

    // ── misheard-command resilience ──
    // Push-to-talk ASR garbles commands ("switch to LIKE theme" for "light theme"). The old pipeline
    // sent that to notes retrieval, which FABRICATED a link to an unrelated meeting (real
    // screenshot: the theme command was "answered" with a weekend-attendance action item). The VUI
    // error-recovery pattern: when a low-confidence command is recognised, degrade to a targeted
    // clarification (a safe no-op), never to an unrelated action — and never to retrieval.

    static readonly Regex SwitchVerbLead = new(
        @"^(switch|change|set|turn|toggle|put|make|use|enable|disable|activate|deactivate|go)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ThemeWordRx = new(@"\btheme\b|\b(dark|light|night|white)\s+mode\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex GhostWordRx = new(@"\bghost\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ModelWordRx = new(@"\b(model|engine)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A short command-SHAPED utterance (imperative switch-verb lead, command domain word) that
    /// didn't match any real command → a clarification reply, so a misheard command degrades to a
    /// safe question instead of a notes search. Null → not command-shaped; fall through to Q&amp;A.
    /// Call only after <see cref="TryMatch"/> returned null. The question-shape veto still applies
    /// ("why is the theme white?" is a question, never a clarification).
    /// </summary>
    public static string? TryNearMiss(string? input)
    {
        var t = (input ?? "").Trim();
        if (t.Length == 0) return null;
        for (var i = 0; i < 3 && Fluff.IsMatch(t); i++) t = Fluff.Replace(t, "").Trim();
        for (var i = 0; i < 3 && TrailFluff.IsMatch(t); i++) t = TrailFluff.Replace(t, "").Trim();
        var q = t.TrimEnd('?', '.', '!', ' ');
        if (q.Length == 0 || QuestionLead.IsMatch(q)) return null;
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return null;
        if (!SwitchVerbLead.IsMatch(q)) return null;
        // Content about the meetings ("go over the model discussion from yesterday") is Q&A, not a
        // garbled command — the meeting-domain words veto the clarification.
        if (Regex.IsMatch(q, @"\b(meetings?|calls?|syncs?|stand-?ups?|discussions?|discussed|yesterday|review)\b", RegexOptions.IgnoreCase))
            return null;

        if (ThemeWordRx.IsMatch(q))
            return "Sounds like a theme switch — say “switch to light theme” or “switch to dark theme” and I'll do it.";
        if (GhostWordRx.IsMatch(q))
            return "Sounds like ghost mode — say “ghost mode on” to hide from recordings, or “ghost mode off” to show.";
        if (ModelWordRx.IsMatch(q))
            return "Sounds like a notes-engine switch — say “switch to the on-device model”, “api model”, or “heuristic”.";
        return null;
    }
}
