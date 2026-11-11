using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using TicketFlow.Core;
using TicketFlow.Core.Ops;
using TicketFlow.Core.Reviews;

namespace TicketFlow.Server;

/// <summary>Server-sent events fan-out: state snapshots, op progress and notifications.</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private readonly Workspace _w;
    private int _statePending;
    private int _reviewsPending;
    private DateTime _lastVisible = DateTime.UtcNow;

    public EventHub(Workspace w, OpRunner runner, PrReviewService reviews)
    {
        _w = w;
        Reviews = reviews;
        w.Changed += ScheduleState;
        reviews.Changed += ScheduleReviews;
        runner.Progress += p => Broadcast("op", p);
        w.Notified += n => Broadcast("notify", n);
    }

    public PrReviewService Reviews { get; }

    public bool AnyVisible => _clients.Count > 0 && DateTime.UtcNow - _lastVisible < TimeSpan.FromMinutes(3);
    public void MarkVisible() => _lastVisible = DateTime.UtcNow;

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        _clients[id] = ch;
        MarkVisible();
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public static string Format(string evt, object payload) =>
        $"event: {evt}\ndata: {JsonSerializer.Serialize(payload, Json.Compact)}\n\n";

    public void Broadcast(string evt, object payload)
    {
        if (_clients.IsEmpty) return;
        string msg;
        lock (payload) msg = Format(evt, payload);
        foreach (var ch in _clients.Values) ch.Writer.TryWrite(msg);
    }

    private void ScheduleReviews()
    {
        if (Interlocked.Exchange(ref _reviewsPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            Interlocked.Exchange(ref _reviewsPending, 0);
            try { Broadcast("reviews", Reviews.View()); } catch { /* ignore */ }
        });
    }

    private void ScheduleState()
    {
        if (Interlocked.Exchange(ref _statePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Interlocked.Exchange(ref _statePending, 0);
            try { Broadcast("state", _w.State()); } catch { /* ignore */ }
        });
    }
}

/// <summary>Watches every configured repo and rescans it (debounced) when files or git refs change.</summary>
public sealed class RepoWatcher(Workspace w, OpRunner runner, ILogger<RepoWatcher> log) : BackgroundService
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Ignored =
    [
        @"\bin\", @"\obj\", @"\node_modules\", @"\.vs\", @"\.idea\", @"\packages\",
        @"\.git\objects\", @"\.git\logs\", @"\.git\lfs\", @"\.git\worktrees\", "index.lock", @"\TestResults\",
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        w.ConfigStore.Changed += _ => Rewire();
        Rewire();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            if (!runner.Busy) await SafeRescanAll();
        }
    }

    private async Task SafeRescanAll()
    {
        try { await w.RescanAllAsync(); }
        catch (Exception ex) { log.LogWarning(ex, "rescan failed"); }
    }

    private void Rewire()
    {
        lock (_watchers)
        {
            foreach (var fw in _watchers) fw.Dispose();
            _watchers.Clear();
            foreach (var repo in w.Config.Repos.Where(r => Directory.Exists(r.Path)))
            {
                var fw = new FileSystemWatcher(repo.Path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                var rc = repo;
                FileSystemEventHandler h = (_, e) => OnEvent(rc, e.FullPath);
                fw.Changed += h;
                fw.Created += h;
                fw.Deleted += h;
                fw.Renamed += (_, e) => OnEvent(rc, e.FullPath);
                fw.Error += (_, _) => Debounce(rc);
                fw.EnableRaisingEvents = true;
                _watchers.Add(fw);
            }
        }
    }

    private void OnEvent(RepoConfig rc, string fullPath)
    {
        var root = rc.Path.TrimEnd('\\', '/');
        var rel = (fullPath.Length > root.Length ? fullPath[root.Length..] : fullPath).Replace('/', '\\') + "\\";
        foreach (var ig in Ignored)
            if (rel.Contains(ig, StringComparison.OrdinalIgnoreCase))
                return;
        Debounce(rc);
    }

    private void Debounce(RepoConfig rc)
    {
        var cts = new CancellationTokenSource();
        var old = _pending.AddOrUpdate(rc.Path, cts, (_, prev) =>
        {
            prev.Cancel();
            return cts;
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, cts.Token);
                while (runner.Busy) await Task.Delay(500, cts.Token);
                await w.RescanAsync(rc, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogDebug(ex, "rescan {Repo} failed", rc.Path); }
        });
    }

    public override void Dispose()
    {
        lock (_watchers)
            foreach (var fw in _watchers) fw.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Polls GitHub for PR status: fast while the UI is open, slow in the background. The same tick also polls the
/// repos that opted in to PR reviews for other people's PRs.
/// </summary>
public sealed class PrPoller(Workspace w, OpRunner runner, EventHub hub, PrReviewService reviews, ILogger<PrPoller> log) : BackgroundService
{
    private readonly SemaphoreSlim _poke = new(0, 1);

    public void Poke()
    {
        if (_poke.CurrentCount == 0) _poke.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await w.RescanAllAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            if (!runner.Busy && w.Snapshots.Any(s => s.Wips.Count > 0))
                await w.PollPrsAsync(ct);
            if (reviews.AnyEnabled)
            {
                try { await reviews.PollAsync(null, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "PR review poll failed"); }
            }
            var cfg = w.Config;
            var secs = hub.AnyVisible ? cfg.PollSeconds : cfg.BackgroundPollSeconds;
            await _poke.WaitAsync(TimeSpan.FromSeconds(Math.Max(15, secs)), ct);
        }
    }
}

/// <summary>Runs queued PR reviews one at a time.</summary>
public sealed class ReviewWorker(PrReviewService reviews) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await reviews.RunWorkerAsync(ct); }
        catch (OperationCanceledException) { }
    }
}

public static class AuthToken
{
    public static string LoadOrCreate(AppPaths paths)
    {
        if (File.Exists(paths.TokenFile))
        {
            var t = File.ReadAllText(paths.TokenFile).Trim();
            if (t.Length >= 32) return t;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(paths.TokenFile, token);
        return token;
    }
}
