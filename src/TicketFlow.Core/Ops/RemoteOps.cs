using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;

namespace TicketFlow.Core.Ops;

public sealed record PushRequest(string Key, string? Description, string? Type, bool Draft, bool OverwriteRemote);

/// <summary>Move to PR / Update PRs: squash + rebase off-tree, push with lease, create or update the PR.</summary>
public sealed class PushOp : Operation<PushRequest>
{
    public override string Kind => "push";
    public override string? KeyOf(PushRequest req) => req.Key;

    private static (string Desc, string Type, string Message) Meta(Workspace w, PushRequest req)
    {
        var (d0, t0) = DashboardBuilder.TicketMeta(w.Legs(req.Key).Select(l => l.Wip));
        var desc = string.IsNullOrWhiteSpace(req.Description) ? d0 : req.Description!.Trim();
        var type = string.IsNullOrWhiteSpace(req.Type) ? t0 : TicketKeys.NormalizeType(req.Type);
        return (desc, type, TicketKeys.CommitMessage(type, req.Key, desc));
    }

    private static string RemoteFor(WipBranch w, string type) => w.RemoteName ?? TicketKeys.RemoteBranch(type, w.Key);

    public override Task<OpPreview> PreviewAsync(Workspace w, PushRequest req)
    {
        var legs = w.Legs(req.Key);
        var (desc, type, msg) = Meta(w, req);
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        var blockers = new List<string>();
        if (legs.Count == 0) blockers.Add($"No in-flight ticket {req.Key}");
        if (string.IsNullOrWhiteSpace(desc)) blockers.Add("Enter a description");
        var anyOpen = false;
        var legData = new List<object>();
        foreach (var (r, wip) in legs)
        {
            var pr = w.Pr(r, wip);
            if (pr?.State == "MERGED")
            {
                steps.Add(P.S(r.Name, $"PR #{pr.Number} already merged — skipped"));
                continue;
            }
            if (r.GitHubOwner is null) blockers.Add($"{r.Name} has no GitHub origin remote");
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            if (wip.IsCurrent && r.HasIncluded) blockers.Add($"{r.Name}: uncommitted changes — commit locally first");
            if (wip.Ahead == 0 && !(wip.IsCurrent && r.HasIncluded)) blockers.Add($"{r.Name}: {wip.Branch} has no changes — discard it from the ticket");
            var remote = RemoteFor(wip, type);
            if (wip.IsCurrent) steps.Add(P.S(r.Name, $"Switch back to {r.DefaultBranch}{P.Carry(r)}"));
            var prep = new List<string>();
            if (wip.Behind > 0) prep.Add($"rebase onto origin/{r.DefaultBranch} ({wip.Behind} new commits)");
            if (wip.Ahead > 1) prep.Add($"squash {wip.Ahead} commits");
            if (wip.Subject != msg && wip.Ahead >= 1) prep.Add("set commit message");
            steps.Add(prep.Count > 0
                ? P.S(r.Name, char.ToUpper(prep[0][0]) + string.Join(", ", prep)[1..] + " (without touching your working copy)",
                    $"GIT_INDEX_FILE=<temp> git read-tree origin/{r.DefaultBranch}", "git diff <merge-base> <commit> | git apply --cached",
                    "(fallback: 3-way merge --squash in a temporary worktree)", $"git commit-tree <tree> -p origin/{r.DefaultBranch}",
                    $"git update-ref refs/heads/{wip.Branch} <new>")
                : P.S(r.Name, "Commit is already on top of origin — no rebase needed"));
            steps.Add(P.S(r.Name, $"Push to origin/{remote}" + (wip.PushedSha is not null ? " (force-with-lease)" : ""),
                $"git push --force-with-lease=refs/heads/{remote}:<expected> origin {wip.Branch}:refs/heads/{remote}"));
            if (pr?.State == "OPEN")
            {
                anyOpen = true;
                steps.Add(P.S(r.Name, $"PR #{pr.Number} updates automatically" + (pr.Title != msg ? "; title updated" : "")));
            }
            else steps.Add(P.S(r.Name, $"Create {(req.Draft ? "draft " : "")}PR “{msg}” → {r.DefaultBranch}"));
            if (wip.RemoteName is not null && wip.RemoteName != TicketKeys.RemoteBranch(type, req.Key))
                warnings.Add($"{r.Name}: already pushed as {wip.RemoteName}; keeping that branch name");
            legData.Add(new { repo = r.Name, remote, @base = r.DefaultBranch, files = wip.Files.Count, pr = pr?.Number });
        }
        if (!w.State().GitHub.Ok) warnings.Add("GitHub status is currently failing: " + w.State().GitHub.Error);
        return Task.FromResult(P.Make(Kind, anyOpen ? $"Update PRs for {req.Key}" : $"Move {req.Key} to PR", msg, steps, warnings, blockers,
            anyOpen ? "Update PRs" : $"Create {OpUtil.Plural(legs.Count(l => w.Pr(l.Repo, l.Wip)?.State != "MERGED"), "PR")}",
            data: new { description = desc, type, message = msg, legs = legData }));
    }

