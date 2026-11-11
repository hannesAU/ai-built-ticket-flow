using TicketFlow.Core.Git;

namespace TicketFlow.Core.Ops;

public sealed record KeyRequest(string Key);
public sealed record AssignRequest(string? Ticket, string? ExistingKey, string? Description, string? Type, List<string>? Repos);
public sealed record CommitRequest(string Key, string? Description, string? Type);
public sealed record DiscardTicketRequest(string Key, List<string>? Repos, bool ClosePrs, bool DeleteRemote);
public sealed record DiscardChangesRequest(string Repo, List<string> Paths);
public sealed record RestoreRequest(string Repo, string Ref);
public sealed record SyncRequest(string? Repo);
public sealed record ResolveRequest(string Key, string Repo);
public sealed record RebaseControlRequest(string Repo, string Action);
public sealed record MetaRequest(string Key, string Description, string Type);

static class P
{
    public static OpPreview Make(string kind, string title, string summary, List<PreviewStep> steps, List<string> warnings,
        List<string> blockers, string button, string? confirm = null, bool destructive = false, object? data = null) =>
        new(kind, title, summary, steps, warnings, blockers, button, confirm, destructive, data);

    public static PreviewStep S(string repo, string text, params string[] cmds) => new(repo, text, cmds.ToList());

    public static string Carry(RepoSnapshot r) =>
        r.ExcludedChanges.Any() ? $" (carrying {OpUtil.Plural(r.ExcludedChanges.Count(), "excluded file")})" : "";
}

// ------------------------------------------------------------------ assign

public sealed class AssignOp : Operation<AssignRequest>
{
    public override string Kind => "assign";
    public override string? KeyOf(AssignRequest req) =>
        req.ExistingKey ?? (string.IsNullOrWhiteSpace(req.Ticket) ? null : req.Ticket);

    public static bool IsTaken(Workspace w, string key) =>
        w.Snapshots.Any(s => s.Wip(key) is not null ||
                             TicketKeys.RemoteBranches(key).Any(s.RemoteTicketBranches.Contains)) ||
        w.Archive.HasKey(key);

    private static List<RepoSnapshot> Targets(Workspace w, AssignRequest req) =>
        w.Snapshots.Where(s => s.HasUnassigned &&
                               (req.Repos is null || req.Repos.Contains(s.Name, StringComparer.OrdinalIgnoreCase))).ToList();

    private static (string? Key, string Desc, string Type, string? Ticket, List<object> Existing, List<string> Blockers) Resolve(Workspace w, AssignRequest req)
    {
        var blockers = new List<string>();
        var existing = new List<object>();
        var desc = req.Description?.Trim() ?? "";
        var type = TicketKeys.NormalizeType(req.Type);
        string? key = null, ticket;
        if (!string.IsNullOrWhiteSpace(req.ExistingKey))
        {
            var legs = w.Legs(req.ExistingKey);
            if (legs.Count == 0) blockers.Add($"Ticket {req.ExistingKey} is not in flight");
            else
            {
                key = req.ExistingKey;
                (desc, type) = DashboardBuilder.TicketMeta(legs.Select(l => l.Wip));
            }
            ticket = TicketKeys.TicketOf(req.ExistingKey);
        }
        else
        {
            // no ticket number → a virtual ticket named after the description ("chore: description")
            ticket = string.IsNullOrWhiteSpace(req.Ticket)
                ? desc.Length > 0 ? TicketKeys.VirtualTicket(desc, type) : null
                : TicketKeys.NormalizeTicket(req.Ticket, w.Config.TicketPrefixes);
            if (ticket is null && !string.IsNullOrWhiteSpace(req.Ticket))
                blockers.Add("Enter a ticket number like PROJ-1234, or leave it empty for work without a ticket");
            if (ticket is not null)
            {
                foreach (var k in w.Snapshots.SelectMany(s => s.Wips).Select(x => x.Key).Distinct()
                             .Where(k => string.Equals(TicketKeys.TicketOf(k), ticket, StringComparison.OrdinalIgnoreCase)))
                    existing.Add(new { key = k, description = DashboardBuilder.TicketMeta(w.Legs(k).Select(l => l.Wip)).Description });
                key = TicketKeys.Allocate(ticket, k => IsTaken(w, k));
            }
            if (desc.Length == 0) blockers.Add("Enter a short description");
        }
        return (key, desc, type, ticket, existing, blockers);
    }

