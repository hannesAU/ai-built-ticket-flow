using System.Text.Json;
using TicketFlow.Core.Git;

namespace TicketFlow.Core.Ops;

public interface IOperation
{
    string Kind { get; }
    Type RequestType { get; }
    Task<OpPreview> PreviewAsync(Workspace w, object request);
    Task RunAsync(OpContext ctx, object request);
    string? KeyOf(object request);

    /// <summary>Long-running ops that stop cleanly at <see cref="OpContext.Cancellation"/> (others can't be cancelled).</summary>
    bool Cancellable { get; }
}

public abstract class Operation<TReq> : IOperation where TReq : class
{
    public abstract string Kind { get; }
    public virtual bool Cancellable => false;
    public Type RequestType => typeof(TReq);
    public abstract Task<OpPreview> PreviewAsync(Workspace w, TReq req);
    public abstract Task RunAsync(OpContext ctx, TReq req);
    public virtual string? KeyOf(TReq req) => null;

    Task<OpPreview> IOperation.PreviewAsync(Workspace w, object request) => PreviewAsync(w, (TReq)request);
    Task IOperation.RunAsync(OpContext ctx, object request) => RunAsync(ctx, (TReq)request);
    string? IOperation.KeyOf(object request) => KeyOf((TReq)request);
}

/// <summary>Execution context: step/log reporting plus helpers to get fresh repo state.</summary>
public sealed class OpContext(Workspace w, OpProgress progress, Action changed, CancellationToken cancellation = default)
{
    /// <summary>Older lines are dropped beyond this (the whole progress is re-sent to the UI on every update).</summary>
    public const int MaxStepLog = 400;

    private OpStepView? _step;
    private DateTime _lastPush = DateTime.MinValue;

    public Workspace W { get; } = w;
    public OpProgress Progress { get; } = progress;
    public AppConfig Config => W.Config;
    public string Tag => Progress.Id;
    public CancellationToken Cancellation { get; } = cancellation;

    public void Step(string repo, string text)
    {
        if (_step is { Status: "running" }) _step.Status = "ok";
        _step = new OpStepView { Repo = repo, Text = text };
        lock (Progress) Progress.Steps.Add(_step);
        changed();
    }

    public void Log(string line)
    {
        if (_step is null) Step("", "Working");
        lock (Progress)
        {
            var log = _step!.Log;
            log.Add(line);
            if (log.Count > MaxStepLog)
            {
                // the first line becomes "… N earlier lines not shown"; trim in chunks so this runs rarely
                var hidden = log[0].StartsWith("… ") && int.TryParse(log[0].Split(' ')[1], out var n) ? n : 0;
                var marker = hidden > 0 ? 1 : 0;
                var drop = log.Count - MaxStepLog + 50;
                log.RemoveRange(0, drop);
                log.Insert(0, $"… {hidden + drop - marker} earlier lines not shown");
            }
        }
        if (DateTime.UtcNow - _lastPush > TimeSpan.FromMilliseconds(250))
        {
            _lastPush = DateTime.UtcNow;
            changed();
        }
    }

    public void Warn(string text)
    {
        lock (Progress) Progress.Warnings.Add(text);
        if (_step is not null) _step.Status = "warn";
        changed();
    }

    /// <summary>
    /// Ends the current step with an explicit status (ok | failed | skipped | warn) without stopping the op;
    /// <paramref name="note"/> is appended to the step's text.
    /// </summary>
    public void EndStep(string status, string? note = null)
    {
        if (_step is null) return;
        lock (Progress)
        {
            if (!string.IsNullOrEmpty(note)) _step.Text += " — " + note;
            _step.Status = status;
        }
        changed();
    }

    public void Link(string url)
    {
        lock (Progress) Progress.Links.Add(url);
        changed();
    }

    public void Finish(bool ok, string? error = null)
    {
        if (_step is { Status: "running" })
        {
            _step.Status = ok ? "ok" : "failed";
            if (error is not null) _step.Log.Add(error);
        }
        changed();
    }

    public GitRepo Git(RepoSnapshot r) => new(W.Git, r.Path, Log);

    public Task<RepoSnapshot> Fresh(RepoSnapshot r) => W.RescanAsync(r.Config);
}

public sealed class OpRunner
{
    private readonly Workspace _w;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IOperation> _ops;

    public OpRunner(Workspace w, IEnumerable<IOperation> ops)
    {
        _w = w;
        _ops = ops.ToDictionary(o => o.Kind, StringComparer.OrdinalIgnoreCase);
    }

    private CancellationTokenSource? _cts;

