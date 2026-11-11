using System.Text;
using System.Text.Json.Nodes;
using TicketFlow.Core.GitHub;

namespace TicketFlow.Core.Reviews;

public sealed record ReviewJobView(string Pr, string Trigger, DateTimeOffset StartedAt, List<string> Progress);

public sealed record RemotePrView(
    string Key,
    string Repo,
    int Number,
    string Url,
    string FilesUrl,
    string Title,
    string Author,
    bool AuthorIsBot,
    bool IsDraft,
    string HeadRef,
    string BaseRef,
    string HeadSha,
    int Additions,
    int Deletions,
    int ChangedFiles,
    DateTimeOffset UpdatedAt,
    string? MyReviewState,
    DateTimeOffset? MyReviewAt,
    string? MyReviewUrl,
    bool NewCommitsSinceMyReview,
    int OpenThreads,
    /// <summary>reviewing | queued | reviewed | failed | draft | skipped | pending</summary>
    string Status,
    string StatusText,
    List<PrReviewResult> Reviews);

/// <summary>Review state of one of your in-flight tickets. Status: reviewing | queued | reviewed | failed | none.</summary>
public sealed record TicketReviewView(string Key, string Status, bool Stale, List<string> StaleRepos, List<PrReviewResult> Reviews);

public sealed record ReviewRepoView(string Name, string? Slug, DateTimeOffset? LastPoll, bool Polling, string? Error, List<RemotePrView> Prs);

public sealed record ReviewsState(
    bool AnyEnabled,
    string? ClaudePath,
    string? ClaudeError,
    List<ReviewRepoView> Repos,
    ReviewJobView? Running,
    List<string> Queue,
    List<TicketReviewView> Tickets);

/// <summary>
/// Watches the repos that opted in for other people's open PRs and reviews each new head commit with Claude Code,
/// one review at a time. Independent of the op runner: reviews never touch a working copy, so they don't block anything.
/// </summary>
public sealed class PrReviewService
{
    private sealed class RepoState
    {
        public RepoRef? Ref;
        public List<RemotePr> Prs = [];
        public DateTimeOffset? LastPoll;
        public string? Error;
        public bool Polling;
    }

    private sealed record Job(string Pr, string Trigger, string? Focus = null);

    private const int ProgressLines = 60;

    private readonly Workspace _w;
    private readonly PrReviewStore _store;
    private readonly IClaudeRunner _claude;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RepoState> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Job> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private Job? _running;
    private DateTimeOffset _runningSince;
    private volatile bool _stopping;
    private CancellationTokenSource? _runningCts;
    private readonly List<string> _progress = [];
    private (string? Configured, string? Path, string? Error, DateTime At)? _located;

    public PrReviewService(Workspace w, PrReviewStore store, IClaudeRunner claude)
    {
        _w = w;
        _store = store;
        _claude = claude;
        w.Changed += PruneTickets;
    }

    /// <summary>Drops ticket reviews once their ticket is completed or discarded (only after a clean scan of every repo).</summary>
    private void PruneTickets()
    {
        var snaps = _w.Snapshots;
        if (snaps.Count != _w.Config.Repos.Count || snaps.Any(s => !s.Ok)) return;
        if (_store.PruneTickets(snaps.SelectMany(s => s.Wips.Select(w => w.Key)).Distinct().ToList())) Raise();
    }

    public PrReviewStore Store => _store;

    /// <summary>True while a Ticket Flow operation runs; ticket snapshots wait for it so no branch switches under them.</summary>
    public Func<bool> OpBusy { get; set; } = () => false;

    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    public bool AnyEnabled => _w.Config.Repos.Any(r => r.ReviewPrs);

    private (string? Path, string? Error) Claude()
    {
        var configured = _w.Config.Reviews.ClaudePath;
        lock (_lock)
            if (_located is { } l && l.Configured == configured && DateTime.UtcNow - l.At < TimeSpan.FromMinutes(1))
                return (l.Path, l.Error);
        var (path, error) = ClaudeCli.Locate(configured);
        lock (_lock)
            _located = (configured, path, error, DateTime.UtcNow);
        return (path, error);
    }