    public override Task<OpPreview> PreviewAsync(Workspace w, AssignRequest req)
    {
        var (key, desc, type, ticket, existing, blockers) = Resolve(w, req);
        var targets = Targets(w, req);
        if (targets.Count == 0) blockers.Add("No unassigned changes selected");
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        var branch = TicketKeys.WipBranch(key ?? "…");
        foreach (var r in targets)
        {
            var n = r.Included.Count();
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            steps.Add(P.S(r.Name, "Back up current changes (Recovery tab)"));
            if (key is not null && r.Wip(key) is { } existingLeg)
                steps.Add(P.S(r.Name, $"Move {OpUtil.Plural(n, "changed file")} onto existing {branch}" + (existingLeg.Ahead > 0 ? " (its commit is re-opened)" : ""),
                    "git stash push -u", $"git switch {branch}", "git reset --mixed <merge-base>", "git stash pop"));
            else if (r.CurrentBranch == r.DefaultBranch)
                steps.Add(P.S(r.Name, $"Create {branch} from {r.DefaultBranch} with {OpUtil.Plural(n, "changed file")}", $"git switch -c {branch}"));
            else
            {
                steps.Add(P.S(r.Name, $"Move {OpUtil.Plural(n, "changed file")} from {r.CurrentBranch ?? "detached HEAD"} onto a new {branch} based on {r.DefaultBranch}",
                    "git stash push -u", $"git switch {r.DefaultBranch}", $"git switch -c {branch}", "git stash pop"));
                warnings.Add($"{r.Name}: changes are on '{r.CurrentBranch ?? "detached HEAD"}', not {r.DefaultBranch}. They move to a branch based on {r.DefaultBranch}; that branch's own commits are not included.");
            }
        }
        var newKey = key is not null && existing.Count > 0 && string.IsNullOrWhiteSpace(req.ExistingKey);
        if (newKey) warnings.Add($"{ticket} is already in flight — this creates a separate ticket {key}. Pick the existing one to add to it instead.");
        var title = key is null ? "Assign changes to a ticket" : $"Assign changes to {key}";
        var summary = key is null ? "" : TicketKeys.CommitMessage(type, key, desc.Length > 0 ? desc : "…");
        return Task.FromResult(P.Make(Kind, title, summary, steps, warnings, blockers,
            string.IsNullOrWhiteSpace(req.ExistingKey) ? $"Create {key ?? "ticket"}" : $"Add to {key}",
            data: new { ticket, key, description = desc, type, existing, repos = targets.Select(t => t.Name) }));
    }

    public override async Task RunAsync(OpContext ctx, AssignRequest req)
    {
        var (key, desc, type, ticket, _, _) = Resolve(ctx.W, req);
        if (key is null || ticket is null) throw new OpBlockedException("No ticket");

        if (string.IsNullOrWhiteSpace(req.ExistingKey))
        {
            // safety check against the origin of the first repo only (querying every repo is too slow);
            // a clash in another repo is resolved at push time by renaming to the next free _n suffix
            if (Targets(ctx.W, req).FirstOrDefault(s => s.Ok) is { } first)
            {
                ctx.Step(first.Name, $"Check {ticket} is free on origin");
                var remote = await OpUtil.RemoteTicketBranchesAsync(ctx.Git(first), ticket);
                var remoteTaken = new HashSet<string>(OpUtil.KeysOfRemoteBranches(remote?.Keys ?? Enumerable.Empty<string>()), StringComparer.OrdinalIgnoreCase);
                var allocated = TicketKeys.Allocate(ticket, k => IsTaken(ctx.W, k) || remoteTaken.Contains(k));
                if (allocated != key)
                {
                    ctx.Warn($"{key} exists on origin of {first.Name} — using {allocated}");
                    key = allocated;
                }
            }
        }

        var branch = TicketKeys.WipBranch(key);
        foreach (var r0 in Targets(ctx.W, req))
        {
            var r = await ctx.Fresh(r0);
            if (!r.HasUnassigned)
            {
                ctx.Step(r.Name, "Nothing to assign");
                continue;
            }
            var g = ctx.Git(r);
            ctx.Step(r.Name, "Back up current changes");
            await GitOps.BackupAsync(g, r.Changes, $"before assigning to {key}", r.CurrentBranch);
            var included = OpUtil.Paths(r.Included);
            var excluded = OpUtil.Paths(r.ExcludedChanges);
            var stashMsg = $"ticket-flow:assign:{ctx.Tag}";

            // included changes move with merge semantics (stash → switch → pop);
            // excluded files are carried byte-for-byte around the whole move.
            async Task MoveIncluded(Func<Task> switchSteps, string context)
            {
                await GitOps.StashPathsAsync(g, included, stashMsg);
                try
                {
                    await switchSteps();
                }
                catch
                {
                    await g.TryAsync("stash", "pop");
                    throw;
                }
                await GitOps.PopAsync(g, stashMsg, context);
            }

            Task WithExcluded(Func<Task> action) =>
                excluded.Count == 0 ? action() : GitOps.CarryVerbatimAsync(g, excluded, $"ticket-flow:carry:{ctx.Tag}", action);

            if (r.Wip(key) is { } existing)
            {
                ctx.Step(r.Name, $"Move changes onto {branch}");
                // pop while the commit is still in place so git can 3-way merge against it, then un-commit
                await WithExcluded(() => MoveIncluded(() => g.RunAsync("switch", "--quiet", branch), $"Switched to {branch}"));
                if (existing.Ahead > 0 && existing.MergeBase is not null)
                    await g.RunAsync("reset", "--quiet", "--mixed", existing.MergeBase);
            }
            else if (r.CurrentBranch == r.DefaultBranch)
            {
                ctx.Step(r.Name, $"Create {branch}");
                await g.RunAsync("switch", "--quiet", "-c", branch);
            }
            else
            {
                ctx.Step(r.Name, $"Move changes onto new {branch} from {r.DefaultBranch}");
                await WithExcluded(() => MoveIncluded(async () =>
                {
                    await g.RunAsync("switch", "--quiet", r.DefaultBranch);
                    await g.RunAsync("switch", "--quiet", "-c", branch);
                }, $"Created {branch}"));
            }
            await GitOps.SetMetaAsync(g, key, desc, type);
        }
        ctx.Progress.Key = key;
    }
}

