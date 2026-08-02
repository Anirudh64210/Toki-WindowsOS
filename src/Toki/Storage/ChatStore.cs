using System.IO;
using System.Text.Json;

namespace Toki.Storage;

/// <summary>
/// Persists the "Ask Toki" conversations to a small JSON file in %APPDATA%\Toki\chats.json — so the
/// history rail survives restarts (all local, no cloud). Deliberately tiny and forgiving: a corrupt or
/// missing file just yields an empty history, never a crash.
/// </summary>
public sealed class ChatStore
{
    public sealed class Turn
    {
        public string Role { get; set; } = "user";       // "user" | "assistant"
        public string Text { get; set; } = "";
        public List<SourceRef> Sources { get; set; } = [];
    }

    public sealed class SourceRef
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
    }

    public sealed class Conversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New chat";
        public string UpdatedAt { get; set; } = DateTimeOffset.Now.ToString("o");
        public List<Turn> Turns { get; set; } = [];

        public DateTimeOffset Updated =>
            DateTimeOffset.TryParse(UpdatedAt, out var d) ? d : DateTimeOffset.MinValue;
    }

    readonly string _path;

    public ChatStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "chats.json");

    public List<Conversation> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<Conversation>>(json) ?? [];
            return list.OrderByDescending(c => c.Updated).ToList();
        }
        catch { return []; }
    }

    public void Save(IEnumerable<Conversation> conversations)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(
                conversations.OrderByDescending(c => c.Updated).ToList(),
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_path, json);
        }
        catch { /* history is best-effort; never break the app over it */ }
    }
}
