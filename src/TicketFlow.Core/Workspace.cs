using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;

namespace TicketFlow.Core;

public sealed record Notification(string Title, string Body, string Tone, string? Url);

/// <summary>Holds the latest scan of every repo plus cached PR info, and builds the dashboard.</summary>
public sealed class Workspace
{
    private readonly Lock _lock = new();
    private Dictionary<string, RepoSnapshot> _snaps = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<PrQuery, PrInfo> _prs = new();
    private readonly Dictionary<string, PrStatus> _lastStatus = new();
    private GitHubView _gh = new(null, true, null, null);
    private long _version;
    private ExcludeMatcher? _matcher;
    private AppConfig? _matcherFor;
    private ClaudeHookExclusions.Result? _hookFor;

    public Workspace(ConfigStore config, GitCli git, IGitHubClient gitHub, ArchiveStore archive, TrashStore trash, JournalStore journal,
        ChecksStore checks)
    {
        ConfigStore = config;
        Git = git;
        GitHub = gitHub;
        Archive = archive;
        Trash = trash;
        Journal = journal;
        Checks = checks;
        Scanner = new RepoScanner(git);
        config.Changed += cfg =>
        {
            Scanner.ClearCache();
            _ = RescanAllAsync();
        };
    }

    public ConfigStore ConfigStore { get; }
    public AppConfig Config => ConfigStore.Current;
    public GitCli Git { get; }
    public IGitHubClient GitHub { get; }
    public ArchiveStore Archive { get; }
    public TrashStore Trash { get; }
    public JournalStore Journal { get; }
    public ChecksStore Checks { get; }
    public RepoScanner Scanner { get; }
    public OpProgress? RunningOp { get; set; }

    public event Action? Changed;
    public event Action<Notification>? Notified;

    public void Notify(Notification n) => Notified?.Invoke(n);

    public ExcludeMatcher Matcher
    {
        get
        {
            var cfg = Config;
            lock (_lock)
            {
                // the Claude hook result is cached by file timestamp, so edits to the hook are picked up on the next scan
                var hook = cfg.ExcludeFromClaudeHook ? ClaudeHookExclusions.Load(cfg.ClaudeSettingsPath) : null;
                if (_matcher is null || !ReferenceEquals(_matcherFor, cfg) || !ReferenceEquals(_hookFor, hook))
                {
                    _matcher = new ExcludeMatcher(cfg.Excluded, hook?.Patterns);
                    _matcherFor = cfg;
                    _hookFor = hook;
                }
                return _matcher;
            }
        }
    }

    /// <summary>Snapshots in configured (merge) order.</summary>
    public IReadOnlyList<RepoSnapshot> Snapshots
    {
        get
        {
            lock (_lock)
                return Config.Repos.Select(r => _snaps.GetValueOrDefault(r.Path)).Where(s => s is not null).ToList()!;
        }
    }

    public RepoSnapshot? Snapshot(string name) =>
        Snapshots.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public RepoSnapshot Require(string name) =>
        Snapshot(name) ?? throw new OpBlockedException($"Unknown repo '{name}'");

    public PrInfo? Pr(RepoSnapshot r, WipBranch w)
    {
        if (r.GitHubOwner is null || r.GitHubRepo is null) return null;
        lock (_lock)
            return _prs.GetValueOrDefault(new PrQuery(r.GitHubOwner, r.GitHubRepo, w.RemoteBranch));
    }

    public async Task RescanAllAsync(CancellationToken ct = default)
    {
        var cfg = Config;
        var matcher = Matcher;
        var scans = await Task.WhenAll(cfg.Repos.Select(r => Scanner.ScanAsync(r, matcher, ct)));
        lock (_lock)
            _snaps = scans.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        NotifyChanged();
    }

    public async Task<RepoSnapshot> RescanAsync(RepoConfig rc, CancellationToken ct = default)
    {
        var snap = await Scanner.ScanAsync(rc, Matcher, ct);
        lock (_lock)
            _snaps[snap.Path] = snap;
        NotifyChanged();
        return snap;
    }

    public async Task<RepoSnapshot> RescanAsync(string repoPath, CancellationToken ct = default)
    {
        var rc = Config.Repos.FirstOrDefault(r => string.Equals(r.Path, repoPath, StringComparison.OrdinalIgnoreCase))
                 ?? throw new OpBlockedException($"Repo not configured: {repoPath}");
        return await RescanAsync(rc, ct);
    }

    public void NotifyChanged()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    /// <summary>Refreshes PR info for every wip branch from GitHub (one GraphQL round trip per 25 branches).</summary>
    public async Task PollPrsAsync(CancellationToken ct = default)
    {
        var queries = Snapshots
            .Where(s => s.GitHubOwner is not null && s.GitHubRepo is not null)
            .SelectMany(s => s.Wips.Select(w => new PrQuery(s.GitHubOwner!, s.GitHubRepo!, w.RemoteBranch)))
            .Distinct()
            .ToList();
        try
        {
            var user = await GitHub.GetViewerAsync(ct);
            var prs = await GitHub.GetPrsAsync(queries, ct);
            lock (_lock)
            {
                _prs = prs;
                _gh = new GitHubView(user, true, null, DateTimeOffset.Now);
            }
            RaiseTransitions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
                _gh = _gh with { Ok = false, Error = ex.Message, LastPoll = DateTimeOffset.Now };
        }
        NotifyChanged();
    }

    public void SetPr(PrQuery q, PrInfo pr)
    {
        lock (_lock)
            _prs[q] = pr;
    }

    private void RaiseTransitions()
    {
        foreach (var s in Snapshots)
        foreach (var w in s.Wips)
        {
            var pr = Pr(s, w);
            var status = PrStatusMapper.Map(pr, s.Config.RequireApproval);
            var id = $"{s.Name}|{w.Key}";
            bool had;
            PrStatus prev;
            lock (_lock)
            {
                had = _lastStatus.TryGetValue(id, out prev);
                _lastStatus[id] = status;
            }
            if (!had || prev == status || pr is null) continue;
            if (status is PrStatus.Approved or PrStatus.Merged or PrStatus.ChecksFailing or PrStatus.Conflicts
                or PrStatus.ChangesRequested or PrStatus.Closed or PrStatus.Behind)
                Notified?.Invoke(new Notification($"{w.Key} · {s.Name}", $"PR #{pr.Number}: {PrStatusMapper.Text(status, pr)}",
                    PrStatusMapper.Tone(status), pr.Url));
        }
    }

    public DashboardState State()
    {
        GitHubView gh;
        Dictionary<PrQuery, PrInfo> prs;
        lock (_lock)
        {
            gh = _gh;
            prs = new(_prs);
        }
        return DashboardBuilder.Build(Config, Snapshots, prs, gh, RunningOp, Archive.All().Count, Interlocked.Read(ref _version), Checks.All());
    }

    /// <summary>All legs of a ticket, in repo (merge) order.</summary>
    public List<(RepoSnapshot Repo, WipBranch Wip)> Legs(string key) =>
        Snapshots.SelectMany(s => s.Wips.Where(w => w.Key == key).Select(w => (s, w))).ToList();
}