// ------------------------------------------------------------------ commit

public sealed class CommitOp : Operation<CommitRequest>
{
    public override string Kind => "commit";
    public override string? KeyOf(CommitRequest req) => req.Key;

    public override Task<OpPreview> PreviewAsync(Workspace w, CommitRequest req)
    {
        var legs = w.Legs(req.Key);
        var (desc0, type0) = DashboardBuilder.TicketMeta(legs.Select(l => l.Wip));
        var desc = string.IsNullOrWhiteSpace(req.Description) ? desc0 : req.Description!.Trim();
        var type = string.IsNullOrWhiteSpace(req.Type) ? type0 : TicketKeys.NormalizeType(req.Type);
        var msg = TicketKeys.CommitMessage(type, req.Key, desc);
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        var blockers = new List<string>();
        var current = legs.Where(l => l.Wip.IsCurrent).ToList();
        var dirty = current.Count(l => l.Repo.HasIncluded);
        if (legs.Count == 0) blockers.Add($"No in-flight ticket {req.Key}");
        else if (dirty == 0) blockers.Add("No uncommitted changes to commit");
        if (string.IsNullOrWhiteSpace(desc)) blockers.Add("Enter a description");

        foreach (var (r, wip) in current)
        {
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            if (r.HasIncluded)
                steps.Add(P.S(r.Name, $"Commit {OpUtil.Plural(r.Included.Count(), "file")}" + (wip.Ahead > 0 ? " (squashed with the existing commit)" : ""),
                    "git add <files>", $"git commit -m \"{msg}\""));
            steps.Add(P.S(r.Name, $"Switch back to {r.DefaultBranch}{P.Carry(r)}", $"git switch {r.DefaultBranch}"));
            steps.Add(P.S(r.Name, $"Pull latest {r.DefaultBranch} (fast-forward only)", $"git fetch origin {r.DefaultBranch}", "git merge --ff-only"));
            if (r.ExcludedChanges.Any())
                warnings.Add($"{r.Name}: stays uncommitted — {string.Join(", ", r.ExcludedChanges.Select(c => c.Path).Take(6))}");
        }
        return Task.FromResult(P.Make(Kind, $"Commit {req.Key} locally", msg, steps, warnings, blockers,
            $"Commit {OpUtil.Plural(dirty, "repo")}", data: new { description = desc, type, message = msg }));
    }

    public override async Task RunAsync(OpContext ctx, CommitRequest req)
    {
        var legs = OpUtil.RequireLegs(ctx.W, req.Key);
        var (desc0, type0) = DashboardBuilder.TicketMeta(legs.Select(l => l.Wip));
        var desc = string.IsNullOrWhiteSpace(req.Description) ? desc0 : req.Description!.Trim();
        var type = string.IsNullOrWhiteSpace(req.Type) ? type0 : TicketKeys.NormalizeType(req.Type);
        var msg = TicketKeys.CommitMessage(type, req.Key, desc);

        foreach (var (r, _) in legs)
            await GitOps.SetMetaAsync(ctx.Git(r), req.Key, desc, type);

        foreach (var (r0, w0) in legs.Where(l => l.Wip.IsCurrent))
        {
            var r = await ctx.Fresh(r0);
            var w = r.Wip(req.Key);
            if (w is null || !w.IsCurrent) continue;
            var g = ctx.Git(r);
            if (r.HasIncluded)
            {
                ctx.Step(r.Name, $"Commit {OpUtil.Plural(r.Included.Count(), "file")}");
                await GitOps.CommitSquashedAsync(g, r.Included.ToList(), msg, w.MergeBase, w.Ahead);
                r = await ctx.Fresh(r);
            }
            ctx.Step(r.Name, $"Switch back to {r.DefaultBranch}");
            await OpUtil.SwitchAsync(ctx, r, r.DefaultBranch);
            ctx.Step(r.Name, $"Pull latest {r.DefaultBranch}");
            await OpUtil.UpdateDefaultAsync(ctx, r);
        }
    }
}

