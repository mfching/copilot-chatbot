using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CopilotChatbot.Models;

namespace CopilotChatbot.Services;

public sealed class ChatSessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _statePath;
    private readonly string _legacyStatePath;

    public ChatSessionStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CopilotChatbot");
        Directory.CreateDirectory(directory);
        _statePath = Path.Combine(directory, "chat-sessions.json.gz");
        _legacyStatePath = Path.Combine(directory, "chat-sessions.json");
    }

    public bool Exists => File.Exists(_statePath) || File.Exists(_legacyStatePath);

    public string StatePath => _statePath;

    public PersistedChatState Load()
    {
        if (File.Exists(_statePath))
            return LoadCompressed(_statePath);

        if (File.Exists(_legacyStatePath))
            return LoadJson(_legacyStatePath);

        return new PersistedChatState();
    }

    public void Save(PersistedChatState state)
    {
        var json = JsonSerializer.Serialize(state, Options);
        var tempPath = _statePath + ".tmp";
        using (var file = File.Create(tempPath))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        using (var writer = new StreamWriter(gzip))
        {
            writer.Write(json);
        }

        if (File.Exists(_statePath))
        {
            File.Replace(tempPath, _statePath, null);
        }
        else
        {
            File.Move(tempPath, _statePath);
        }
    }

    private static PersistedChatState LoadCompressed(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<PersistedChatState>(json, Options) ?? new PersistedChatState();
        }
        catch
        {
            return new PersistedChatState();
        }
    }

    private static PersistedChatState LoadJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PersistedChatState>(json, Options) ?? new PersistedChatState();
        }
        catch
        {
            return new PersistedChatState();
        }
    }
}
