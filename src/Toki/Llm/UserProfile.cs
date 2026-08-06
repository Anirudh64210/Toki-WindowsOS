namespace Toki.Llm;

/// <summary>
/// The one place the dialogue brain reads and writes WHO IT'S TALKING TO. Toki always knew the
/// user's name for the greeting (<see cref="AppSettings.UserName"/> → Windows account fallback),
/// but the Ask pipeline never looked - which is how "what is my name" became a meeting-notes
/// search (real screenshot). This wraps the SAME persisted settings field (no second store, no
/// second source of truth: correcting your name in chat also fixes the greeting) plus a small
/// list of user-stated facts ("I work at …") - the "saved memories" shape of modern assistants
/// (ChatGPT memory / MemGPT: a page of durable user facts kept outside the conversation window).
///
/// Test seam: <see cref="SettingsOverride"/> points everything at an in-memory AppSettings, so
/// tests never read or write the real %APPDATA%\Toki\settings.json.
/// </summary>
public static class UserProfile
{
    /// <summary>Test seam: an in-memory settings object. While set, nothing touches disk.</summary>
    public static AppSettings? SettingsOverride;

    static AppSettings S => SettingsOverride ?? AppSettings.Current;

    const int MaxFacts = 10;

    /// <summary>The name Toki should call the user, or "" when nothing is known at all.
    /// (Explicitly set name first, else the cleaned Windows account name - same as the greeting.)</summary>
    public static string DisplayName => S.DisplayName;

    /// <summary>True when the user explicitly told Toki their name (vs. the Windows-account guess) -
    /// lets "what is my name" answer confidently vs. honestly hedged.</summary>
    public static bool NameWasSetByUser => !string.IsNullOrWhiteSpace(S.UserName);

    /// <summary>Set the user's name ("call me Anirudh") - persisted through the normal settings path,
    /// so the greeting uses it too.</summary>
    public static void SetName(string name)
    {
        S.UserName = name.Trim();
        Save();
    }

    /// <summary>Durable user-stated facts ("Works at Contoso"). Small, capped, newest kept.</summary>
    public static IReadOnlyList<string> Facts => S.UserFacts ?? (IReadOnlyList<string>)[];

    public static void AddFact(string fact)
    {
        fact = fact.Trim();
        if (fact.Length == 0) return;
        var list = S.UserFacts ??= new List<string>();
        list.RemoveAll(f => string.Equals(f, fact, StringComparison.OrdinalIgnoreCase));
        list.Add(fact);
        while (list.Count > MaxFacts) list.RemoveAt(0);
        Save();
    }

    static void Save()
    {
        if (SettingsOverride is not null) return;   // tests: in-memory only, never the real file
        S.Save();
    }
}