// ------------------------------------------------------------------ resume / re-open

public sealed class ResumeOp : Operation<KeyRequest>
{
    public override string Kind => "resume";
    public override string? KeyOf(KeyRequest req) => req.Key;

    public override Task<OpPreview> PreviewAsync(Workspace w, KeyRequest req)
    {
        var legs = w.Legs(req.Key);
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        var blockers = new List<string>();
        if (legs.Count == 0) blockers.Add($"No in-flight ticket {req.Key}");
        var branch = TicketKeys.WipBranch(req.Key);
        var reopen = false;
        foreach (var (r, wip) in legs)
        {
            var pr = w.Pr(r, wip);
            if (pr?.State == "MERGED") blockers.Add($"{r.Name}: PR #{pr.Number} is already merged — complete the ticket instead");
            if (!r.Ok) blockers.Add($"{r.Name}: {r.Error}");
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            if (!wip.IsCurrent)
            {
                if (r.HasUnassigned)
                    blockers.Add($"{r.Name} has unassigned changes on {r.CurrentBranch ?? "a detached HEAD"} — assign or discard them first");
                else if (r.IsOnWip && r.HasIncluded)
                    steps.Add(P.S(r.Name, $"Commit {r.CurrentWipKey} locally (auto-park its {OpUtil.Plural(r.Included.Count(), "file")})"));
                steps.Add(P.S(r.Name, $"Switch to {branch}{P.Carry(r)}", $"git switch {branch}"));
            }
            if (wip.Ahead > 0)
            {
                reopen = true;
                steps.Add(P.S(r.Name, "Un-commit (changes become uncommitted again)", "git reset --mixed <merge-base>"));
            }
            if (wip.IsCurrent && wip.Ahead == 0) steps.Add(P.S(r.Name, "Already checked out"));
            if (pr?.State == "OPEN")
                warnings.Add($"{r.Name}: PR #{pr.Number} stays open. After re-committing, use “Update PRs” to push your changes.");
        }
        return Task.FromResult(P.Make(Kind, $"{(reopen ? "Re-open" : "Resume")} {req.Key}",
            "Switches every repo of this ticket to its wip branch so you can keep working.", steps, warnings, blockers,
            reopen ? "Re-open" : "Resume"));
    }

    public override async Task RunAsync(OpContext ctx, KeyRequest req)
    {
        var branch = TicketKeys.WipBranch(req.Key);
        foreach (var (r0, _) in OpUtil.RequireLegs(ctx.W, req.Key))
        {
            var r = await ctx.Fresh(r0);
            var w = r.Wip(req.Key) ?? throw new OpBlockedException($"{r.Name}: {branch} disappeared");
            var g = ctx.Git(r);
            if (!w.IsCurrent)
            {
                if (r.IsOnWip && r.HasIncluded)
                {
                    await OpUtil.ParkCurrentAsync(ctx, r);
                    r = await ctx.Fresh(r);
                }
                if (r.HasIncluded) throw new OpBlockedException($"{r.Name} has uncommitted changes on {r.CurrentBranch}");
                ctx.Step(r.Name, $"Switch to {branch}");
                await OpUtil.SwitchAsync(ctx, r, branch);
                r = await ctx.Fresh(r);
                w = r.Wip(req.Key)!;
            }
            if (w.Ahead > 0)
            {
                ctx.Step(r.Name, "Un-commit");
                var mb = await g.RunAsync("merge-base", r.BaseRef, "HEAD");
                await g.RunAsync("reset", "--quiet", "--mixed", mb);
            }
        }
    }
}

// ------------------------------------------------------------------ discard ticket

public sealed class DiscardTicketOp : Operation<DiscardTicketRequest>
{
    public override string Kind => "discard-ticket";
    public override string? KeyOf(DiscardTicketRequest req) => req.Key;

