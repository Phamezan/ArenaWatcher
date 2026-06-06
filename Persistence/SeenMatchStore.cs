using System.Text.Json;
using DiscordBot.Serialization;

namespace DiscordBot.Persistence;

public sealed class SeenMatchStore
{
    private readonly string _path;
    private readonly HashSet<string> _seenKeys;

    private SeenMatchStore(string path, HashSet<string> seenKeys)
    {
        _path = path;
        _seenKeys = seenKeys;
    }

    public static async Task<SeenMatchStore> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new SeenMatchStore(path, []);
        }

        await using var stream = File.OpenRead(path);
        var seenKeys = await JsonSerializer.DeserializeAsync<HashSet<string>>(stream, JsonOptions.Default)
            ?? [];

        return new SeenMatchStore(path, seenKeys);
    }

    public bool HasSeen(string key) => _seenKeys.Contains(key);

    public void MarkSeen(string key) => _seenKeys.Add(key);

    public async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, _seenKeys.Order(StringComparer.Ordinal).ToArray(), JsonOptions.Default);
    }
}
