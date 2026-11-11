namespace TicketFlow.Core.GitHub;

public enum PrStatus
{
    NotFound,
    Draft,
    Conflicts,
    Behind,
    ChecksFailing,
    ChecksRunning,
    ChangesRequested,
    ReviewRequired,
    Pending,
    Approved,
    Merged,
    Closed,
}

public static class PrStatusMapper
{
    public static PrStatus Map(PrInfo? pr, bool requireApproval)
    {
        if (pr is null) return PrStatus.NotFound;
        if (pr.State == "MERGED") return PrStatus.Merged;
        if (pr.State == "CLOSED") return PrStatus.Closed;
        if (pr.IsDraft) return PrStatus.Draft;
        if (pr.Mergeable == "CONFLICTING" || pr.MergeState == "DIRTY") return PrStatus.Conflicts;
        if (pr.Checks is "FAILURE" or "ERROR") return PrStatus.ChecksFailing;
        if (pr.ReviewDecision == "CHANGES_REQUESTED") return PrStatus.ChangesRequested;
        if (pr.MergeState == "BEHIND") return PrStatus.Behind;
        var approved = pr.ReviewDecision == "APPROVED" || (pr.ReviewDecision is null && !requireApproval);
        if (!approved) return PrStatus.ReviewRequired;
        if (pr.Checks is "PENDING" or "EXPECTED") return PrStatus.ChecksRunning;
        if (pr.Mergeable == "UNKNOWN" || pr.MergeState is "UNKNOWN" or null) return PrStatus.Pending;
        if (pr.MergeState == "BLOCKED") return PrStatus.Pending;
        return PrStatus.Approved;
    }

    /// <summary>True when the PR may be completed: merged, or approved and mergeable.</summary>
    public static bool IsReady(PrStatus s) => s is PrStatus.Approved or PrStatus.Merged;

    public static string Text(PrStatus s, PrInfo? pr) => s switch
    {
        PrStatus.NotFound => "No PR",
        PrStatus.Draft => "Draft",
        PrStatus.Conflicts => "Conflicts — needs rebase",
        PrStatus.Behind => "Behind base — needs update",
        PrStatus.ChecksFailing => "Checks failing",
        PrStatus.ChecksRunning => "Approved · checks running",
        PrStatus.ChangesRequested => "Changes requested",
        PrStatus.ReviewRequired => pr?.ReviewDecision is null ? "Awaiting approval" : "Review required",
        PrStatus.Pending => pr?.MergeState == "BLOCKED" ? "Blocked by branch rules" : "Checking mergeability",
        PrStatus.Approved => "Approved",
        PrStatus.Merged => "Merged",
        PrStatus.Closed => "Closed (not merged)",
        _ => s.ToString(),
    };

    /// <summary>Severity used for colouring: ok / warn / bad / info / done.</summary>
    public static string Tone(PrStatus s) => s switch
    {
        PrStatus.Approved => "ok",
        PrStatus.Merged => "done",
        PrStatus.Conflicts or PrStatus.ChecksFailing or PrStatus.ChangesRequested or PrStatus.Closed => "bad",
        PrStatus.Behind or PrStatus.NotFound => "warn",
        _ => "info",
    };
}
