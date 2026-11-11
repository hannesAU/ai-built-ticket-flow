using System.Text.RegularExpressions;
using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;

namespace TicketFlow.Core;

public static partial class Stages
{
    public const string Working = "working";
    public const string Parked = "parked";
    public const string Review = "review";
    public const string Ready = "ready";

    public static int Order(string stage) => stage switch { Working => 0, Parked => 1, Review => 2, Ready => 3, _ => 4 };
}

public static class LegStates
{
    public const string Working = "working";
    public const string WorkingCommitted = "working-committed";
    public const string Empty = "empty";
    public const string Parked = "parked";
    public const string Review = "review";
    public const string Merged = "merged";

    public static bool IsWorking(string s) => s is Working or WorkingCommitted;
}

/// <summary>Pure derivation of the dashboard from repo snapshots + PR info.</summary>
public static partial class DashboardBuilder
{
    [GeneratedRegex(@"^(feat|fix|chore)(?:\(([^)]*)\))?:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectRegex();

    public static FileView ToView(FileChange c) => new(c.Path, c.OrigPath, c.StatusLetter, c.Excluded);

    public static (string Description, string Type) TicketMeta(IEnumerable<WipBranch> wips)
    {
        var list = wips.ToList();
        var desc = list.Select(w => w.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        var type = list.Select(w => w.Type).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        foreach (var w in list.Where(w => w.Ahead > 0))
        {
            var m = SubjectRegex().Match(w.Subject);
            if (!m.Success) continue;
            desc ??= m.Groups[3].Value;
            type ??= m.Groups[1].Value.ToLowerInvariant();
        }
        return (desc ?? "", TicketKeys.NormalizeType(type));
    }

    public static DashboardState Build(AppConfig cfg, IReadOnlyList<RepoSnapshot> snaps, IReadOnlyDictionary<PrQuery, PrInfo> prs,
        GitHubView gh, OpProgress? running, int archiveCount, long version, IReadOnlyList<CheckRun>? checks = null)
    {
        var repos = snaps.Select(s => new RepoView(
            s.Name, s.Path, s.Ok, s.Error, s.DefaultBranch, s.CurrentBranch,
            s.Included.Count(), s.ExcludedChanges.Count(), s.DefaultAhead, s.DefaultBehind, s.InProgress,
            s.HasIncluded ? s.CurrentWipKey : null,
            s.GitHubOwner is null ? null : $"{s.GitHubOwner}/{s.GitHubRepo}",
            s.Changes.Select(ToView).ToList())).ToList();

        var unassigned = snaps.Where(s => s.HasUnassigned).Select(s => new UnassignedView(
            s.Name,
            s.CurrentBranch ?? "(detached HEAD)",
            s.CurrentBranch == s.DefaultBranch,
            s.Included.Select(ToView).ToList(),
            s.ExcludedChanges.Select(ToView).ToList(),
            s.InProgress is not null ? $"{s.InProgress} in progress — finish it first" : null)).ToList();

        var keys = snaps.SelectMany(s => s.Wips.Select(w => w.Key)).Distinct().ToList();
        var tickets = keys.Select(k => BuildTicket(cfg, k, snaps, prs,
                checks?.FirstOrDefault(c => string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(t => Stages.Order(t.Stage))
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DashboardState(version, snaps.Count == 0 ? DateTimeOffset.Now : snaps.Max(s => s.ScannedAt),
            cfg.Repos.Count > 0, repos, unassigned, tickets, gh, cfg.JiraBaseUrl, running, archiveCount);
    }

    private static TicketView BuildTicket(AppConfig cfg, string key, IReadOnlyList<RepoSnapshot> snaps, IReadOnlyDictionary<PrQuery, PrInfo> prs,
        CheckRun? check)
    {
        var legsRaw = snaps.SelectMany(s => s.Wips.Where(w => w.Key == key).Select(w => (s, w))).ToList();
        var (desc, type) = TicketMeta(legsRaw.Select(l => l.w));
        var message = TicketKeys.CommitMessage(type, key, desc);
        var legs = legsRaw.Select(l => BuildLeg(l.s, l.w, message, prs)).ToList();
        var flags = new List<string>();

        var active = legs.Where(l => l.State != LegStates.Merged).ToList();
        string stage;
        if (legs.Any(l => LegStates.IsWorking(l.State))) stage = Stages.Working;
        else if (legs.All(l => l.Ready) && legs.Count > 0) stage = Stages.Ready;
        else if (legs.Any(l => l.Pr is { State: "OPEN" or "MERGED" })) stage = Stages.Review;
        else stage = Stages.Parked;

        if (stage == Stages.Review && active.Any(l => l.Pr is null or { State: "CLOSED" })) flags.Add("Some repos have no PR yet");
        foreach (var l in legsRaw.Where(l => l.w.Ahead > 1)) flags.Add($"{l.s.Name}: {l.w.Ahead} commits (will be squashed)");
        if (string.IsNullOrWhiteSpace(desc)) flags.Add("No description");

        var actions = new Dictionary<string, ActionState>
        {
            ["commit"] = CommitAction(legs),
            ["resume"] = ResumeAction(stage, legsRaw, legs),
            ["push"] = PushAction(legsRaw, legs),
            ["complete"] = CompleteAction(stage, legs),
            ["check"] = CheckAction(legsRaw),
            ["discard"] = new(true, null, "Discard…"),
        };

        var primary = stage switch
        {
            Stages.Working => "commit",
            Stages.Parked => "push",
            Stages.Review => actions["push"].Enabled ? "push" : null,
            Stages.Ready => "complete",
            _ => null,
        };

        var statusText = stage switch
        {
            Stages.Working => $"Working in {string.Join(", ", legs.Where(l => LegStates.IsWorking(l.State)).Select(l => l.Repo))}",
            Stages.Parked => "Committed locally",
            Stages.Ready => "All PRs approved or merged",
            _ => string.Join(" · ", legs.Select(l => $"{l.Repo}: {l.StateText}")),
        };

        var ticket = TicketKeys.TicketOf(key);
        return new TicketView(key, ticket, desc, type, stage, statusText, message, legs, flags, actions, primary,
            string.IsNullOrWhiteSpace(cfg.JiraBaseUrl) || TicketKeys.IsVirtual(key) ? null : cfg.JiraBaseUrl!.TrimEnd('/') + "/" + ticket,
            check is null ? null : BuildCheck(check, legsRaw));
    }

    /// <summary>
    /// A repo's result is current while its tested code is unchanged: compared by working-copy fingerprint while the
    /// branch is checked out, and by tree once it's committed (so committing a checked ticket keeps the result).
    /// </summary>
    private static CheckView BuildCheck(CheckRun run, List<(RepoSnapshot s, WipBranch w)> legs)
    {
        var stale = new List<string>();
        foreach (var (s, w) in legs)
        {
            var r = run.Repos.FirstOrDefault(x => string.Equals(x.Repo, s.Name, StringComparison.OrdinalIgnoreCase));
            if (r is null) stale.Add($"{s.Name} (not checked)");
            else if (w.IsCurrent ? r.Fingerprint is null || r.Fingerprint != s.Fingerprint : r.Tree is null || r.Tree != w.Tree) stale.Add(s.Name);
        }
        return new CheckView(run.Status, stale.Count > 0, CheckSummary(run), run.EndedAt, (run.EndedAt - run.StartedAt).TotalSeconds, stale, run.Repos);
    }

    public static string CheckSummary(CheckRun run)
    {
        var failed = run.Repos.Where(r => r.Status == "failed").ToList();
        if (failed.Count > 0) return "Failed in " + string.Join(", ", failed.Select(r => $"{r.Repo} ({r.Summary})"));
        if (run.Repos.All(r => r.Status == "skipped")) return "Nothing was built or tested";
        var tests = run.Repos.SelectMany(r => r.Phases).Select(p => p.Tests).OfType<TestCounts>().ToList();
        return tests.Count > 0 ? $"Builds pass · {tests.Sum(t => t.Passed)} tests pass" : "Builds pass";
    }

    private static ActionState CheckAction(List<(RepoSnapshot s, WipBranch w)> raw)
    {
        const string label = "Build & test";
        var notOut = raw.Where(l => !l.w.IsCurrent).Select(l => l.s.Name).ToList();
        if (notOut.Count == raw.Count)
            return new(false, "Not checked out — resume the ticket first; checks build your working copy", label);
        if (notOut.Count > 0)
            return new(false, $"Not checked out in {string.Join(", ", notOut)} — resume the ticket first", label);
        foreach (var (s, _) in raw)
        {
            if (!s.Ok) return new(false, $"{s.Name}: {s.Error}", label);
            if (s.InProgress is not null) return new(false, $"{s.Name}: {s.InProgress} in progress", label);
        }
        return new(true, null, label);
    }

    private static LegView BuildLeg(RepoSnapshot s, WipBranch w, string message, IReadOnlyDictionary<PrQuery, PrInfo> prs)
    {
        PrInfo? pr = null;
        if (s.GitHubOwner is not null && s.GitHubRepo is not null)
            prs.TryGetValue(new PrQuery(s.GitHubOwner, s.GitHubRepo, w.RemoteBranch), out pr);
        var status = PrStatusMapper.Map(pr, s.Config.RequireApproval);
        var dirty = w.IsCurrent ? s.Included.ToList() : [];

        string state;
        if (status == PrStatus.Merged) state = LegStates.Merged;
        else if (w.IsCurrent && dirty.Count > 0) state = w.Ahead > 0 ? LegStates.WorkingCommitted : LegStates.Working;
        else if (w.IsCurrent && w.Ahead == 0) state = LegStates.Working;
        else if (w.Ahead == 0) state = LegStates.Empty;
        else if (pr?.State == "OPEN") state = LegStates.Review;
        else state = LegStates.Parked;

        var open = pr?.State == "OPEN";
        var localAhead = open && pr!.HeadSha != w.Sha;
        var needsPush = state is LegStates.Parked or LegStates.Review && w.Ahead >= 1 && (
            !open || localAhead || w.Ahead > 1 || w.Subject != message || pr!.Title != message ||
            status is PrStatus.Conflicts or PrStatus.Behind);
        var ready = status == PrStatus.Merged || (status == PrStatus.Approved && !localAhead && !LegStates.IsWorking(state));

        var stateText = state switch
        {
            LegStates.Working => dirty.Count == 0 ? "Checked out, no changes" : $"{dirty.Count} uncommitted",
            LegStates.WorkingCommitted => $"Commit + {dirty.Count} uncommitted",
            LegStates.Empty => "No changes",
            LegStates.Parked => "Committed locally" + (w.Behind > 0 ? $" · {w.Behind} behind {s.DefaultBranch}" : ""),
            LegStates.Review => PrStatusMapper.Text(status, pr) + (localAhead ? " · local changes not pushed" : ""),
            LegStates.Merged => "Merged",
            _ => state,
        };

        var files = dirty.Select(ToView)
            .Concat(w.Files.Where(f => dirty.All(d => d.Path != f.Path)).Select(f => new FileView(f.Path, null, f.Status.ToString(), false)))
            .ToList();

        var prView = pr is null ? null : new PrView(pr.Number, pr.Url, pr.State, pr.IsDraft, status.ToString(),
            PrStatusMapper.Text(status, pr), PrStatusMapper.Tone(status), pr.Mergeable, pr.MergeState, pr.ReviewDecision,
            pr.Checks, pr.HeadSha, pr.Title);

        return new LegView(s.Name, w.Branch, w.RemoteBranch, state, stateText, w.IsCurrent, w.Ahead, w.Behind, w.Sha, w.Subject,
            files, prView, localAhead, ready, needsPush);
    }

    private static ActionState CommitAction(List<LegView> legs)
    {
        var any = legs.Any(l => LegStates.IsWorking(l.State) && l.Files.Count > 0 && l.IsCurrent);
        return new(any, any ? null : "No uncommitted changes", "Commit locally");
    }

    private static ActionState ResumeAction(string stage, List<(RepoSnapshot s, WipBranch w)> raw, List<LegView> legs)
    {
        var label = stage is Stages.Review or Stages.Ready ? "Re-open" : "Resume";
        if (legs.Any(l => l.State == LegStates.Merged))
            return new(false, "A PR is already merged — complete the ticket instead", label);
        if (raw.All(l => l.w.IsCurrent && l.w.Ahead == 0))
            return new(false, "Already active", label);
        foreach (var (s, w) in raw)
        {
            if (!s.Ok) return new(false, $"{s.Name}: {s.Error}", label);
            if (w.IsCurrent) continue;
            if (s.InProgress is not null) return new(false, $"{s.Name}: {s.InProgress} in progress", label);
            if (s.HasUnassigned)
                return new(false, $"{s.Name} has unassigned changes on {s.CurrentBranch ?? "a detached HEAD"} — assign or discard them first", label);
        }
        return new(true, null, label);
    }

    private static ActionState PushAction(List<(RepoSnapshot s, WipBranch w)> raw, List<LegView> legs)
    {
        var label = legs.Any(l => l.Pr?.State == "OPEN") ? "Update PRs" : "Move to PR";
        if (legs.Any(l => LegStates.IsWorking(l.State)))
            return new(false, "Commit locally first", label);
        var empty = legs.FirstOrDefault(l => l.State == LegStates.Empty);
        if (empty is not null)
            return new(false, $"{empty.Repo} has no changes — discard that repo from the ticket", label);
        var noRemote = raw.FirstOrDefault(l => l.s.GitHubOwner is null);
        if (noRemote.s is not null)
            return new(false, $"{noRemote.s.Name} has no GitHub origin remote", label);
        var busy = raw.FirstOrDefault(l => l.s.InProgress is not null);
        if (busy.s is not null)
            return new(false, $"{busy.s.Name}: {busy.s.InProgress} in progress", label);
        if (!legs.Any(l => l.NeedsPush))
            return new(false, "PRs are up to date", label);
        return new(true, null, label);
    }

    private static ActionState CompleteAction(string stage, List<LegView> legs)
    {
        if (stage == Stages.Ready) return new(true, null, "Complete…");
        var waiting = legs.Where(l => !l.Ready).Select(l => $"{l.Repo}: {l.StateText}");
        return new(false, "Waiting on " + string.Join("; ", waiting), "Complete…");
    }
}
