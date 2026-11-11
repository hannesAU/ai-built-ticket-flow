using TicketFlow.Core.GitHub;

namespace TicketFlow.Core.Reviews;

/// <summary>One concern raised by an automated review. Severity: high | medium | low.</summary>
public sealed record ReviewConcern(string Severity, string Title, string Detail, string? File = null, int? Line = null, string? Repo = null);

/// <summary>What a ticket review saw in one repo: the working-copy fingerprint (while checked out) and the reviewed tree.</summary>
public sealed record ReviewedRepo(string Repo, string? Fingerprint, string? Tree);

/// <summary>Whether a change the viewer asked for earlier was made. Status: addressed | partially | not-addressed | unclear.</summary>
public sealed record RequestCheck(string Request, string Status, string Note);

/// <summary>The outcome of one Claude Code review of one PR head commit.</summary>
public sealed record PrReviewResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    /// <summary>owner/repo#number, or ticket:KEY for a review of your own work in progress</summary>
    public required string Pr { get; init; }
    /// <summary>Ticket reviews: the repo the review was started from (reviewed first, but every repo is reviewed).</summary>
    public string? Focus { get; init; }
    /// <summary>Ticket reviews: what was reviewed per repo (compared with the current code to tell when it's stale).</summary>
    public List<ReviewedRepo> Repos { get; init; } = [];
    public required string HeadSha { get; init; }
    /// <summary>The earlier commit the "since" diff started from (the viewer's last review, else the last automated review).</summary>
    public string? SinceSha { get; init; }
    /// <summary>new | commits | manual</summary>
    public string Trigger { get; init; } = "new";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public double Seconds { get; init; }
    /// <summary>ok | failed</summary>
    public string Status { get; init; } = "ok";
    public string? Error { get; init; }
    /// <summary>looks-good | minor-issues | needs-changes</summary>
    public string? Verdict { get; init; }
    /// <summary>low | medium | high</summary>
    public string? Risk { get; init; }
    public string? Summary { get; init; }
    public List<ReviewConcern> Concerns { get; init; } = [];
    public List<RequestCheck> PreviousRequests { get; init; } = [];
    public double? CostUsd { get; init; }
    public int? Turns { get; init; }
    public string? Model { get; init; }
    public string? LogFile { get; init; }
}

/// <summary>Review history per PR (pr-reviews.json); results of PRs that are no longer open are dropped on the next poll.</summary>
public sealed class PrReviewStore(AppPaths paths)
{
    public const int KeepPerPr = 8;
    private readonly JsonListStore<PrReviewResult> _file = new(paths.ReviewsFile);
    private readonly Lock _lock = new();
    private List<PrReviewResult>? _cache;

    public string Dir => paths.ReviewsDir;

    public IReadOnlyList<PrReviewResult> All()
    {
        lock (_lock)
            return _cache ??= _file.All();
    }

    /// <summary>Newest first.</summary>
    public List<PrReviewResult> For(string pr) =>
        All().Where(r => r.Pr == pr).OrderByDescending(r => r.At).ToList();

    public PrReviewResult? Get(string id) => All().FirstOrDefault(r => r.Id == id);

    public void Add(PrReviewResult r)
    {
        lock (_lock)
        {
            _file.Mutate(list =>
            {
                list.Add(r);
                var old = list.Where(x => x.Pr == r.Pr).OrderByDescending(x => x.At).Skip(KeepPerPr).ToList();
                foreach (var o in old)
                {
                    list.Remove(o);
                    TryDelete(o.LogFile);
                }
            });
            _cache = null;
        }
    }

    /// <summary>Drops the results (and logs) of PRs in <paramref name="repo"/> that aren't in <paramref name="open"/> any more.</summary>
    public void Prune(RepoRef repo, IReadOnlyCollection<string> open)
    {
        var prefix = $"{repo.Owner}/{repo.Repo}#";
        var openSet = open.ToHashSet();
        if (!All().Any(r => r.Pr.StartsWith(prefix, StringComparison.Ordinal) && !openSet.Contains(r.Pr))) return;
        lock (_lock)
        {
            _file.Mutate(list =>
            {
                foreach (var r in list.Where(r => r.Pr.StartsWith(prefix, StringComparison.Ordinal) && !openSet.Contains(r.Pr)).ToList())
                {
                    list.Remove(r);
                    TryDelete(r.LogFile);
                }
            });
            _cache = null;
        }
        try
        {
            var safe = SafeName(repo.Owner, repo.Repo, 0)[..^1]; // "owner-repo-"
            var keep = openSet.Select(SafeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Dir))
                foreach (var d in Directory.EnumerateDirectories(Dir, safe + "*"))
                {
                    var name = Path.GetFileName(d);
                    if (name[safe.Length..].All(char.IsAsciiDigit) && !keep.Contains(name)) Directory.Delete(d, true);
                }
        }
        catch { /* best effort */ }
    }

    public string WorkDir(RemotePr pr) => Path.Combine(Dir, SafeName(pr.Owner, pr.Repo, pr.Number));

    public const string TicketPrefix = "ticket:";

    public string TicketWorkDir(string key) => Path.Combine(Dir, "ticket-" + string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')));

    /// <summary>Drops the results (and work folders) of ticket reviews whose ticket is no longer in flight. True when anything was dropped.</summary>
    public bool PruneTickets(IReadOnlyCollection<string> inFlight)
    {
        var keep = inFlight.Select(k => TicketPrefix + k).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = All().Where(r => r.Pr.StartsWith(TicketPrefix, StringComparison.Ordinal) && !keep.Contains(r.Pr)).ToList();
        if (gone.Count == 0) return false;
        lock (_lock)
        {
            _file.Mutate(list => list.RemoveAll(r => r.Pr.StartsWith(TicketPrefix, StringComparison.Ordinal) && !keep.Contains(r.Pr)));
            _cache = null;
        }
        foreach (var key in gone.Select(r => r.Pr[TicketPrefix.Length..]).Distinct())
            try
            {
                var d = TicketWorkDir(key);
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
            catch { /* best effort */ }
        return true;
    }

    private static string SafeName(string owner, string repo, int number) =>
        string.Concat($"{owner}-{repo}-{number}".Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static string SafeName(string prKey)
    {
        var hash = prKey.LastIndexOf('#');
        var slash = prKey.IndexOf('/');
        return SafeName(prKey[..slash], prKey[(slash + 1)..hash], int.Parse(prKey[(hash + 1)..]));
    }

    private static void TryDelete(string? file)
    {
        try { if (file is not null && File.Exists(file)) File.Delete(file); }
        catch { /* best effort */ }
    }
}