    public override async Task RunAsync(OpContext ctx, PushRequest req)
    {
        var (desc, type, msg) = Meta(ctx.W, req);
        var gh = ctx.W.GitHub;

        ctx.Step("", "Check GitHub account");
        var viewer = await gh.GetViewerAsync();
        var want = ctx.Config.GitHub.User;
        if (!string.IsNullOrWhiteSpace(want) && !string.Equals(viewer, want, StringComparison.OrdinalIgnoreCase))
            throw new OpBlockedException($"GitHub token belongs to {viewer}, expected {want}");
        ctx.Log($"authenticated as {viewer}");

        var key = req.Key;
        var legs = OpUtil.RequireLegs(ctx.W, key);
        foreach (var (r, _) in legs)
            await GitOps.SetMetaAsync(ctx.Git(r), key, desc, type);

        // Assign only checks one repo's origin, so a first push can still hit a branch someone else owns.
        // Rather than block, move the whole ticket to the next free _n key before anything is pushed.
        var knownRemoteSha = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var firstPush = legs.Where(l => l.Wip.PushedSha is null && l.Wip.RemoteName is null && ctx.W.Pr(l.Repo, l.Wip)?.State != "MERGED").ToList();
        if (firstPush.Count > 0 && !req.OverwriteRemote)
        {
            var ticket = TicketKeys.TicketOf(key);
            ctx.Step("", $"Check {TicketKeys.RemoteBranch(type, key)} is free on origin");
            var remoteTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refsByRepo = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (r, _) in firstPush)
            {
                var refs = await OpUtil.RemoteTicketBranchesAsync(ctx.Git(r), ticket)
                           ?? throw new GitException($"{r.Name}: could not reach origin");
                refsByRepo[r.Name] = refs;
                remoteTaken.UnionWith(OpUtil.KeysOfRemoteBranches(refs.Keys));
            }
            var clashes = firstPush.Where(l => refsByRepo[l.Repo.Name].ContainsKey(RemoteFor(l.Wip, type))).ToList();
            if (clashes.Count > 0)
            {
                var newKey = TicketKeys.Allocate(ticket, k => AssignOp.IsTaken(ctx.W, k) || remoteTaken.Contains(k));
                ctx.Step("", $"Rename {key} to {newKey}");
                ctx.Warn($"origin already has {string.Join(", ", clashes.Select(l => $"{RemoteFor(l.Wip, type)} in {l.Repo.Name}"))} — renamed {key} to {newKey}");
                foreach (var (r, w) in legs)
                    await ctx.Git(r).RunAsync("branch", "-m", w.Branch, TicketKeys.WipBranch(newKey)); // branch config moves along
                key = newKey;
                ctx.Progress.Key = key;
                await Task.WhenAll(legs.Select(l => ctx.Fresh(l.Repo)));
                legs = OpUtil.RequireLegs(ctx.W, key);
            }
            // the new remote name is known to be free in these repos; otherwise reuse what ls-remote returned
            foreach (var (r, w) in legs.Where(l => refsByRepo.ContainsKey(l.Repo.Name)))
                knownRemoteSha[r.Name] = refsByRepo[r.Name].GetValueOrDefault(RemoteFor(w, type));
        }