    public event Action<OpProgress>? Progress;
    public OpProgress? Last { get; private set; }
    public Task LastTask { get; private set; } = Task.CompletedTask;
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>Asks the running op to stop. False when nothing is running or the op can't be cancelled.</summary>
    public bool Cancel()
    {
        var p = _w.RunningOp;
        var cts = _cts;
        if (p is null || !p.Cancellable || cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public IOperation Get(string kind) =>
        _ops.TryGetValue(kind, out var op) ? op : throw new OpBlockedException($"Unknown operation '{kind}'");

    public object ParseRequest(IOperation op, JsonElement body) =>
        body.Deserialize(op.RequestType, Json.Options) ?? throw new OpBlockedException("Missing request body");

    public Task<OpPreview> PreviewAsync(string kind, JsonElement body)
    {
        var op = Get(kind);
        return op.PreviewAsync(_w, ParseRequest(op, body));
    }

    public OpProgress Start(string kind, JsonElement body)
    {
        var op = Get(kind);
        var req = ParseRequest(op, body);
        if (!_gate.Wait(0)) throw new OpBlockedException("Another operation is still running.");

        var progress = new OpProgress { Kind = op.Kind, Title = op.Kind, Key = op.KeyOf(req), Cancellable = op.Cancellable };
        Last = progress;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _w.RunningOp = progress;
        var ctx = new OpContext(_w, progress, () => Progress?.Invoke(progress), cts.Token);

        LastTask = Task.Run(async () =>
        {
            try
            {
                await _w.RescanAllAsync();
                var preview = await op.PreviewAsync(_w, req);
                progress.Title = preview.Title;
                if (preview.Blockers.Count > 0) throw new OpBlockedException(string.Join("\n", preview.Blockers));
                await op.RunAsync(ctx, req);
                ctx.Finish(true);
                await RescanQuietly();
                progress.Status = "succeeded";
            }
            catch (Exception ex)
            {
                var message = ex is OperationCanceledException && cts.IsCancellationRequested ? "Cancelled" : ex.Message;
                ctx.Finish(false, message);
                await RescanQuietly();
                progress.Status = "failed";
                progress.Error = message;
            }
            finally
            {
                progress.EndedAt = DateTimeOffset.Now;
                progress.Cancellable = false;
                _cts = null;
                cts.Dispose();
                _w.RunningOp = null;
                try
                {
                    _w.Journal.Append(new JournalEntry(progress.Id, progress.Kind, progress.Title, progress.Key, progress.Status,
                        progress.Error, progress.StartedAt, progress.EndedAt, progress.Steps));
                }
                catch { /* journal is best effort */ }
                _gate.Release();
                _w.NotifyChanged();
                Progress?.Invoke(progress);
                if (progress.Kind is "push" or "complete" or "discard-ticket")
                    _ = _w.PollPrsAsync();
            }
        });
        return progress;

        async Task RescanQuietly()
        {
            try { await _w.RescanAllAsync(); } catch { /* state refresh is best effort */ }
        }
    }
}

/// <summary>Shared helpers for operations.</summary>
public static class OpUtil
{
    public static List<string> Paths(IEnumerable<FileChange> changes) => changes.SelectMany(c => c.Paths).Distinct().ToList();

    public static List<(RepoSnapshot Repo, WipBranch Wip)> RequireLegs(Workspace w, string key)
    {
        var legs = w.Legs(key);
        if (legs.Count == 0) throw new OpBlockedException($"No in-flight ticket {key}");
        return legs;
    }

    public static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>
    /// feat/fix branches of a ticket (any suffix) on one repo's origin: remote branch name → sha.
    /// Null when origin can't be reached.
    /// </summary>
    public static async Task<Dictionary<string, string>?> RemoteTicketBranchesAsync(GitRepo g, string ticket)
    {
        var ls = await g.TryAsync("ls-remote", "--heads", "origin", $"feat/{ticket}*", $"fix/{ticket}*");
        if (!ls.Ok) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ls.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length == 2) d[parts[1].Replace("refs/heads/", "")] = parts[0];
        }
        return d;
    }

    /// <summary>"feat/PROJ-1_2" → "PROJ-1_2".</summary>
    public static IEnumerable<string> KeysOfRemoteBranches(IEnumerable<string> branches) =>
        branches.Select(b => b.IndexOf('/') is var i and > 0 ? b[(i + 1)..] : b);

    /// <summary>Switch branch carrying the repo's dirty (excluded) files verbatim; warns when one differs on the target.</summary>
    public static async Task SwitchAsync(OpContext ctx, RepoSnapshot r, string target)
    {
        var kept = await GitOps.SwitchCarryingAsync(ctx.Git(r), target, Paths(r.Changes), ctx.Tag);
        if (kept.Count > 0)
            ctx.Warn($"{r.Name}: kept your local {string.Join(", ", kept)} — the committed version differs on {target}, worth a look");
    }

    /// <summary>Fetch + fast-forward the default branch; problems become warnings, never failures.</summary>
    public static async Task UpdateDefaultAsync(OpContext ctx, RepoSnapshot r)
    {
        var up = await GitOps.UpdateDefaultAsync(ctx.Git(r), r.DefaultBranch, ctx.Tag, ctx.W.Matcher.IsExcluded);
        if (up.Warn) ctx.Warn($"{r.Name}: {up.Message}");
        else ctx.Log(up.Message);
    }

    /// <summary>Commits uncommitted work of whatever wip branch is checked out (used to auto-park before switching).</summary>
    public static async Task ParkCurrentAsync(OpContext ctx, RepoSnapshot r)
    {
        var key = r.CurrentWipKey!;
        var wip = r.Wip(key)!;
        var (desc, type) = DashboardBuilder.TicketMeta(ctx.W.Legs(key).Select(l => l.Wip));
        if (string.IsNullOrWhiteSpace(desc)) desc = "work in progress";
        ctx.Step(r.Name, $"Commit {key} locally (auto-park)");
        await GitOps.CommitSquashedAsync(ctx.Git(r), r.Included.ToList(), TicketKeys.CommitMessage(type, key, desc), wip.MergeBase, wip.Ahead);
    }
}
