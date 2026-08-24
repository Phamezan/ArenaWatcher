using System.Text.Json;
using DiscordBot.Models;
using DiscordBot.Serialization;

namespace DiscordBot.Persistence;

/// <summary>
/// Wins whose arena-tracker sync failed, held so a later cycle can resend them.
///
/// A match is marked seen once Discord has been posted, which is the right call
/// for Discord (posting twice is worse than posting late) but used to mean a
/// failed dashboard sync was lost for good. The worker dedupes on matchId, so
/// resending is harmless and this queue can retry freely.
/// </summary>
public sealed class PendingWinStore
{
    /// <summary>Bounded so a long outage cannot grow the file without limit.</summary>
    private const int MaxEntries = 500;

    private readonly string _path;
    private readonly Dictionary<string, ArenaWinEvent> _pending;

    private PendingWinStore(string path, Dictionary<string, ArenaWinEvent> pending)
    {
        _path = path;
        _pending = pending;
    }

    public int Count => _pending.Count;

    public static async Task<PendingWinStore> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new PendingWinStore(path, []);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<List<ArenaWinEvent>>(stream, JsonOptions.Default) ?? [];
            return new PendingWinStore(path, entries.ToDictionary(Key, entry => entry));
        }
        catch (Exception ex)
        {
            // A corrupt queue must not stop the watcher; losing it is no worse
            // than the behaviour this class replaces.
            Console.WriteLine($"Could not read pending wins from {path}: {ex.Message}");
            return new PendingWinStore(path, []);
        }
    }

    private static string Key(ArenaWinEvent win) => $"{win.MatchId}:{win.Summoner}";

    public void Add(ArenaWinEvent win)
    {
        if (_pending.Count >= MaxEntries && !_pending.ContainsKey(Key(win)))
        {
            Console.WriteLine($"Pending win queue is full ({MaxEntries}); dropping {Key(win)}.");
            return;
        }

        _pending[Key(win)] = win;
    }

    public void Remove(ArenaWinEvent win) => _pending.Remove(Key(win));

    public IReadOnlyList<ArenaWinEvent> Drain() => _pending.Values.ToList();

    public async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, _pending.Values.ToArray(), JsonOptions.Default);
    }
}