        foreach (var (r0, _) in legs)
        {
            var r = await ctx.Fresh(r0);
            var w = r.Wip(key) ?? throw new OpBlockedException($"{r.Name}: branch disappeared");
            if (ctx.W.Pr(r, w)?.State == "MERGED") continue;
            var g = ctx.Git(r);
            var owner = r.GitHubOwner!;
            var repo = r.GitHubRepo!;

            if (w.IsCurrent)
            {
                if (r.HasIncluded) throw new OpBlockedException($"{r.Name}: uncommitted changes — commit locally first");
                ctx.Step(r.Name, $"Switch back to {r.DefaultBranch}");
                await OpUtil.SwitchAsync(ctx, r, r.DefaultBranch);
            }

            ctx.Step(r.Name, $"Fetch origin/{r.DefaultBranch}");
            var fetch = await g.TryAsync("fetch", "--quiet", "origin", r.DefaultBranch);
            if (!fetch.Ok) throw new GitException($"{r.Name}: fetch failed — {fetch.StdErr.Trim()}");

            var remote = RemoteFor(w, type);
            if (!knownRemoteSha.TryGetValue(r.Name, out var remoteSha))
            {
                var ls = await g.RunAsync("ls-remote", "origin", $"refs/heads/{remote}");
                remoteSha = ls.Split('\t', StringSplitOptions.TrimEntries).FirstOrDefault(s => s.Length >= 40);
            }
            if (remoteSha is not null && remoteSha != w.PushedSha && !req.OverwriteRemote)
                throw new OpBlockedException(w.PushedSha is null
                    ? $"{r.Name}: origin already has a branch {remote} that Ticket Flow didn't create. Tick “Overwrite remote branch” to replace it."
                    : $"{r.Name}: {remote} changed on origin since the last push (e.g. GitHub “Update branch” or a reviewer's commit). Tick “Overwrite remote branch” to replace it.");

            ctx.Step(r.Name, $"Rebase/squash onto origin/{r.DefaultBranch}");
            var newSha = await GitOps.NormalizeOffTreeAsync(g, r.Name, w.Branch, $"origin/{r.DefaultBranch}", msg);

            ctx.Step(r.Name, $"Push {remote}");
            if (newSha == remoteSha) ctx.Log("remote already up to date");
            else await g.RunAsync("push", "--quiet", $"--force-with-lease=refs/heads/{remote}:{remoteSha ?? ""}", "origin", $"{newSha}:refs/heads/{remote}");
            await GitOps.SetConfigAsync(g, w.Branch, "ticketflowPushed", newSha);
            await GitOps.SetConfigAsync(g, w.Branch, "ticketflowRemote", remote);

            ctx.Step(r.Name, "Pull request");
            var q = new PrQuery(owner, repo, remote);
            var existing = (await gh.GetPrsAsync([q])).GetValueOrDefault(q);
            if (existing?.State == "OPEN")
            {
                if (existing.Title != msg) await gh.UpdatePrTitleAsync(owner, repo, existing.Number, msg);
                ctx.Log($"PR #{existing.Number} updated");
                ctx.Link(existing.Url);
            }
            else
            {
                var pr = await gh.CreatePrAsync(owner, repo, remote, r.DefaultBranch, msg, "", req.Draft);
                ctx.Log($"created PR #{pr.Number}");
                ctx.Link(pr.Url);
                ctx.W.SetPr(q, pr);
            }
        }
    }
}

/// <summary>Merge every PR (in repo order), delete remote branches, then local branches, then archive.</summary>
public sealed class CompleteOp : Operation<KeyRequest>
{
    public override string Kind => "complete";
    public override string? KeyOf(KeyRequest req) => req.Key;