    private static List<(RepoSnapshot Repo, WipBranch Wip)> Legs(Workspace w, DiscardTicketRequest req) =>
        w.Legs(req.Key).Where(l => req.Repos is null || req.Repos.Contains(l.Repo.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    public override Task<OpPreview> PreviewAsync(Workspace w, DiscardTicketRequest req)
    {
        var legs = Legs(w, req);
        var steps = new List<PreviewStep>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (legs.Count == 0) blockers.Add($"Nothing to discard for {req.Key}");
        foreach (var (r, wip) in legs)
        {
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            if (wip.IsCurrent)
            {
                if (r.HasIncluded) steps.Add(P.S(r.Name, $"Back up and discard {OpUtil.Plural(r.Included.Count(), "uncommitted file")}"));
                steps.Add(P.S(r.Name, $"Switch to {r.DefaultBranch}{P.Carry(r)}"));
            }
            steps.Add(P.S(r.Name, $"Delete {wip.Branch} ({(wip.Ahead > 0 ? "its commit" : "the branch")} stays recoverable for {w.Config.BackupRetentionDays} days)",
                $"git update-ref {GitOps.TrashRefPrefix}{req.Key}/<time> {wip.Sha[..Math.Min(8, wip.Sha.Length)]}", $"git branch -D {wip.Branch}"));
            var pr = w.Pr(r, wip);
            if (pr?.State == "OPEN")
            {
                if (req.ClosePrs) steps.Add(P.S(r.Name, $"Close PR #{pr.Number}"));
                else warnings.Add($"{r.Name}: PR #{pr.Number} stays open on GitHub");
            }
            if (req.DeleteRemote && (wip.PushedSha is not null || pr is not null))
                steps.Add(P.S(r.Name, $"Delete remote branch {wip.RemoteBranch}"));
        }
        var all = req.Repos is null || legs.Count == w.Legs(req.Key).Count;
        return Task.FromResult(P.Make(Kind, all ? $"Discard {req.Key}" : $"Discard {req.Key} in {string.Join(", ", legs.Select(l => l.Repo.Name))}",
            "Removes the work from your working copies. Everything is backed up first and can be restored from the Recovery tab.",
            steps, warnings, blockers, "Discard", confirm: req.Key, destructive: true,
            data: new { hasPrs = legs.Any(l => w.Pr(l.Repo, l.Wip)?.State == "OPEN"), pushed = legs.Any(l => l.Wip.PushedSha is not null) }));
    }

    public override async Task RunAsync(OpContext ctx, DiscardTicketRequest req)
    {
        var (desc, type) = DashboardBuilder.TicketMeta(ctx.W.Legs(req.Key).Select(l => l.Wip));
        foreach (var (r0, _) in Legs(ctx.W, req))
        {
            var r = await ctx.Fresh(r0);
            var w = r.Wip(req.Key);
            if (w is null) continue;
            var pr = ctx.W.Pr(r, w);
            var g = ctx.Git(r);
            if (w.IsCurrent)
            {
                if (r.Changes.Count > 0)
                {
                    ctx.Step(r.Name, "Back up uncommitted changes");
                    await GitOps.BackupAsync(g, r.Changes, $"before discarding {req.Key}", r.CurrentBranch);
                }
                if (r.HasIncluded)
                {
                    ctx.Step(r.Name, "Discard uncommitted changes");
                    await GitOps.DiscardAsync(g, r.Included.ToList());
                    r = await ctx.Fresh(r);
                }
                ctx.Step(r.Name, $"Switch to {r.DefaultBranch}");
                await OpUtil.SwitchAsync(ctx, r, r.DefaultBranch);
            }
            ctx.Step(r.Name, $"Delete {w.Branch}");
            var trashRef = await GitOps.TrashBranchAsync(g, req.Key, w.Sha);
            ctx.W.Trash.Add(new TrashEntry(r.Name, r.Path, req.Key, trashRef, w.Sha, desc, type, w.RemoteName, DateTimeOffset.Now));
            await g.RunAsync("branch", "-D", w.Branch);

            if (r.GitHubOwner is not null && r.GitHubRepo is not null)
            {
                if (req.ClosePrs && pr?.State == "OPEN")
                {
                    ctx.Step(r.Name, $"Close PR #{pr.Number}");
                    await ctx.W.GitHub.ClosePrAsync(r.GitHubOwner, r.GitHubRepo, pr.Number);
                }
                if (req.DeleteRemote && (w.PushedSha is not null || pr is not null))
                {
                    ctx.Step(r.Name, $"Delete remote branch {w.RemoteBranch}");
                    if (!await ctx.W.GitHub.DeleteBranchAsync(r.GitHubOwner, r.GitHubRepo, w.RemoteBranch)) ctx.Log("already gone");
                    await g.TryAsync("update-ref", "-d", $"refs/remotes/origin/{w.RemoteBranch}");
                }
            }
        }
    }
}

// ------------------------------------------------------------------ discard files

public sealed class DiscardChangesOp : Operation<DiscardChangesRequest>
{
    public override string Kind => "discard-changes";

    private static List<FileChange> Files(RepoSnapshot r, DiscardChangesRequest req)
    {
        var set = new HashSet<string>(req.Paths ?? [], StringComparer.OrdinalIgnoreCase);
        return r.Changes.Where(c => set.Contains(c.Path)).ToList();
    }

    public override Task<OpPreview> PreviewAsync(Workspace w, DiscardChangesRequest req)
    {
        var r = w.Require(req.Repo);
        var files = Files(r, req);
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (files.Count == 0) blockers.Add("No files selected");
        if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
        var excluded = files.Where(f => f.Excluded).ToList();
        if (excluded.Count > 0) warnings.Add($"Includes excluded files: {string.Join(", ", excluded.Select(f => f.Path))}");
        var steps = new List<PreviewStep>
        {
            P.S(r.Name, $"Back up {OpUtil.Plural(files.Count, "file")} (Recovery tab)"),
            P.S(r.Name, $"Discard {OpUtil.Plural(files.Count, "file")} on {r.CurrentBranch ?? "detached HEAD"}", "git restore --staged --worktree <files>", "delete untracked <files>"),
        };
        return Task.FromResult(P.Make(Kind, $"Discard changes in {r.Name}", string.Join("\n", files.Select(f => $"{f.StatusLetter} {f.Path}").Take(30)),
            steps, warnings, blockers, $"Discard {OpUtil.Plural(files.Count, "file")}", confirm: r.Name, destructive: true));
    }

    public override async Task RunAsync(OpContext ctx, DiscardChangesRequest req)
    {
        var r = await ctx.Fresh(ctx.W.Require(req.Repo));
        var files = Files(r, req);
        var g = ctx.Git(r);
        ctx.Step(r.Name, "Back up");
        var backup = await GitOps.BackupAsync(g, files, "before discarding changes", r.CurrentBranch);
        ctx.Log($"backup: {backup}");
        ctx.Step(r.Name, $"Discard {OpUtil.Plural(files.Count, "file")}");
        await GitOps.DiscardAsync(g, files);
    }
}

// ------------------------------------------------------------------ restore from Recovery

public sealed class RestoreOp : Operation<RestoreRequest>
{
    public override string Kind => "restore";

    public override async Task<OpPreview> PreviewAsync(Workspace w, RestoreRequest req)
    {
        var r = w.Require(req.Repo);
        var g = new GitRepo(w.Git, r.Path) { ReadOnly = true };
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        var blockers = new List<string>();
        if (req.Ref.StartsWith(GitOps.TrashRefPrefix))
        {
            var entry = w.Trash.All().FirstOrDefault(t => t.Ref == req.Ref && t.RepoPath == r.Path);
            var key = entry?.Key ?? req.Ref[GitOps.TrashRefPrefix.Length..].Split('/')[0];
            if (r.Wip(key) is not null) blockers.Add($"{TicketKeys.WipBranch(key)} already exists in {r.Name}");
            steps.Add(P.S(r.Name, $"Recreate {TicketKeys.WipBranch(key)} from the deleted branch", $"git branch {TicketKeys.WipBranch(key)} <sha>"));
            return P.Make(Kind, $"Restore {key} in {r.Name}", entry?.Description ?? "", steps, warnings, blockers, "Restore branch");
        }
        if (!req.Ref.StartsWith(GitOps.BackupRefPrefix)) blockers.Add("Not a Ticket Flow backup");
        var body = (await g.TryAsync("log", "-1", "--format=%B", req.Ref)).Out;
        var branchLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("branch:"))?["branch:".Length..].Trim();
        var files = PorcelainV2.ParseNameStatus((await g.TryAsync("diff", "--name-status", "-z", "--no-renames", req.Ref + "^", req.Ref)).StdOut);
        if (r.Changes.Count > 0) steps.Add(P.S(r.Name, "Back up current changes first"));
        steps.Add(P.S(r.Name, $"Restore {OpUtil.Plural(files.Count, "file")} into the working copy (on {r.CurrentBranch ?? "detached HEAD"})",
            $"git restore --source={req.Ref} --worktree <files>"));
        if (branchLine is not null && branchLine != r.CurrentBranch)
            warnings.Add($"This backup was taken on {branchLine}; you are on {r.CurrentBranch ?? "a detached HEAD"}.");
        var overwrite = files.Select(f => f.Path).Intersect(r.Changes.Select(c => c.Path), StringComparer.OrdinalIgnoreCase).ToList();
        if (overwrite.Count > 0) warnings.Add($"Overwrites current changes in: {string.Join(", ", overwrite.Take(8))}");
        return P.Make(Kind, $"Restore backup in {r.Name}", string.Join("\n", files.Select(f => $"{f.Status} {f.Path}").Take(30)),
            steps, warnings, blockers, "Restore files");
    }

    public override async Task RunAsync(OpContext ctx, RestoreRequest req)
    {
        var r = await ctx.Fresh(ctx.W.Require(req.Repo));
        var g = ctx.Git(r);
        if (req.Ref.StartsWith(GitOps.TrashRefPrefix))
        {
            var entry = ctx.W.Trash.All().FirstOrDefault(t => t.Ref == req.Ref && t.RepoPath == r.Path);
            var key = entry?.Key ?? req.Ref[GitOps.TrashRefPrefix.Length..].Split('/')[0];
            var sha = await g.RunAsync("rev-parse", req.Ref);
            ctx.Step(r.Name, $"Recreate {TicketKeys.WipBranch(key)}");
            await g.RunAsync("branch", TicketKeys.WipBranch(key), sha);
            if (entry is not null)
            {
                await GitOps.SetMetaAsync(g, key, entry.Description ?? "", entry.Type ?? "feat");
                if (entry.RemoteName is not null) await GitOps.SetConfigAsync(g, TicketKeys.WipBranch(key), "ticketflowRemote", entry.RemoteName);
            }
            await g.RunAsync("update-ref", "-d", req.Ref);
            ctx.W.Trash.Mutate(l => l.RemoveAll(t => t.Ref == req.Ref && t.RepoPath == r.Path));
            return;
        }
        if (r.Changes.Count > 0)
        {
            ctx.Step(r.Name, "Back up current changes");
            await GitOps.BackupAsync(g, r.Changes, "before restoring a backup", r.CurrentBranch);
        }
        ctx.Step(r.Name, "Restore files");
        var n = await GitOps.RestoreBackupAsync(g, req.Ref);
        ctx.Log($"{n} files restored");
    }
}

// ------------------------------------------------------------------ sync (fetch + fast-forward default branches, prune old backups)

public sealed class SyncOp : Operation<SyncRequest>
{
    public override string Kind => "sync";

