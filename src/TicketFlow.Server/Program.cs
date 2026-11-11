using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using TicketFlow.Core;
using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;
using TicketFlow.Core.Ops;
using TicketFlow.Core.Reviews;
using TicketFlow.Server;

var paths = AppPaths.Default();
var configStore = new ConfigStore(paths);
var cfg = configStore.Current;
var token = Environment.GetEnvironmentVariable("TICKETFLOW_TOKEN") is { Length: > 0 } envToken ? envToken : AuthToken.LoadOrCreate(paths);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://127.0.0.1:{cfg.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
    foreach (var c in Json.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

var git = new GitCli();

var workspace = new Workspace(configStore, git, new GitHubClient(() => configStore.Current.GitHub.User),
    new ArchiveStore(paths), new TrashStore(paths), new JournalStore(paths), new ChecksStore(paths));
IOperation[] ops =
[
    new AssignOp(), new CommitOp(), new ResumeOp(), new PushOp(), new CompleteOp(), new DiscardTicketOp(),
    new DiscardChangesOp(), new RestoreOp(), new SyncOp(), new ResolveOp(), new RebaseControlOp(), new MetaOp(),
    new CheckOp(),
];
var runner = new OpRunner(workspace, ops);
var reviews = new PrReviewService(workspace, new PrReviewStore(paths), new ClaudeCli()) { OpBusy = () => runner.Busy };
var hub = new EventHub(workspace, runner, reviews);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(workspace);
builder.Services.AddSingleton(runner);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(reviews);
builder.Services.AddHostedService<ReviewWorker>();
builder.Services.AddSingleton<PrPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrPoller>());
builder.Services.AddHostedService<RepoWatcher>();

var app = builder.Build();