    public override Task<OpPreview> PreviewAsync(Workspace w, KeyRequest req)
    {
        var legs = w.Legs(req.Key);
        var steps = new List<PreviewStep>();
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (legs.Count == 0) blockers.Add($"No in-flight ticket {req.Key}");
        foreach (var (r, wip) in legs)
        {
            var pr = w.Pr(r, wip);
            var status = PrStatusMapper.Map(pr, r.Config.RequireApproval);
            if (pr is null) { blockers.Add($"{r.Name}: no PR"); continue; }
            if (!PrStatusMapper.IsReady(status)) blockers.Add($"{r.Name}: PR #{pr.Number} is {PrStatusMapper.Text(status, pr)}");
            else if (status != PrStatus.Merged && pr.HeadSha != wip.Sha) blockers.Add($"{r.Name}: local branch differs from PR #{pr.Number} — Update PRs first");
            if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
            if (wip.IsCurrent && r.HasIncluded) blockers.Add($"{r.Name}: uncommitted changes on {wip.Branch}");
            steps.Add(status == PrStatus.Merged
                ? P.S(r.Name, $"PR #{pr.Number} already merged")
                : P.S(r.Name, $"{Cap(r.Config.MergeMethod)}-merge PR #{pr.Number} into {r.DefaultBranch}", $"PUT /pulls/{pr.Number}/merge merge_method={r.Config.MergeMethod} sha={pr.HeadSha[..7]}"));
            steps.Add(P.S(r.Name, $"Delete remote branch {wip.RemoteBranch}"));
        }
        foreach (var (r, wip) in legs)
        {
            if (wip.IsCurrent) steps.Add(P.S(r.Name, $"Switch to {r.DefaultBranch}{P.Carry(r)}"));
            steps.Add(P.S(r.Name, $"Delete local {wip.Branch} (after all PRs are merged)"));
            steps.Add(P.S(r.Name, $"Pull latest {r.DefaultBranch}"));
        }
        steps.Add(P.S("", "Move ticket to the archive"));
        warnings.Add("Merging is not reversible from here. Repos are merged in the order configured in Settings.");
        return Task.FromResult(P.Make(Kind, $"Complete {req.Key}", "", steps, warnings, blockers, "Merge & complete"));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public override async Task RunAsync(OpContext ctx, KeyRequest req)
    {
        var gh = ctx.W.GitHub;
        var legs = OpUtil.RequireLegs(ctx.W, req.Key);
        var (desc, type) = DashboardBuilder.TicketMeta(legs.Select(l => l.Wip));

        ctx.Step("", "Re-check PR status on GitHub");
        var queries = legs.Select(l => new PrQuery(l.Repo.GitHubOwner!, l.Repo.GitHubRepo!, l.Wip.RemoteBranch)).ToList();
        var prs = await gh.GetPrsAsync(queries);
        var archived = new List<ArchivedLeg>();

        for (var i = 0; i < legs.Count; i++)
        {
            var (r, w) = legs[i];
            var pr = prs.GetValueOrDefault(queries[i]) ?? throw new OpBlockedException($"{r.Name}: PR not found");
            var status = PrStatusMapper.Map(pr, r.Config.RequireApproval);
            if (!PrStatusMapper.IsReady(status))
                throw new OpBlockedException($"{r.Name}: PR #{pr.Number} is now {PrStatusMapper.Text(status, pr)}");
            if (status != PrStatus.Merged && pr.HeadSha != w.Sha)
                throw new OpBlockedException($"{r.Name}: local branch differs from PR #{pr.Number}");
        }

        for (var i = 0; i < legs.Count; i++)
        {
            var (r, w) = legs[i];
            var pr = prs[queries[i]];
            string? mergeSha = null;
            if (pr.State != "MERGED")
            {
                ctx.Step(r.Name, $"Merge PR #{pr.Number}");
                mergeSha = await gh.MergePrAsync(r.GitHubOwner!, r.GitHubRepo!, pr.Number, r.Config.MergeMethod, pr.HeadSha, $"{pr.Title} (#{pr.Number})");
                ctx.Log($"merged as {mergeSha}");
            }
            ctx.Step(r.Name, $"Delete remote branch {w.RemoteBranch}");
            if (!await gh.DeleteBranchAsync(r.GitHubOwner!, r.GitHubRepo!, w.RemoteBranch)) ctx.Log("already deleted");
            ctx.Link(pr.Url);
            archived.Add(new ArchivedLeg(r.Name, w.RemoteBranch, pr.Number, pr.Url, mergeSha));
        }

        foreach (var (r0, _) in legs)
        {
            var r = await ctx.Fresh(r0);
            var w = r.Wip(req.Key);
            if (w is null) continue;
            var g = ctx.Git(r);
            if (w.IsCurrent)
            {
                if (r.HasIncluded) throw new OpBlockedException($"{r.Name}: uncommitted changes on {w.Branch} — not deleting it");
                ctx.Step(r.Name, $"Switch to {r.DefaultBranch}");
                await OpUtil.SwitchAsync(ctx, r, r.DefaultBranch);
            }
            ctx.Step(r.Name, $"Delete {w.Branch}");
            var trashRef = await GitOps.TrashBranchAsync(g, req.Key, w.Sha);
            ctx.W.Trash.Add(new TrashEntry(r.Name, r.Path, req.Key, trashRef, w.Sha, desc, type, w.RemoteName, DateTimeOffset.Now));
            await g.RunAsync("branch", "-D", w.Branch);
            await g.TryAsync("update-ref", "-d", $"refs/remotes/origin/{w.RemoteBranch}");
            ctx.Step(r.Name, $"Pull latest {r.DefaultBranch}");
            await OpUtil.UpdateDefaultAsync(ctx, r);
        }

        ctx.Step("", "Archive");
        ctx.W.Archive.Add(new ArchivedTicket(req.Key, TicketKeys.TicketOf(req.Key), desc, type, DateTimeOffset.Now, archived));
    }
}