    // ---------------------------------------------------------------- polling

    /// <summary>Fetches the open PRs of every opted-in repo (or just <paramref name="repoName"/>) and queues the ones needing a review.</summary>
    public async Task PollAsync(string? repoName = null, CancellationToken ct = default)
    {
        var cfg = _w.Config;
        var enabled = cfg.Repos.Where(r => r.ReviewPrs).ToList();
        lock (_lock)
            foreach (var gone in _repos.Keys.Where(k => !enabled.Any(r => Same(r.DisplayName, k))).ToList())
                _repos.Remove(gone);
        var targets = enabled.Where(r => repoName is null || Same(r.DisplayName, repoName)).ToList();
        if (repoName is not null && targets.Count == 0)
            throw new OpBlockedException($"PR reviews aren't enabled for {repoName}");
        if (targets.Count == 0)
        {
            Raise();
            return;
        }

        await _pollGate.WaitAsync(ct);
        try
        {
            var refs = new Dictionary<string, RepoRef>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (var rc in targets)
                {
                    var st = State(rc.DisplayName);
                    st.Polling = true;
                    var snap = _w.Snapshots.FirstOrDefault(s => Same(s.Path, rc.Path));
                    if (snap?.GitHubOwner is null || snap.GitHubRepo is null)
                    {
                        st.Error = snap is null ? "Not scanned yet" : "origin isn't a GitHub repo";
                        st.Polling = false;
                        continue;
                    }
                    st.Ref = new RepoRef(snap.GitHubOwner, snap.GitHubRepo);
                    refs[rc.DisplayName] = st.Ref;
                }
            Raise();
            if (refs.Count == 0) return;

            try
            {
                var viewer = await _w.GitHub.GetViewerAsync(ct);
                var found = await _w.GitHub.GetOpenPrsAsync(refs.Values.ToList(), viewer, ct);
                var now = DateTimeOffset.Now;
                foreach (var (name, rr) in refs)
                {
                    var prs = found.GetValueOrDefault(rr) ?? [];
                    lock (_lock)
                    {
                        var st = State(name);
                        st.Prs = prs;
                        st.LastPoll = now;
                        st.Error = null;
                        _queue.RemoveAll(j => j.Pr.StartsWith($"{rr.Owner}/{rr.Repo}#", StringComparison.Ordinal) && prs.All(p => p.Key != j.Pr));
                    }
                    _store.Prune(rr, prs.Select(p => p.Key).ToList());
                    foreach (var pr in prs.Where(p => Visible(p, cfg.Reviews.BotPrs)))
                    {
                        var need = ReviewPrompt.Need(pr, _store.For(pr.Key), cfg.Reviews.BotPrs);
                        if (need.Run) Enqueue(pr.Key, need.Trigger, front: false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                    foreach (var name in refs.Keys)
                        State(name).Error = ex.Message;
            }
        }
        finally
        {
            lock (_lock)
                foreach (var rc in targets)
                    if (_repos.TryGetValue(rc.DisplayName, out var st)) st.Polling = false;
            _pollGate.Release();
            Raise();
        }
    }

    private RepoState State(string name)
    {
        if (!_repos.TryGetValue(name, out var st)) _repos[name] = st = new RepoState();
        return st;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private RemotePr? FindPr(string key)
    {
        lock (_lock)
            return _repos.Values.SelectMany(r => r.Prs).FirstOrDefault(p => p.Key == key);
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Queues a review; a manual request jumps the queue. False when it's already queued or running.</summary>
    public bool Enqueue(string prKey, string trigger = "manual", bool front = true, string? focus = null)
    {
        lock (_lock)
        {
            if (_running?.Pr == prKey || _queue.Any(j => j.Pr == prKey)) return false;
            if (front) _queue.Insert(0, new Job(prKey, trigger, focus));
            else _queue.Add(new Job(prKey, trigger, focus));
        }
        _signal.Release();
        Raise();
        return true;
    }

    /// <summary>
    /// Queues a review of your own in-flight ticket across all of its repos; <paramref name="focusRepo"/> is the repo it
    /// was started from and is reviewed first. False when it's already queued or running.
    /// </summary>
    public bool EnqueueTicket(string key, string? focusRepo)
    {
        var legs = _w.Legs(key);
        if (legs.Count == 0) throw new OpBlockedException($"{key} has no local wip branch any more");
        if (focusRepo is not null && !legs.Any(l => Same(l.Repo.Name, focusRepo)))
            throw new OpBlockedException($"{key} has no branch in {focusRepo}");
        return Enqueue(PrReviewStore.TicketPrefix + key, "manual", front: true, focus: focusRepo);
    }

    /// <summary>Stops the running review of this PR, or takes it off the queue.</summary>
    public bool Cancel(string prKey)
    {
        lock (_lock)
        {
            if (_running?.Pr == prKey)
            {
                _runningCts?.Cancel();
                return true;
            }
            var removed = _queue.RemoveAll(j => j.Pr == prKey) > 0;
            if (removed) Raise();
            return removed;
        }
    }

    /// <summary>Runs queued reviews one at a time until <paramref name="stop"/>.</summary>
    public async Task RunWorkerAsync(CancellationToken stop)
    {
        stop.Register(() => _stopping = true);
        while (!stop.IsCancellationRequested)
        {
            await _signal.WaitAsync(stop);
            Job? job;
            lock (_lock)
            {
                if (_queue.Count == 0) continue;
                job = _queue[0];
                _queue.RemoveAt(0);
                _running = job;
                _runningSince = DateTimeOffset.Now;
                _runningCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                _progress.Clear();
            }
            Raise();
            try
            {
                await ReviewAsync(job, _runningCts.Token);
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // a failure outside the claude run (disk, store) must not stop the worker
            }
            finally
            {
                lock (_lock)
                {
                    _running = null;
                    _runningCts?.Dispose();
                    _runningCts = null;
                }
                Raise();
            }
        }
    }

    private void Progress(string line)
    {
        lock (_lock)
        {
            _progress.Add(line);
            if (_progress.Count > ProgressLines) _progress.RemoveAt(0);
        }
        Raise();
    }

    // ---------------------------------------------------------------- one review

    private async Task ReviewAsync(Job job, CancellationToken ct)
    {
        if (job.Pr.StartsWith(PrReviewStore.TicketPrefix, StringComparison.Ordinal))
        {
            await TicketReviewAsync(job, ct);
            return;
        }
        var pr = FindPr(job.Pr);
        if (pr is null) return; // closed or merged since it was queued
        var history = _store.For(pr.Key);
        var since = ReviewPrompt.SinceSha(pr, history);
        var previous = history.FirstOrDefault(h => h.Status == "ok");
        var started = DateTimeOffset.Now;
        var workDir = _store.WorkDir(pr);
        Directory.CreateDirectory(workDir);
        var logFile = Path.Combine(workDir, $"{started:yyyyMMdd-HHmmss}-{Short(pr.HeadSha)}.jsonl");

        PrReviewResult Fail(string error, ClaudeRunResult? r = null) => new()
        {
            Pr = pr.Key, HeadSha = pr.HeadSha, SinceSha = since, Trigger = job.Trigger, At = started,
            Seconds = (DateTimeOffset.Now - started).TotalSeconds, Status = "failed", Error = error,
            CostUsd = r?.CostUsd, Turns = r?.Turns, Model = r?.Model, LogFile = File.Exists(logFile) ? logFile : null,
        };

        PrReviewResult result;
        try
        {
            result = await RunClaudeAsync(pr, job, since, previous, workDir, logFile, started, Fail, ct);
        }
        catch (OperationCanceledException) when (!_stopping)
        {
            // cancelled by the user: recorded, so this commit isn't queued again automatically
            result = Fail("Cancelled");
        }

        _store.Add(result);
        if (result.Error != "Cancelled") _w.Notify(Notification(pr, result));
    }

    private async Task<PrReviewResult> RunClaudeAsync(RemotePr pr, Job job, string? since, PrReviewResult? previous, string workDir,
        string logFile, DateTimeOffset started, Func<string, ClaudeRunResult?, PrReviewResult> fail, CancellationToken ct)
    {
        var cfg = _w.Config.Reviews;
        var (exe, exeError) = Claude();
        if (exe is null) return fail(exeError ?? "Claude Code not found", null);

        var prompt = ReviewPrompt.Build(pr, since, previous, cfg.Instructions);
        await File.WriteAllTextAsync(Path.Combine(workDir, ReviewPrompt.Script), ReviewPrompt.BuildScript(pr, since), new UTF8Encoding(false), ct);
        await File.WriteAllTextAsync(Path.Combine(workDir, "prompt.md"), prompt, new UTF8Encoding(false), ct);
        string token;
        try
        {
            token = await _w.GitHub.GetTokenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fail(ex.Message, null);
        }

        Progress($"Reviewing {pr.Key} at {Short(pr.HeadSha)}" + (since is null ? "" : $" (changes since {Short(since)})"));
        var env = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = token,
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["GH_PAGER"] = "cat",
            ["PAGER"] = "cat",
            ["NO_COLOR"] = "1",
        };
        var run = await _claude.RunAsync(
            new ClaudeRunRequest(exe, workDir, prompt, cfg.Model, cfg.MaxBudgetUsd, TimeSpan.FromMinutes(Math.Clamp(cfg.TimeoutMinutes, 1, 240)), env, logFile),
            Progress, ct);
        if (!run.Ok || run.Output is null) return fail(run.Error ?? "Review failed", run);
        return FromOutput(run.Output) with
        {
            Pr = pr.Key, HeadSha = pr.HeadSha, SinceSha = since, Trigger = job.Trigger, At = started,
            Seconds = (DateTimeOffset.Now - started).TotalSeconds, CostUsd = run.CostUsd, Turns = run.Turns, Model = run.Model, LogFile = logFile,
        };
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    private async Task TicketReviewAsync(Job job, CancellationToken ct)
    {
        var key = job.Pr[PrReviewStore.TicketPrefix.Length..];
        if (OpBusy()) Progress("Waiting for the running action to finish");
        while (OpBusy()) await Task.Delay(500, ct);
        var legs = TicketReview.Order(_w.Legs(key), job.Focus);
        if (legs.Count == 0) return; // completed or discarded since it was queued
        var cfg = _w.Config;
        var history = _store.For(job.Pr);
        var previous = history.FirstOrDefault(h => h.Status == "ok");
        var started = DateTimeOffset.Now;
        var workDir = _store.TicketWorkDir(key);
        Directory.CreateDirectory(workDir);
        var logFile = Path.Combine(workDir, $"{started:yyyyMMdd-HHmmss}.jsonl");
        List<TicketReviewLeg> snap = [];

        PrReviewResult Fail(string error, ClaudeRunResult? r = null) => new()
        {
            Pr = job.Pr, HeadSha = snap.Count > 0 ? TicketReview.Fingerprint(snap) : "", Focus = job.Focus, Trigger = job.Trigger, At = started,
            Seconds = (DateTimeOffset.Now - started).TotalSeconds, Status = "failed", Error = error,
            Repos = snap.Select(l => new ReviewedRepo(l.Repo, l.Fingerprint, l.Tree)).ToList(),
            CostUsd = r?.CostUsd, Turns = r?.Turns, Model = r?.Model, LogFile = File.Exists(logFile) ? logFile : null,
        };

        PrReviewResult result;
        try
        {
            var (exe, exeError) = Claude();
            if (exe is null)
            {
                result = Fail(exeError ?? "Claude Code not found");
            }
            else
            {
                Progress($"Snapshotting {key} in {string.Join(", ", legs.Select(l => l.Repo.Name))}");
                snap = await TicketReview.SnapshotAsync(_w.Git, legs, workDir, ct);
                var wip = legs[0].Wip;
                var prompt = TicketReview.BuildPrompt(key, wip.Description ?? "", wip.EffectiveType, legs.Count > 1 ? job.Focus : null, snap,
                    previous, cfg.Reviews.Instructions);
                await File.WriteAllTextAsync(Path.Combine(workDir, "prompt.md"), prompt, new UTF8Encoding(false), ct);
                var deny = TicketReview.DenyRules(legs.Select(l => l.Repo.Path), cfg.Excluded, cfg.ClaudeSettingsPath);
                Progress($"Reviewing {key} ({snap.Sum(l => l.Files)} changed files)");
                var run = await _claude.RunAsync(
                    new ClaudeRunRequest(exe, workDir, prompt, cfg.Reviews.Model, cfg.Reviews.MaxBudgetUsd,
                        TimeSpan.FromMinutes(Math.Clamp(cfg.Reviews.TimeoutMinutes, 1, 240)), new Dictionary<string, string> { ["NO_COLOR"] = "1" }, logFile)
                    {
                        Mode = ReviewMode.Local,
                        AddDirs = legs.Select(l => l.Repo.Path).ToList(),
                        Deny = deny,
                    },
                    Progress, ct);
                result = run.Ok && run.Output is not null
                    ? FromOutput(run.Output) with
                    {
                        Pr = job.Pr, HeadSha = TicketReview.Fingerprint(snap), Focus = job.Focus, Trigger = job.Trigger, At = started,
                        Seconds = (DateTimeOffset.Now - started).TotalSeconds, CostUsd = run.CostUsd, Turns = run.Turns, Model = run.Model,
                        LogFile = logFile, Repos = snap.Select(l => new ReviewedRepo(l.Repo, l.Fingerprint, l.Tree)).ToList(),
                    }
                    : Fail(run.Error ?? "Review failed", run);
            }
        }
        catch (OperationCanceledException) when (!_stopping)
        {
            result = Fail("Cancelled");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Fail(ex.Message);
        }

        _store.Add(result);
        if (result.Error == "Cancelled") return;
        var title = $"Review · {key}";
        var high = result.Concerns.Count(c => c.Severity == "high");
        _w.Notify(result.Status != "ok"
            ? new Notification(title, $"Review failed: {result.Error}", "bad", null)
            : new Notification(title, $"{VerdictText(result.Verdict)} · {result.Concerns.Count} concern{(result.Concerns.Count == 1 ? "" : "s")}" +
                                      (high > 0 ? $" ({high} high)" : ""),
                result.Verdict switch { "looks-good" => "ok", "needs-changes" => "bad", _ => "warn" }, null));
    }

    /// <summary>Maps Claude's structured output onto a result (identity fields are filled in by the caller).</summary>
    public static PrReviewResult FromOutput(JsonNode o)
    {
        static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static int? I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        return new PrReviewResult
        {
            Pr = "", HeadSha = "",
            Summary = S(o["summary"]),
            Verdict = S(o["verdict"]),
            Risk = S(o["risk"]),
            Concerns = (o["concerns"] as JsonArray ?? [])
                .Where(c => c is not null)
                .Select(c => new ReviewConcern(S(c!["severity"]) ?? "low", S(c["title"]) ?? "", S(c["detail"]) ?? "", S(c["file"]), I(c["line"]), S(c["repo"])))
                .OrderBy(c => c.Severity switch { "high" => 0, "medium" => 1, _ => 2 })
                .ToList(),
            PreviousRequests = (o["previousRequests"] as JsonArray ?? [])
                .Where(c => c is not null)
                .Select(c => new RequestCheck(S(c!["request"]) ?? "", S(c["status"]) ?? "unclear", S(c["note"]) ?? ""))
                .ToList(),
        };
    }

    private static Notification Notification(RemotePr pr, PrReviewResult r)
    {
        var title = $"Review · {pr.Repo} #{pr.Number}";
        if (r.Status != "ok") return new Notification(title, $"Review failed: {r.Error}", "bad", pr.FilesUrl);
        var high = r.Concerns.Count(c => c.Severity == "high");
        var open = r.PreviousRequests.Count(p => p.Status != "addressed");
        var body = $"{VerdictText(r.Verdict)} · {r.Concerns.Count} concern{(r.Concerns.Count == 1 ? "" : "s")}" +
                   (high > 0 ? $" ({high} high)" : "") +
                   (r.PreviousRequests.Count > 0 ? $" · {r.PreviousRequests.Count - open}/{r.PreviousRequests.Count} of your requests addressed" : "");
        var tone = r.Verdict switch { "looks-good" => "ok", "needs-changes" => "bad", _ => "warn" };
        return new Notification(title, body, tone, pr.FilesUrl);
    }

    private static string VerdictText(string? v) => v switch
    {
        "looks-good" => "Looks good",
        "minor-issues" => "Minor issues",
        "needs-changes" => "Needs changes",
        _ => "Reviewed",
    };

    // ---------------------------------------------------------------- view

    public ReviewsState View()
    {
        var cfg = _w.Config;
        var (claudePath, claudeError) = Claude();
        lock (_lock)
        {
            var repos = cfg.Repos.Where(r => r.ReviewPrs).Select(rc =>
            {
                _repos.TryGetValue(rc.DisplayName, out var st);
                var prs = (st?.Prs ?? []).Where(p => Visible(p, cfg.Reviews.BotPrs)).Select(p => PrView(rc.DisplayName, p, cfg.Reviews.BotPrs)).ToList();
                return new ReviewRepoView(rc.DisplayName, st?.Ref is { } r ? $"{r.Owner}/{r.Repo}" : null, st?.LastPoll, st?.Polling ?? false, st?.Error, prs);
            }).ToList();
            var running = _running is { } j ? new ReviewJobView(j.Pr, j.Trigger, _runningSince, [.. _progress]) : null;
            return new ReviewsState(repos.Count > 0, claudePath, claudeError, repos, running, _queue.Select(q => q.Pr).ToList(), TicketViews());
        }
    }

    private List<TicketReviewView> TicketViews()
    {
        var keys = _store.All().Select(r => r.Pr)
            .Concat(_queue.Select(q => q.Pr))
            .Concat(_running is { } j ? [j.Pr] : [])
            .Where(k => k.StartsWith(PrReviewStore.TicketPrefix, StringComparison.Ordinal))
            .Distinct();
        var list = new List<TicketReviewView>();
        foreach (var pr in keys)
        {
            var key = pr[PrReviewStore.TicketPrefix.Length..];
            var legs = _w.Legs(key);
            if (legs.Count == 0) continue;
            var history = _store.For(pr);
            var status = _running?.Pr == pr ? "reviewing"
                : _queue.Any(q => q.Pr == pr) ? "queued"
                : history.Count == 0 ? "none"
                : history[0].Status == "ok" ? "reviewed" : "failed";
            var stale = history.Count > 0 ? TicketReview.StaleRepos(history[0], legs) : [];
            list.Add(new TicketReviewView(key, status, stale.Count > 0, stale, history));
        }
        return list;
    }

    /// <summary>Hidden bot PRs are still fetched (so switching the setting shows them at once) but never listed or queued.</summary>
    private static bool Visible(RemotePr p, BotPrMode bots) => !p.AuthorIsBot || bots != BotPrMode.Hidden;

    private RemotePrView PrView(string repo, RemotePr p, BotPrMode bots)
    {
        var history = _store.For(p.Key);
        var latest = history.FirstOrDefault();
        string status, text;
        if (_running?.Pr == p.Key) (status, text) = ("reviewing", "Reviewing…");
        else if (_queue.Any(q => q.Pr == p.Key)) (status, text) = ("queued", "Queued");
        else if (latest?.HeadSha == p.HeadSha) (status, text) = latest.Status == "ok" ? ("reviewed", "Reviewed") : ("failed", "Review failed");
        else if (ReviewPrompt.Need(p, history, bots) is { Run: false, Reason: { } why }) (status, text) = (p.IsDraft ? "draft" : "skipped", why);
        else if (latest is not null) (status, text) = ("pending", "New commits — review on next poll");
        else (status, text) = ("pending", "Not reviewed yet");

        var my = p.MyReview;
        return new RemotePrView(
            p.Key, repo, p.Number, p.Url, p.FilesUrl, p.Title, p.Author, p.AuthorIsBot, p.IsDraft, p.HeadRef, p.BaseRef, p.HeadSha,
            p.Additions, p.Deletions, p.ChangedFiles, p.UpdatedAt,
            my?.State, my?.At, my?.Url,
            my?.CommitSha is { Length: > 0 } c && c != p.HeadSha,
            p.MyThreads.Count(t => !t.IsResolved),
            status, text, history);
    }
}
