using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordBot.Configuration;
using DiscordBot.Serialization;

namespace DiscordBot.Services;

/// <summary>
/// Tiny built-in admin page for toggling which tracked players get Discord
/// win posts (DiscordPostAllowlist in appsettings.json) and for running the
/// watcher's maintenance commands (see <see cref="AdminCommandRunner"/>).
/// Serves a single page (GET /), accepts saves (POST /save), and runs
/// commands (POST /run?id=..., polled via GET /run/status); after a
/// successful save the process shuts down cleanly so docker compose
/// (restart: unless-stopped) brings it back up with the new config.
///
/// The page shows no secrets, so the token is optional: if WebUiToken is set,
/// requests must carry it (?token=... or X-Admin-Token).
/// </summary>
public sealed class AdminUiServer(
    AppConfig config,
    string configPath,
    int port,
    string? token,
    Action requestShutdown,
    AdminCommandRunner? commands = null)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        // http://+:port needs a URL ACL on Windows; localhost works everywhere
        // for local testing. Linux (the container) binds all interfaces so the
        // published docker port is reachable.
        var host = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "localhost" : "+";
        listener.Prefixes.Add($"http://{host}:{port}/");
        listener.Start();
        Console.WriteLine($"Admin UI listening on port {port} (token required).");

        await using var registration = cancellationToken.Register(listener.Stop);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                context.Response.StatusCode = 403;
                await WriteAsync(context.Response, "Forbidden: missing or wrong token.", "text/plain");
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET" && path is "/" or "/index.html")
            {
                await WriteAsync(context.Response, BuildPage(), "text/html");
            }
            else if (context.Request.HttpMethod == "POST" && path == "/save")
            {
                await HandleSaveAsync(context);
            }
            else if (context.Request.HttpMethod == "POST" && path == "/run")
            {
                await HandleRunAsync(context);
            }
            else if (context.Request.HttpMethod == "GET" && path == "/run/status")
            {
                await HandleRunStatusAsync(context);
            }
            else if (context.Request.HttpMethod == "GET" && path == "/run/image")
            {
                await HandleRunImageAsync(context);
            }
            else
            {
                context.Response.StatusCode = 404;
                await WriteAsync(context.Response, "Not found.", "text/plain");
            }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteAsync(context.Response, $"Error: {ex.Message}", "text/plain");
        }
        finally
        {
            context.Response.Close();
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(token)) return true; // token optional: page shows no secrets
        var provided = request.QueryString["token"] ?? request.Headers["X-Admin-Token"];
        return !string.IsNullOrEmpty(provided)
            && string.Equals(provided, token, StringComparison.Ordinal);
    }

    private async Task HandleSaveAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var form = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, JsonOptions.Default)
            ?? throw new InvalidOperationException("Empty save payload.");

        var json = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();

        if (form.TryGetValue("DiscordPostAllowlist", out var allowlist)
            && allowlist.ValueKind == JsonValueKind.Array)
        {
            json["DiscordPostAllowlist"] = new JsonArray(
                allowlist.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => (JsonNode)JsonValue.Create(e.GetString()!))
                    .ToArray());
        }

        // Validate the merged result before touching disk: it must still
        // deserialize into a valid AppConfig.
        var merged = json.ToJsonString();
        _ = JsonSerializer.Deserialize<AppConfig>(merged, JsonOptions.Default)
            ?? throw new InvalidOperationException("Merged config is invalid.");

        await File.WriteAllTextAsync(configPath, merged);
        Console.WriteLine("Admin UI: config saved, restarting to apply.");

        await WriteAsync(context.Response, "{\"ok\":true}", "application/json");

        // Respond first, then let compose restart us with the new config.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            requestShutdown();
        });
    }

    private async Task HandleRunAsync(HttpListenerContext context)
    {
        if (commands is null)
        {
            context.Response.StatusCode = 400;
            await WriteAsync(context.Response, "Commands are not available in this mode.", "text/plain");
            return;
        }

        var command = commands.Find(context.Request.QueryString["id"] ?? "");
        if (command is null)
        {
            context.Response.StatusCode = 404;
            await WriteAsync(context.Response, "Unknown command.", "text/plain");
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var parameters = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(body, JsonOptions.Default)
                ?? new Dictionary<string, string>();

        var job = commands.Start(command, parameters, out var error);
        if (job is null)
        {
            context.Response.StatusCode = 409;
            await WriteAsync(context.Response, error ?? "Could not start command.", "text/plain");
            return;
        }

        await WriteAsync(context.Response, JsonSerializer.Serialize(new { job = job.Id }), "application/json");
    }

    private async Task HandleRunStatusAsync(HttpListenerContext context)
    {
        var job = commands?.GetJob(context.Request.QueryString["job"] ?? "");
        if (job is null)
        {
            context.Response.StatusCode = 404;
            await WriteAsync(context.Response, "Unknown or expired job.", "text/plain");
            return;
        }

        string output;
        lock (job.Output)
        {
            output = job.Output.ToString();
        }

        await WriteAsync(
            context.Response,
            JsonSerializer.Serialize(new
            {
                state = job.State,
                output,
                hasImage = job.Image is not null,
            }),
            "application/json");
    }

    private async Task HandleRunImageAsync(HttpListenerContext context)
    {
        var job = commands?.GetJob(context.Request.QueryString["job"] ?? "");
        if (job?.Image is null)
        {
            context.Response.StatusCode = 404;
            await WriteAsync(context.Response, "No image for this job.", "text/plain");
            return;
        }

        context.Response.ContentType = "image/png";
        context.Response.ContentLength64 = job.Image.Length;
        await context.Response.OutputStream.WriteAsync(job.Image);
    }

    private string BuildPage()
    {
        var allowlist = new HashSet<string>(
            config.DiscordPostAllowlist ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Players come from the effective config (after RosterUrl is applied),
        // not the file — the file's TrackedPlayers can be stale or empty.
        var playersCheckboxes = new StringBuilder();
        foreach (var player in config.TrackedPlayers)
        {
            var riotId = $"{player.GameName}#{player.TagLine}";
            var isChecked = allowlist.Contains(player.GameName.Trim()) ? " checked" : "";
            playersCheckboxes.AppendLine(
                $"<label class=\"player\"><input type=\"checkbox\" name=\"allow\" value=\"{Escape(player.GameName)}\"{isChecked}> {Escape(riotId)}</label>");
        }

        // Commands are only available when the full watcher services exist
        // (not in --admin-ui-only mode).
        var commandsSection = "";
        var commandsJson = "[]";
        if (commands is not null)
        {
            commandsSection = """
                <h1 style="margin-top:36px">Commands</h1>
                <p class="sub">The watcher's maintenance commands — the same ones as
                <code>docker compose run --rm arena-watcher &lt;flag&gt;</code>. One at a time;
                output streams into the card. Docker-level ops (<code>up --build</code>,
                <code>logs</code>, <code>ps</code>) still run on the host; logs are also in Dozzle.</p>
                <div id="cmds"></div>
                """;
            commandsJson = JsonSerializer.Serialize(
                commands.Commands.Select(c => new
                {
                    id = c.Id,
                    label = c.Label,
                    description = c.Description,
                    dangerous = c.Dangerous,
                    hasImage = c.HasImage,
                    inputs = c.Inputs.Select(i => new { key = i.Key, label = i.Label, placeholder = i.Placeholder }),
                }),
                JsonOptions.Default);
        }

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>ArenaWatcher — Admin</title>
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <style>
                body { margin: 0; padding: 32px; background: #0b0e14; color: #e6e6e6;
                       font-family: system-ui, sans-serif; }
                main { max-width: 560px; margin: 0 auto; }
                h1 { font-size: 1.3em; }
                p.sub { color: #8b93a3; font-size: 0.85em; }
                label.player { display: flex; gap: 8px; align-items: center; margin: 8px 0;
                               padding: 10px 12px; background: #161b26; border: 1px solid #2a2f3a;
                               border-radius: 8px; cursor: pointer; }
                label.player:hover { border-color: #0ac8b9; }
                button { margin-top: 20px; padding: 10px 22px; border: 1px solid #0ac8b9;
                         background: #0ac8b9; color: #0b0e14; font-weight: 700; border-radius: 6px;
                         cursor: pointer; }
                button:disabled { opacity: 0.5; cursor: wait; }
                button.danger { border-color: #e06060; background: #e06060; }
                #status { margin-top: 12px; font-size: 0.85em; color: #8b93a3; }
                .cmd { margin: 12px 0; padding: 12px; background: #161b26;
                       border: 1px solid #2a2f3a; border-radius: 8px; }
                .cmd h2 { font-size: 1em; margin: 0 0 4px; }
                .cmd p { margin: 0 0 8px; color: #8b93a3; font-size: 0.82em; }
                .cmd input[type=text] { width: 100%; box-sizing: border-box; margin: 4px 0;
                       padding: 8px; background: #0b0e14; color: #e6e6e6;
                       border: 1px solid #2a2f3a; border-radius: 6px; }
                .cmd button { margin-top: 8px; }
                .cmd pre { display: none; max-height: 240px; overflow: auto; margin-top: 10px;
                       padding: 8px; background: #0b0e14; border: 1px solid #2a2f3a;
                       border-radius: 6px; font-size: 0.78em; white-space: pre-wrap; }
                .cmd img { display: none; max-width: 100%; margin-top: 10px; border-radius: 6px; }
              </style>
            </head>
            <body>
            <main>
              <h1>Discord win posts</h1>
              <p class="sub">Checked players get their Arena wins posted to Discord. The arena-tracker dashboard still updates for everyone.</p>
              <form id="f">
                {{playersCheckboxes}}
                <button type="submit">Save &amp; restart</button>
                <div id="status"></div>
              </form>
              {{commandsSection}}
            </main>
            <script>
              const token = new URLSearchParams(location.search).get("token") || "";
              document.getElementById("f").addEventListener("submit", async (e) => {
                e.preventDefault();
                const fd = new FormData(e.target);
                const status = document.getElementById("status");
                status.textContent = "Saving...";
                const resp = await fetch("/save?token=" + encodeURIComponent(token), {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify({ DiscordPostAllowlist: fd.getAll("allow") }),
                });
                status.textContent = resp.ok
                  ? "Saved. The watcher is restarting with the new config (give it a few seconds)."
                  : "Save failed: " + await resp.text();
              });

              const commands = {{commandsJson}};
              const cmdsRoot = document.getElementById("cmds");
              if (cmdsRoot) {
                for (const cmd of commands) {
                  const card = document.createElement("div");
                  card.className = "cmd";
                  card.innerHTML =
                    "<h2></h2><p></p>" +
                    cmd.inputs.map(i =>
                      '<input type="text" data-key="' + i.key + '" placeholder="' + i.label +
                      ' — e.g. ' + i.placeholder + '">').join("") +
                    '<button' + (cmd.dangerous ? ' class="danger"' : '') + '>Run</button>' +
                    "<pre></pre><img alt=\"rendered card\">";
                  card.querySelector("h2").textContent = cmd.label;
                  card.querySelector("p").textContent = cmd.description;
                  const btn = card.querySelector("button");
                  const out = card.querySelector("pre");
                  const img = card.querySelector("img");
                  btn.addEventListener("click", async () => {
                    const params = {};
                    for (const input of card.querySelectorAll("input[data-key]")) {
                      params[input.dataset.key] = input.value.trim();
                    }
                    if (cmd.dangerous && !confirm("Really run \"" + cmd.label + "\"?")) return;
                    btn.disabled = true;
                    out.style.display = "block";
                    img.style.display = "none";
                    out.textContent = "Starting...";
                    const resp = await fetch("/run?token=" + encodeURIComponent(token) + "&id=" + cmd.id, {
                      method: "POST",
                      headers: { "Content-Type": "application/json" },
                      body: JSON.stringify(params),
                    });
                    if (!resp.ok) {
                      out.textContent = "Failed to start: " + await resp.text();
                      btn.disabled = false;
                      return;
                    }
                    const { job } = await resp.json();
                    const poll = async () => {
                      const s = await (await fetch(
                        "/run/status?token=" + encodeURIComponent(token) + "&job=" + job)).json();
                      out.textContent = s.output || "(no output yet)";
                      out.scrollTop = out.scrollHeight;
                      if (s.state === "running") {
                        setTimeout(poll, 1000);
                      } else {
                        if (s.hasImage) {
                          img.src = "/run/image?token=" + encodeURIComponent(token) + "&job=" + job;
                          img.style.display = "block";
                        }
                        btn.disabled = false;
                      }
                    };
                    poll();
                  });
                  cmdsRoot.appendChild(card);
                }
              }
            </script>
            </body>
            </html>
            """;
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static async Task WriteAsync(HttpListenerResponse response, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