    private static IEnumerable<RepoSnapshot> Targets(Workspace w, SyncRequest req) =>
        w.Snapshots.Where(s => s.Ok && (req.Repo is null || string.Equals(s.Name, req.Repo, StringComparison.OrdinalIgnoreCase)));

    public override Task<OpPreview> PreviewAsync(Workspace w, SyncRequest req)
    {
        var steps = Targets(w, req).Select(r => P.S(r.Name, $"Fetch origin and fast-forward {r.DefaultBranch}")).ToList();
        return Task.FromResult(P.Make(Kind, req.Repo is null ? "Fetch all repos" : $"Update {req.Repo}", "", steps, [], [], "Fetch"));
    }

    public override async Task RunAsync(OpContext ctx, SyncRequest req)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, ctx.Config.BackupRetentionDays));
        foreach (var r in Targets(ctx.W, req))
        {
            var g = ctx.Git(r);
            ctx.Step(r.Name, "Fetch");
            var f = await g.TryAsync("fetch", "--quiet", "--prune", "origin");
            if (!f.Ok)
            {
                ctx.Warn($"{r.Name}: fetch failed — {f.StdErr.Trim()}");
                continue;
            }
            if (r.InProgress is null)
            {
                ctx.Step(r.Name, $"Update {r.DefaultBranch}");
                await OpUtil.UpdateDefaultAsync(ctx, r);
            }
            var old = (await GitOps.ListRefsAsync(g, GitOps.RefRoot)).Where(b => b.Date < cutoff).ToList();
            if (old.Count > 0)
            {
                ctx.Step(r.Name, $"Prune {OpUtil.Plural(old.Count, "old backup")}");
                foreach (var b in old) await g.TryAsync("update-ref", "-d", b.Ref);
                ctx.W.Trash.Mutate(l => l.RemoveAll(t => t.RepoPath == r.Path && old.Any(o => o.Ref == t.Ref)));
            }
        }
    }
}