// ---- security: loopback only, same-origin, per-install token (cookie) ----
const string CookieName = "ticketflow";
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost"))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Host is "127.0.0.1" or "localhost"))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (ctx.Request.Path == "/auth")
    {
        if (ctx.Request.Query["t"] == token)
        {
            ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Expires = DateTimeOffset.Now.AddYears(1), Path = "/",
            });
            ctx.Response.Redirect("/");
        }
        else ctx.Response.StatusCode = 401;
        return;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        ctx.Request.Cookies[CookieName] != token &&
        ctx.Request.Headers["X-TicketFlow-Token"] != token)
    {
        ctx.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/state", (Workspace w) => w.State());

api.MapGet("/events", async (HttpContext ctx, EventHub h, Workspace w, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    var (id, reader) = h.Subscribe();
    try
    {
        await ctx.Response.WriteAsync(EventHub.Format("state", w.State()), ct);
        await ctx.Response.WriteAsync(EventHub.Format("reviews", h.Reviews.View()), ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            var readTask = reader.WaitToReadAsync(ct).AsTask();
            var done = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(20), ct));
            if (done != readTask)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
            }
            else
            {
                if (!await readTask) break;
                while (reader.TryRead(out var msg)) await ctx.Response.WriteAsync(msg, ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        h.Unsubscribe(id);
    }
});

api.MapPost("/visible", (EventHub h) =>
{
    h.MarkVisible();
    return Results.NoContent();
});

api.MapPost("/refresh", async (Workspace w, PrPoller poller) =>
{
    w.Scanner.ClearCache();
    await w.RescanAllAsync();
    await w.PollPrsAsync();
    return w.State();
});

// ---- config ----
api.MapGet("/config", (Workspace w) =>
{
    var hook = ClaudeHookExclusions.Load(w.Config.ClaudeSettingsPath);
    return new
    {
        config = w.Config,
        paths = new { config = paths.ConfigFile, data = paths.DataDir, home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
        claudeHook = new { path = hook.SettingsPath, patterns = hook.Sources, error = hook.Error },
    };
});

api.MapPut("/config", (AppConfig body, Workspace w) =>
{
    body.Repos = body.Repos
        .Where(r => !string.IsNullOrWhiteSpace(r.Path))
        .Select(r =>
        {
            r.Path = Path.GetFullPath(r.Path.Trim());
            r.MergeMethod = r.MergeMethod is "merge" or "rebase" ? r.MergeMethod : "squash";
            return r;
        })
        .DistinctBy(r => r.Path.ToLowerInvariant())
        .ToList();
    foreach (var r in body.Repos)
    {
        r.BuildCommand = string.IsNullOrWhiteSpace(r.BuildCommand) ? null : r.BuildCommand.Trim();
        r.TestCommand = string.IsNullOrWhiteSpace(r.TestCommand) ? null : r.TestCommand.Trim();
    }
    body.CheckTimeoutMinutes = Math.Clamp(body.CheckTimeoutMinutes, 1, 24 * 60);
    body.Reviews ??= new ReviewConfig();
    body.Reviews.TimeoutMinutes = Math.Clamp(body.Reviews.TimeoutMinutes, 1, 240);
    body.Reviews.ClaudePath = string.IsNullOrWhiteSpace(body.Reviews.ClaudePath) ? null : body.Reviews.ClaudePath.Trim().Trim('"');
    body.Reviews.Model = string.IsNullOrWhiteSpace(body.Reviews.Model) ? null : body.Reviews.Model.Trim();
    body.Reviews.Instructions = string.IsNullOrWhiteSpace(body.Reviews.Instructions) ? null : body.Reviews.Instructions.Trim();
    if (body.Reviews.MaxBudgetUsd is <= 0) body.Reviews.MaxBudgetUsd = null;
    body.Excluded = body.Excluded.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
    if (body.TicketPrefixes.Count == 0) body.TicketPrefixes = ["PROJ"];
    w.ConfigStore.Save(body);
    return Results.Ok(body);
});

api.MapPost("/config/discover", (DiscoverRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Folder) || !Directory.Exists(req.Folder)) return Results.BadRequest(new { error = "Folder not found" });
    static bool IsRepo(string d) => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"));
    var found = new List<string>();
    if (IsRepo(req.Folder)) found.Add(Path.GetFullPath(req.Folder));
    foreach (var d in Directory.EnumerateDirectories(req.Folder).OrderBy(d => d))
        if (IsRepo(d)) found.Add(Path.GetFullPath(d));
    return Results.Ok(found);
});

api.MapPost("/config/excluded-preview", (ExcludedPreviewRequest req, Workspace w) =>
{
    var m = new ExcludeMatcher(req.Globs ?? [],
        req.UseClaudeHook ? ClaudeHookExclusions.Load(req.ClaudeSettingsPath ?? w.Config.ClaudeSettingsPath).Patterns : null);
    return w.Snapshots.Select(s => new
    {
        repo = s.Name,
        matches = s.Changes.Where(c => m.IsExcluded(c.Path)).Select(c => c.Path).ToList(),
        others = s.Changes.Where(c => !m.IsExcluded(c.Path)).Select(c => c.Path).ToList(),
    });
});

// what "Build & test" would run in each configured repo when no command is set (shown as the default in Settings)
api.MapGet("/config/checks", (Workspace w) => w.Config.Repos.Select(r =>
{
    var (build, test) = CheckDetector.Describe(r);
    return new { path = r.Path, build, test };
}));

// ---- data ----
api.MapGet("/archive", (Workspace w) => w.Archive.All().OrderByDescending(a => a.CompletedAt));

// full output of one command of the ticket's latest "Build & test" (the path comes from the stored result, never the request)
api.MapGet("/checks/log", (string key, string repo, int index, Workspace w) =>
{
    var phase = w.Checks.Get(key)?.Repos
        .FirstOrDefault(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase))?.Phases.ElementAtOrDefault(index);
    if (phase?.LogFile is null || !File.Exists(phase.LogFile)) return Results.Text("(no log for this step)");
    using var fs = new FileStream(phase.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    const int max = 2_000_000;
    if (fs.Length > max) fs.Seek(-max, SeekOrigin.End);
    var text = new StreamReader(fs).ReadToEnd();
    return Results.Text(fs.Length > max ? "… (start of the log cut off)\n" + text : text);
});
api.MapGet("/journal", (string? key, Workspace w) => w.Journal.Read(key));

