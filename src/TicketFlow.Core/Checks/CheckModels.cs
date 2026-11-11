namespace TicketFlow.Core.Checks;

public sealed record TestCounts(int Total, int Passed, int Failed, int Skipped);

/// <summary>One command's result. Status: ok | failed | skipped.</summary>
public sealed record CheckPhase(
    string Phase,
    string Label,
    string Command,
    string Status,
    int? ExitCode,
    double Seconds,
    string Summary,
    TestCounts? Tests,
    List<string> Errors,
    string? LogFile);

/// <summary>
/// One repo's result. Status: passed | failed | skipped. <see cref="Fingerprint"/> (working copy) and <see cref="Tree"/>
/// (HEAD + the ticket's changes as a git tree) record exactly what was tested, so the result can be marked stale later.
/// </summary>
public sealed record RepoCheck(string Repo, string? Fingerprint, string? Tree, string Status, string Summary, List<CheckPhase> Phases);

/// <summary>Latest "Build &amp; test" result of a ticket. Status: passed | failed.</summary>
public sealed record CheckRun(string Key, string Status, DateTimeOffset StartedAt, DateTimeOffset EndedAt, List<RepoCheck> Repos);

/// <summary>Latest result per in-flight ticket (checks.json) plus the full command logs (check-logs/&lt;key&gt;/&lt;run&gt;/).</summary>
public sealed class ChecksStore(AppPaths paths)
{
    private readonly JsonListStore<CheckRun> _file = new(paths.ChecksFile);
    private readonly Lock _lock = new();
    private List<CheckRun>? _cache;

    public string LogDir => paths.CheckLogsDir;

    public IReadOnlyList<CheckRun> All()
    {
        lock (_lock)
            return _cache ??= _file.All();
    }

    public CheckRun? Get(string key) => All().FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string NewRunDir(string key, string runId) => Path.Combine(LogDir, Safe(key), runId);

    /// <summary>Replaces the ticket's result; drops results and logs of tickets no longer in flight and older runs' logs.</summary>
    public void Save(CheckRun run, string runDir, IReadOnlyCollection<string> inFlight)
    {
        var keep = new HashSet<string>(inFlight, StringComparer.OrdinalIgnoreCase) { run.Key };
        lock (_lock)
        {
            _file.Mutate(list =>
            {
                list.RemoveAll(c => string.Equals(c.Key, run.Key, StringComparison.OrdinalIgnoreCase) || !keep.Contains(c.Key));
                list.Add(run);
            });
            _cache = null;
        }
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var keepDirs = keep.Select(Safe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.EnumerateDirectories(LogDir))
                if (!keepDirs.Contains(Path.GetFileName(d))) Directory.Delete(d, true);
            var ticketDir = Path.Combine(LogDir, Safe(run.Key));
            if (Directory.Exists(ticketDir))
                foreach (var d in Directory.EnumerateDirectories(ticketDir))
                    if (!string.Equals(Path.GetFullPath(d), Path.GetFullPath(runDir), StringComparison.OrdinalIgnoreCase)) Directory.Delete(d, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* log cleanup is best effort */ }
    }

    public static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