// ------------------------------------------------------------------ rebase in working copy (conflict resolution)

public sealed class ResolveOp : Operation<ResolveRequest>
{
    public override string Kind => "resolve";
    public override string? KeyOf(ResolveRequest req) => req.Key;

    public override Task<OpPreview> PreviewAsync(Workspace w, ResolveRequest req)
    {
        var r = w.Require(req.Repo);
        var wip = r.Wip(req.Key);
        var blockers = new List<string>();
        if (wip is null) blockers.Add($"{req.Key} has no branch in {r.Name}");
        if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} already in progress");
        if (r.HasIncluded && !(wip?.IsCurrent ?? false)) blockers.Add($"{r.Name} has uncommitted changes on {r.CurrentBranch} — commit or assign them first");
        if (wip is { IsCurrent: true } && r.HasIncluded) blockers.Add("Commit locally first");
        var steps = new List<PreviewStep>
        {
            P.S(r.Name, $"Fetch origin/{r.DefaultBranch}"),
            P.S(r.Name, $"Switch to {TicketKeys.WipBranch(req.Key)}{P.Carry(r)}"),
            P.S(r.Name, $"Rebase onto origin/{r.DefaultBranch}", $"git rebase --autostash origin/{r.DefaultBranch}"),
        };
        return Task.FromResult(P.Make(Kind, $"Rebase {req.Key} in {r.Name}",
            "If there are conflicts, the repo is left mid-rebase: fix the files in your editor, then press Continue on the repo.",
            steps, [], blockers, "Start rebase"));
    }

    public override async Task RunAsync(OpContext ctx, ResolveRequest req)
    {
        var r = await ctx.Fresh(ctx.W.Require(req.Repo));
        var g = ctx.Git(r);
        ctx.Step(r.Name, "Fetch");
        await g.TryAsync("fetch", "--quiet", "origin", r.DefaultBranch);
        var branch = TicketKeys.WipBranch(req.Key);
        if (r.CurrentBranch != branch)
        {
            ctx.Step(r.Name, $"Switch to {branch}");
            await OpUtil.SwitchAsync(ctx, r, branch);
        }
        ctx.Step(r.Name, $"Rebase onto origin/{r.DefaultBranch}");
        var rb = await g.TryAsync("rebase", "--autostash", $"origin/{r.DefaultBranch}");
        if (!rb.Ok)
            ctx.Warn($"{r.Name}: conflicts — resolve them in your editor, then press Continue on the {r.Name} repo card (or Abort).");
        else
            ctx.Log("Rebased cleanly. Use “Update PRs” to push.");
    }
}