api.MapGet("/backups", async (Workspace w) =>
{
    var trash = w.Trash.All();
    var result = new List<BackupView>();
    foreach (var s in w.Snapshots.Where(s => s.Ok))
    {
        var g = new GitRepo(w.Git, s.Path) { ReadOnly = true };
        foreach (var b in await GitOps.ListRefsAsync(g, GitOps.RefRoot))
        {
            var isTrash = b.Ref.StartsWith(GitOps.TrashRefPrefix);
            var t = trash.FirstOrDefault(x => x.Ref == b.Ref && x.RepoPath == s.Path);
            var key = t?.Key ?? b.Ref[GitOps.TrashRefPrefix.Length..].Split('/')[0];
            result.Add(isTrash
                ? new BackupView(s.Name, "branch", b.Ref, b.Sha, t?.At ?? b.Date, $"{key} — {t?.Description ?? b.Subject}", $"Deleted branch wip/{key}")
                : new BackupView(s.Name, "files", b.Ref, b.Sha, b.Date, b.Subject.Replace("ticket-flow backup: ", ""), b.Body));
        }
    }
    return result.OrderByDescending(x => x.Date);
});

api.MapGet("/diff", async (string repo, string path, string? key, Workspace w) =>
{
    var s = w.Snapshot(repo);
    if (s is null) return Results.NotFound();
    var g = new GitRepo(w.Git, s.Path) { ReadOnly = true };
    var working = s.Changes.FirstOrDefault(c => c.Path == path);
    var wip = key is null ? null : s.Wip(key);
    GitResult r;
    if (working is not null && (wip is null || wip.IsCurrent))
    {
        if (working.Kind == ChangeKind.Untracked)
            r = await g.TryAsync("diff", "--no-color", "--no-index", "--", "/dev/null", path);
        else if (s.HeadSha is null)
            r = await g.TryAsync("diff", "--no-color", "--cached", "--", path);
        else
            r = await g.TryAsync(["diff", "--no-color", "HEAD", "--", .. working.Paths]);
    }
    else if (wip?.MergeBase is not null)
        r = await g.TryAsync("diff", "--no-color", wip.MergeBase, wip.Sha, "--", path);
    else return Results.Text("(no diff available)");
    var text = r.StdOut.Length > 400_000 ? r.StdOut[..400_000] + "\n… (truncated)" : r.StdOut;
    return Results.Text(text.Length == 0 ? "(no textual changes)" : text);
});

