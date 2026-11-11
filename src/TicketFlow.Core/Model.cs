namespace TicketFlow.Core;

public sealed record FileView(string Path, string? OrigPath, string Status, bool Excluded);

public sealed record RepoView(
    string Name,
    string Path,
    bool Ok,
    string? Error,
    string DefaultBranch,
    string? CurrentBranch,
    int Dirty,
    int DirtyExcluded,
    int Ahead,
    int Behind,
    string? InProgress,
    string? WorkingKey,
    string? GitHubSlug,
    List<FileView> Files);

public sealed record UnassignedView(
    string Repo,
    string Branch,
    bool IsDefault,
    List<FileView> Files,
    List<FileView> ExcludedFiles,
    string? BlockReason);

public sealed record PrView(
    int Number,
    string Url,
    string State,
    bool IsDraft,
    string Status,
    string StatusText,
    string Tone,
    string? Mergeable,
    string? MergeState,
    string? ReviewDecision,
    string? Checks,
    string HeadSha,
    string Title);

public sealed record LegView(
    string Repo,
    string Branch,
    string RemoteBranch,
    string State,
    string StateText,
    bool IsCurrent,
    int Ahead,
    int Behind,
    string Sha,
    string Subject,
    List<FileView> Files,
    PrView? Pr,
    bool LocalAhead,
    bool Ready,
    bool NeedsPush);

public sealed record ActionState(bool Enabled, string? Reason, string Label);

public sealed record TicketView(
    string Key,
    string Ticket,
    string Description,
    string Type,
    string Stage,
    string StatusText,
    string CommitMessage,
    List<LegView> Legs,
    List<string> Flags,
    Dictionary<string, ActionState> Actions,
    string? PrimaryAction,
    string? JiraUrl,
    CheckView? Check);

/// <summary>
/// Latest "Build &amp; test" result of a ticket. Status: passed | failed. Stale when the code changed since
/// (<see cref="StaleRepos"/> says where, including repos added to the ticket after the run).
/// </summary>
public sealed record CheckView(
    string Status,
    bool Stale,
    string Text,
    DateTimeOffset At,
    double Seconds,
    List<string> StaleRepos,
    List<Checks.RepoCheck> Repos);

public sealed record GitHubView(string? User, bool Ok, string? Error, DateTimeOffset? LastPoll);

public sealed record DashboardState(
    long Version,
    DateTimeOffset ScannedAt,
    bool Configured,
    List<RepoView> Repos,
    List<UnassignedView> Unassigned,
    List<TicketView> Tickets,
    GitHubView GitHub,
    string? JiraBaseUrl,
    OpProgress? RunningOp,
    int ArchiveCount);

// ---- operations ----

public sealed record PreviewStep(string Repo, string Text, List<string> Commands);

public sealed record OpPreview(
    string Kind,
    string Title,
    string Summary,
    List<PreviewStep> Steps,
    List<string> Warnings,
    List<string> Blockers,
    string ButtonLabel,
    string? ConfirmText = null,
    bool Destructive = false,
    object? Data = null);

public sealed class OpStepView
{
    public string Repo { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "running"; // running | ok | failed | skipped | warn
    public List<string> Log { get; set; } = [];
}

public sealed class OpProgress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Key { get; set; }
    public string Status { get; set; } = "running"; // running | succeeded | failed
    public bool Cancellable { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Links { get; set; } = [];
    public List<OpStepView> Steps { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class OpBlockedException(string message) : Exception(message);