public sealed class RebaseControlOp : Operation<RebaseControlRequest>
{
    public override string Kind => "rebase";

    public override Task<OpPreview> PreviewAsync(Workspace w, RebaseControlRequest req)
    {
        var r = w.Require(req.Repo);
        var blockers = new List<string>();
        if (r.InProgress != "rebase") blockers.Add($"{r.Name} is not mid-rebase");
        var conflicted = r.Changes.Where(c => c.Kind == ChangeKind.Conflicted).Select(c => c.Path).ToList();
        var cont = req.Action == "continue";
        return Task.FromResult(P.Make(Kind, cont ? $"Continue rebase in {r.Name}" : $"Abort rebase in {r.Name}",
            cont ? (conflicted.Count > 0 ? $"Marks as resolved: {string.Join(", ", conflicted)}" : "") : "Returns the branch to how it was before the rebase.",
            [P.S(r.Name, cont ? "git add <conflicted files> && git rebase --continue" : "git rebase --abort")], [], blockers,
            cont ? "Continue" : "Abort rebase"));
    }

    public override async Task RunAsync(OpContext ctx, RebaseControlRequest req)
    {
        var r = await ctx.Fresh(ctx.W.Require(req.Repo));
        var g = ctx.Git(r);
        if (req.Action != "continue")
        {
            ctx.Step(r.Name, "Abort rebase");
            await g.RunAsync("rebase", "--abort");
            return;
        }
        var conflicted = r.Changes.Where(c => c.Kind == ChangeKind.Conflicted).Select(c => c.Path).ToList();
        foreach (var path in conflicted)
        {
            var text = File.Exists(Path.Combine(r.Path, path)) ? await File.ReadAllTextAsync(Path.Combine(r.Path, path)) : "";
            if (text.Contains("<<<<<<< ") && text.Contains(">>>>>>> "))
                throw new OpBlockedException($"{path} still contains conflict markers");
        }
        ctx.Step(r.Name, "Continue rebase");
        if (conflicted.Count > 0)
            await g.RunAsync(["add", "--pathspec-from-file=-", "--pathspec-file-nul"], stdin: GitRepo.Nul(conflicted));
        var c = await g.TryAsync("rebase", "--continue");
        if (!c.Ok) ctx.Warn($"{r.Name}: more conflicts — resolve them and press Continue again.");
    }
}

// ------------------------------------------------------------------ ticket metadata

public sealed class MetaOp : Operation<MetaRequest>
{
    public override string Kind => "meta";
    public override string? KeyOf(MetaRequest req) => req.Key;

    public override Task<OpPreview> PreviewAsync(Workspace w, MetaRequest req)
    {
        var legs = w.Legs(req.Key);
        var blockers = new List<string>();
        if (legs.Count == 0) blockers.Add($"No in-flight ticket {req.Key}");
        if (string.IsNullOrWhiteSpace(req.Description)) blockers.Add("Description can't be empty");
        var warnings = legs.Where(l => l.Wip.RemoteName is not null && !l.Wip.RemoteName.StartsWith(TicketKeys.NormalizeType(req.Type) + "/"))
            .Select(l => $"{l.Repo.Name}: already pushed as {l.Wip.RemoteName}; the remote branch name won't change").ToList();
        return Task.FromResult(P.Make(Kind, $"Update {req.Key}", TicketKeys.CommitMessage(req.Type, req.Key, req.Description ?? ""),
            legs.Select(l => P.S(l.Repo.Name, "Save description/type")).ToList(), warnings, blockers, "Save"));
    }

    public override async Task RunAsync(OpContext ctx, MetaRequest req)
    {
        foreach (var (r, _) in OpUtil.RequireLegs(ctx.W, req.Key))
        {
            ctx.Step(r.Name, "Save description/type");
            await GitOps.SetMetaAsync(ctx.Git(r), req.Key, req.Description, req.Type);
        }
    }
}