api.MapPost("/open", (OpenRequest req, Workspace w) =>
{
    var s = w.Snapshot(req.Repo);
    if (s is null) return Results.NotFound();
    var target = req.Path is null ? s.Path : Path.GetFullPath(Path.Combine(s.Path, req.Path));
    if (!target.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
    try
    {
        switch (req.With)
        {
            case "code":
                // `code` is a .cmd shim on Windows, which only cmd.exe can start
                Process.Start(OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", ["/c", "code", target]) { CreateNoWindow = true, UseShellExecute = false }
                    : new ProcessStartInfo("code", [target]) { UseShellExecute = false });
                break;
            case "terminal":
                if (OperatingSystem.IsWindows())
                {
                    try { Process.Start(new ProcessStartInfo("wt.exe", ["-d", s.Path]) { UseShellExecute = true }); }
                    catch { Process.Start(new ProcessStartInfo("powershell.exe", ["-NoExit", "-Command", $"Set-Location -LiteralPath '{s.Path}'"]) { UseShellExecute = true }); }
                }
                else if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo("open", ["-a", "Terminal", s.Path]) { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("x-terminal-emulator") { WorkingDirectory = s.Path, UseShellExecute = false });
                break;
            default:
                var folder = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
                Process.Start(OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("explorer.exe", [folder]) { UseShellExecute = true }
                    : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [folder]) { UseShellExecute = false });
                break;
        }
        return Results.NoContent();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- PR reviews (other people's PRs, reviewed by Claude Code) ----
api.MapGet("/reviews", (PrReviewService r) => r.View());

api.MapPost("/reviews/poll", async (ReviewPollRequest req, PrReviewService r, CancellationToken ct) =>
{
    try
    {
        await r.PollAsync(string.IsNullOrWhiteSpace(req.Repo) ? null : req.Repo, ct);
        return Results.Ok(r.View());
    }
    catch (OpBlockedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/reviews/run", (ReviewPrRequest req, PrReviewService r) =>
{
    if (!r.View().Repos.SelectMany(x => x.Prs).Any(p => p.Key == req.Pr)) return Results.NotFound(new { error = $"{req.Pr} isn't an open PR in a watched repo" });
    return r.Enqueue(req.Pr) ? Results.NoContent() : Results.Conflict(new { error = "Already queued or running" });
});

// review of your own in-flight ticket, across all of its repos (repo = where it was started from; reviewed first)
api.MapPost("/reviews/ticket", (TicketReviewRequest req, PrReviewService r) =>
{
    try { return r.EnqueueTicket(req.Key, req.Repo) ? Results.NoContent() : Results.Conflict(new { error = "Already queued or running" }); }
    catch (OpBlockedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/reviews/cancel", (ReviewPrRequest req, PrReviewService r) =>
    r.Cancel(req.Pr) ? Results.NoContent() : Results.Conflict(new { error = "Not queued or running" }));

// readable transcript of one review run (the path comes from the stored result, never the request)
api.MapGet("/reviews/log", (string id, PrReviewService r) =>
{
    var res = r.Store.Get(id);
    return Results.Text(res?.LogFile is { } f && File.Exists(f) ? ReviewLog.Render(f) : "(no log for this review)");
});

// ---- operations ----
api.MapPost("/ops/{kind}/preview", async (string kind, JsonElement body, OpRunner r) =>
{
    try { return Results.Ok(await r.PreviewAsync(kind, body)); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/ops/{kind}/run", (string kind, JsonElement body, OpRunner r) =>
{
    try { return Results.Ok(r.Start(kind, body)); }
    catch (Exception ex) { return Results.Conflict(new { error = ex.Message }); }
});

api.MapGet("/ops/last", (OpRunner r) => r.Last is null ? Results.NoContent() : Results.Ok(r.Last));

api.MapPost("/ops/cancel", (OpRunner r) =>
    r.Cancel() ? Results.NoContent() : Results.Conflict(new { error = "Nothing that can be cancelled is running." }));

app.MapFallback(async ctx =>
{
    var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
    if (File.Exists(index))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(index);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("UI not built. Run: cd web && npm install && npm run build");
    }
});

var url = $"http://127.0.0.1:{cfg.Port}/auth?t={token}";
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine($"  Ticket Flow is running → {url}");
    Console.WriteLine($"  Config: {paths.ConfigFile}");
    Console.WriteLine();
    if (cfg.OpenBrowserOnStart && Environment.GetEnvironmentVariable("TICKETFLOW_NO_BROWSER") is null)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }
});

app.Run();

record DiscoverRequest(string Folder);
record ReviewPollRequest(string? Repo);
record ReviewPrRequest(string Pr);
record TicketReviewRequest(string Key, string? Repo);
record ExcludedPreviewRequest(List<string>? Globs, bool UseClaudeHook, string? ClaudeSettingsPath);
record OpenRequest(string Repo, string? Path, string? With);
record BackupView(string Repo, string Kind, string Ref, string Sha, DateTimeOffset Date, string Title, string Detail);
