using System.Collections.Concurrent;
using System.Text;
using DiscordBot.Rendering;

namespace DiscordBot.Services;

/// <summary>
/// Runs the watcher's maintenance commands (the same ones the CLI flags expose,
/// e.g. `docker compose run --rm arena-watcher --post-latest`) from the admin
/// web UI. One command at a time; console output produced while a command runs
/// is teed into that job's buffer so the page can show live progress.
/// </summary>
public sealed class AdminCommandRunner
{
    public sealed record CommandInput(string Key, string Label, string Placeholder);

    public sealed record Command(
        string Id,
        string Label,
        string Description,
        CommandInput[] Inputs,
        bool Dangerous,
        bool HasImage,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<byte[]?>> Execute);

    public sealed class Job
    {
        public required string Id { get; init; }
        public required string CommandId { get; init; }
        public StringBuilder Output { get; } = new();
        public string State { get; set; } = "running"; // running | done | failed
        public byte[]? Image { get; set; }
    }

    private readonly IReadOnlyList<Command> _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly object _bufferLock = new();
    private StringBuilder? _activeBuffer;

    public AdminCommandRunner(
        ArenaWatcherService watcher,
        SeasonBackfillService seasonBackfill,
        MatchCardRenderer matchCardRenderer,
        Action requestShutdown)
    {
        _commands =
        [
            new Command(
                "post-latest",
                "Post latest matches",
                "Post the latest Arena match for every tracked player to Discord. Same as `--post-latest`.",
                [],
                Dangerous: false,
                HasImage: false,
                async (_, ct) => { await watcher.PostLatestMatchForTrackedPlayersAsync(ct); return null; }),
            new Command(
                "post-latest-for",
                "Post latest match for one player",
                "Post the latest Arena match for a single player, e.g. after downtime. Same as `--post-latest-for \"GameName#TagLine\"`.",
                [new CommandInput("riotId", "Riot ID", "GameName#TagLine")],
                Dangerous: false,
                HasImage: false,
                async (p, ct) => { await watcher.PostLatestMatchForPlayerAsync(p["riotId"], ct); return null; }),
            new Command(
                "inspect-latest",
                "Inspect latest matches",
                "Print the participant breakdown of the latest match for every tracked player, without posting anything. Same as `--inspect-latest`.",
                [],
                Dangerous: false,
                HasImage: false,
                async (_, ct) => { await watcher.InspectLatestMatchForTrackedPlayersAsync(ct); return null; }),
            new Command(
                "post-latest-group-test",
                "Post group result test",
                "Post a group match result test card to Discord. Same as `--post-latest-group-test`.",
                [],
                Dangerous: false,
                HasImage: false,
                async (_, ct) => { await watcher.PostLatestGroupTestForTrackedPlayersAsync(ct); return null; }),
            new Command(
                "backfill-season",
                "Season backfill (incremental)",
                "Re-scan every tracked player's matches since the last backfill (plus a 3h overlap) and push fresh snapshots to arena-tracker. Same as `--backfill-season`.",
                [],
                Dangerous: false,
                HasImage: false,
                async (_, ct) => { await seasonBackfill.ForceBackfillAsync(fullScan: false, ct); return null; }),
            new Command(
                "backfill-season-full",
                "Season backfill (full)",
                "Re-scan EVERY match since season start for every tracked player and rebuild from scratch. Slow (~15 min per player). Same as `--backfill-season --full`.",
                [],
                Dangerous: true,
                HasImage: false,
                async (_, ct) => { await seasonBackfill.ForceBackfillAsync(fullScan: true, ct); return null; }),
            new Command(
                "calibrate-season",
                "Calibrate season start",
                "Print unique first-place champion counts per candidate cutoff date for one player, to pin the season start. Same as `--calibrate-season \"GameName#TagLine\" --since \"YYYY-MM-DD\"`.",
                [
                    new CommandInput("riotId", "Riot ID", "GameName#TagLine"),
                    new CommandInput("since", "Since date", "2026-01-01"),
                ],
                Dangerous: false,
                HasImage: false,
                async (p, ct) => { await seasonBackfill.CalibrateAsync(p["riotId"], p["since"], ct); return null; }),
            new Command(
                "render-layout-test",
                "Render layout test card",
                "Render a sample group card and show it inline. Same as `--render-layout-test` (no file is written).",
                [],
                Dangerous: false,
                HasImage: true,
                async (_, ct) => await matchCardRenderer.RenderGroupAsync(LayoutTestData.CreateGroupCards(), ct)),
            new Command(
                "restart",
                "Restart watcher",
                "Shut the process down; docker compose (restart: unless-stopped) brings it right back up. Same as `docker compose restart`.",
                [],
                Dangerous: true,
                HasImage: false,
                (_, _) =>
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        requestShutdown();
                    });
                    return Task.FromResult<byte[]?>(null);
                }),
        ];

        // Tee console output into the active job's buffer so the page can show it.
        Console.SetOut(new TeeWriter(Console.Out, this));
    }

    public IReadOnlyList<Command> Commands => _commands;

    public Command? Find(string id) =>
        _commands.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts a command. Returns null (and sets error) when another is still running.</summary>
    public Job? Start(Command command, IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        error = Validate(command, parameters);
        if (error is not null)
        {
            return null;
        }

        if (!_gate.Wait(0))
        {
            error = "Another command is still running — wait for it to finish.";
            return null;
        }

        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            CommandId = command.Id,
        };
        _jobs[job.Id] = job;
        PruneJobs();

        _ = Task.Run(async () =>
        {
            lock (_bufferLock)
            {
                _activeBuffer = job.Output;
            }

            try
            {
                Console.WriteLine($"[admin-ui] running '{command.Id}'...");
                job.Image = await command.Execute(parameters, CancellationToken.None);
                job.State = "done";
                Console.WriteLine($"[admin-ui] '{command.Id}' finished.");
            }
            catch (Exception ex)
            {
                job.State = "failed";
                Console.WriteLine($"[admin-ui] '{command.Id}' failed: {ex.Message}");
            }
            finally
            {
                lock (_bufferLock)
                {
                    _activeBuffer = null;
                }

                _gate.Release();
            }
        });

        return job;
    }

    public Job? GetJob(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    private void Append(string value)
    {
        StringBuilder? buffer;
        lock (_bufferLock)
        {
            buffer = _activeBuffer;
        }

        if (buffer is null)
        {
            return;
        }

        lock (buffer)
        {
            buffer.Append(value);
        }
    }

    private static string? Validate(Command command, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var input in command.Inputs)
        {
            if (!parameters.TryGetValue(input.Key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return $"Missing input \"{input.Key}\".";
            }
        }

        if (parameters.TryGetValue("riotId", out var riotId) && !riotId.Contains('#'))
        {
            return "Riot ID must look like \"GameName#TagLine\".";
        }

        if (parameters.TryGetValue("since", out var since) && !DateOnly.TryParse(since, out _))
        {
            return "Since must be a date like \"2026-01-01\".";
        }

        return null;
    }

    private void PruneJobs()
    {
        // Keep the dictionary bounded: drop finished jobs beyond the last 20.
        var finished = _jobs.Values
            .Where(j => j.State != "running")
            .OrderByDescending(j => j.Id, StringComparer.Ordinal)
            .Skip(20)
            .ToList();
        foreach (var job in finished)
        {
            _jobs.TryRemove(job.Id, out _);
        }
    }

    private sealed class TeeWriter(TextWriter inner, AdminCommandRunner owner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            owner.Append(value.ToString());
        }

        public override void Write(string? value)
        {
            inner.Write(value);
            if (value is not null)
            {
                owner.Append(value);
            }
        }
    }
}
