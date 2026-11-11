namespace TicketFlow.Core.Git;

public sealed class RebaseConflictException(string repo, IReadOnlyList<string> files)
    : GitException($"Rebase conflict in {repo}: {string.Join(", ", files.Take(8))}{(files.Count > 8 ? " …" : "")}")
{
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>Higher-level git building blocks used by operations. All are careful never to lose work.</summary>
public static class GitOps
{
    public const string RefRoot = "refs/ticket-flow/";
    public const string BackupRefPrefix = RefRoot + "backups/";
    public const string TrashRefPrefix = RefRoot + "trash/";

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    public static async Task<StatusResult> StatusAsync(GitRepo g) =>
        PorcelainV2.Parse((await g.Cli.RunAsync(g.Path, PorcelainV2.Args, readOnly: true)).StdOut);

    /// <summary>
    /// Snapshot every dirty file (tracked, untracked, excluded) into a commit referenced by
    /// refs/ticket-flow/backups/…, using a temporary index so the working tree and real index are untouched.
    /// </summary>
    public static async Task<string?> BackupAsync(GitRepo g, IReadOnlyCollection<FileChange> changes, string label, string? branch)
    {
        if (changes.Count == 0) return null;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticketflow-idx-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tmp };
        try
        {
            var head = await g.ResolveAsync("HEAD");
            if (head is not null) await g.RunAsync(["read-tree", head], env: env);
            await g.RunAsync(["add", "-A", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"],
                stdin: GitRepo.Nul(changes.SelectMany(c => c.Paths).Distinct()), env: env);
            var tree = await g.RunAsync(["write-tree"], env: env);
            var msg = $"ticket-flow backup: {label}\n\nbranch: {branch ?? "(detached)"}\nfiles: {changes.Count}";
            var args = new List<string> { "commit-tree", tree, "-m", msg };
            if (head is not null) args.InsertRange(2, ["-p", head]);
            var commit = await g.RunAsync(args);
            var refName = BackupRefPrefix + Stamp();
            await g.RunAsync("update-ref", refName, commit);
            return refName;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Tree id of HEAD plus the given working-copy paths, built in a temporary index (working tree and real index untouched).
    /// Committing exactly those paths onto HEAD yields a commit with this tree.
    /// </summary>
    public static async Task<string?> WorkTreeAsync(GitRepo g, IReadOnlyCollection<string> paths)
    {
        var head = await g.ResolveAsync("HEAD");
        if (head is null) return null;
        if (paths.Count == 0) return await g.ResolveTreeAsync(head);
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticketflow-idx-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tmp };
        try
        {
            await g.RunAsync(["read-tree", head], env: env);
            await g.RunAsync(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], stdin: GitRepo.Nul(paths), env: env);
            return await g.RunAsync(["write-tree"], env: env);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>Keeps a branch tip reachable under refs/ticket-flow/trash/&lt;key&gt;/… before deleting the branch.</summary>
    public static async Task<string> TrashBranchAsync(GitRepo g, string key, string sha)
    {
        var refName = $"{TrashRefPrefix}{key}/{Stamp()}";
        await g.RunAsync("update-ref", refName, sha);
        return refName;
    }

    /// <summary>
    /// Switches branch while carrying the given dirty paths (the excluded files) byte-for-byte.
    /// Uses a plain switch when git can carry them itself; otherwise the files are saved, cleared via a
    /// safety stash, the branch is switched and the exact bytes are written back.
    /// Returns the carried paths whose committed content differs on the target (worth reviewing).
    /// </summary>
    public static async Task<List<string>> SwitchCarryingAsync(GitRepo g, string target, IReadOnlyCollection<string> carry, string tag)
    {
        if (carry.Count == 0)
        {
            await g.RunAsync("switch", "--quiet", target);
            return [];
        }

        var differing = await DifferingAsync(g, "HEAD", target);
        var overlap = carry.Where(differing.Contains).ToList();
        if (overlap.Count == 0)
        {
            var plain = await g.TryAsync("switch", "--quiet", target);
            if (plain.Ok) return [];
            g.Log?.Invoke("Plain switch refused; carrying files verbatim.");
        }

        await CarryVerbatimAsync(g, carry, $"ticket-flow:carry:{tag}", async () =>
        {
            var sw = await g.TryAsync("switch", "--quiet", target);
            if (!sw.Ok) throw new GitException($"Could not switch to {target}: {sw.StdErr.Trim()}", sw);
        });
        foreach (var p in overlap) g.Log?.Invoke($"kept your local {p} (it differs on {target})");
        return overlap;
    }

    public static async Task<HashSet<string>> DifferingAsync(GitRepo g, string a, string b) =>
        new((await g.RunAsync("diff", "--name-only", "-z", a, b)).Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs <paramref name="action"/> with <paramref name="paths"/> cleared from the working tree, then restores
    /// their exact previous bytes (or absence). A stash holds them meanwhile so nothing is lost on failure.
    /// </summary>
    public static async Task CarryVerbatimAsync(GitRepo g, IReadOnlyCollection<string> paths, string stashMessage, Func<Task> action)
    {
        var saved = paths.Distinct().ToDictionary(p => p, p =>
        {
            var full = System.IO.Path.Combine(g.Path, p);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        });
        await StashPathsAsync(g, saved.Keys, stashMessage);
        try
        {
            await action();
        }
        catch
        {
            await RestoreStashAsync(g, stashMessage);
            throw;
        }
        foreach (var (p, bytes) in saved)
        {
            var full = System.IO.Path.Combine(g.Path, p);
            if (bytes is null)
            {
                if (File.Exists(full)) File.Delete(full);
                continue;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes);
        }
        await DropStashAsync(g, stashMessage);
    }

    private static async Task<string?> FindStashAsync(GitRepo g, string message)
    {
        var list = await g.TryAsync("stash", "list", "--format=%gd%x00%s");
        foreach (var line in list.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\0');
            if (f.Length == 2 && f[1].EndsWith(message)) return f[0];
        }
        return null;
    }

    private static async Task DropStashAsync(GitRepo g, string message)
    {
        if (await FindStashAsync(g, message) is { } id) await g.TryAsync("stash", "drop", "--quiet", id);
    }

    private static async Task RestoreStashAsync(GitRepo g, string message)
    {
        if (await FindStashAsync(g, message) is { } id)
        {
            var r = await g.TryAsync("stash", "pop", "--quiet", id);
            if (!r.Ok) g.Log?.Invoke($"Your files are safe in the stash \"{message}\" ({id}).");
        }
    }

    public static async Task StashPathsAsync(GitRepo g, IReadOnlyCollection<string> paths, string message) =>
        await g.RunAsync(["stash", "push", "--include-untracked", "-m", message, "--pathspec-from-file=-", "--pathspec-file-nul"],
            stdin: GitRepo.Nul(paths.Distinct()));

    public static async Task PopAsync(GitRepo g, string stashMessage, string context)
    {
        var pop = await g.TryAsync("stash", "pop");
        if (!pop.Ok)
            throw new GitException(
                $"{context}, but re-applying your carried files conflicted. Nothing is lost: they are in the stash \"{stashMessage}\". " +
                "Resolve the conflicted files, then run `git stash drop`.", pop);
    }

    public sealed record UpdateResult(bool Updated, string Message, bool Warn = false);

    /// <summary>Fetches origin/default and fast-forwards the local default branch (never forces).</summary>
    public static async Task<UpdateResult> UpdateDefaultAsync(GitRepo g, string defaultBranch, string tag, Func<string, bool> isExcluded)
    {
        var fetch = await g.TryAsync("fetch", "--quiet", "origin", defaultBranch);
        if (!fetch.Ok) return new(false, $"fetch failed: {fetch.StdErr.Trim()}", true);
        var remote = await g.ResolveAsync($"refs/remotes/origin/{defaultBranch}");
        var local = await g.ResolveAsync($"refs/heads/{defaultBranch}");
        if (remote is null || local is null) return new(false, "no origin branch to update from");
        if (remote == local) return new(false, $"{defaultBranch} already up to date");
        if (!(await g.TryAsync("merge-base", "--is-ancestor", local, remote)).Ok)
        {
            if ((await g.TryAsync("merge-base", "--is-ancestor", remote, local)).Ok)
                return new(false, $"{defaultBranch} is ahead of origin (unpushed local commits)", true);
            return new(false, $"{defaultBranch} has diverged from origin — not updated", true);
        }

        var status = await StatusAsync(g);
        if (status.Head != defaultBranch)
        {
            await g.RunAsync("update-ref", $"refs/heads/{defaultBranch}", remote, local);
            return new(true, $"{defaultBranch} fast-forwarded");
        }

        var dirty = status.Changes.SelectMany(c => c.Paths).Distinct().ToList();
        var incoming = await DifferingAsync(g, "HEAD", remote);
        var overlap = dirty.Where(incoming.Contains).ToList();
        if (overlap.Count == 0)
        {
            await g.RunAsync("merge", "--ff-only", "--quiet", remote);
            return new(true, $"{defaultBranch} fast-forwarded");
        }
        var mine = overlap.Where(p => !isExcluded(p)).ToList();
        if (mine.Count > 0)
            return new(false, $"{defaultBranch} not updated — incoming changes touch files you've modified: {string.Join(", ", mine.Take(5))}", true);
        await CarryVerbatimAsync(g, overlap, $"ticket-flow:pull:{tag}", async () =>
        {
            var merge = await g.TryAsync("merge", "--ff-only", "--quiet", remote);
            if (!merge.Ok) throw new GitException($"Fast-forward failed: {merge.StdErr.Trim()}", merge);
        });
        return new(true, $"{defaultBranch} fast-forwarded (kept your local {string.Join(", ", overlap)}, which changed upstream — worth a look)", true);
    }

    /// <summary>
    /// Commits the given (included) changes so the branch is exactly one commit on top of <paramref name="mergeBase"/>.
    /// Anything already committed on the branch is squashed in.
    /// </summary>
    public static async Task CommitSquashedAsync(GitRepo g, IReadOnlyCollection<FileChange> included, string message, string? mergeBase, int ahead)
    {
        await g.RunAsync("reset", "--quiet");
        if (ahead > 0 && mergeBase is not null) await g.RunAsync("reset", "--quiet", "--soft", mergeBase);
        if (included.Count > 0)
            await g.RunAsync(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"],
                stdin: GitRepo.Nul(included.SelectMany(c => c.Paths).Distinct()));
        if ((await g.TryAsync("diff", "--cached", "--quiet")).Ok)
            throw new GitException("Nothing to commit (all changes are excluded or empty).");
        await g.RunAsync("commit", "--quiet", "-m", message);
    }

    /// <summary>Discards the given working-tree changes (caller must take a backup first).</summary>
    public static async Task DiscardAsync(GitRepo g, IReadOnlyCollection<FileChange> files)
    {
        var untracked = files.Where(f => f.Kind == ChangeKind.Untracked).Select(f => f.Path).ToList();
        var added = files.Where(f => f.Kind != ChangeKind.Untracked && f.X is 'A' or 'R' or 'C').Select(f => f.Path).ToList();
        var restore = files
            .Where(f => f.Kind != ChangeKind.Untracked && f.X is not ('A' or 'R' or 'C'))
            .Select(f => f.Path)
            .Concat(files.Where(f => f.OrigPath is not null).Select(f => f.OrigPath!))
            .Distinct()
            .ToList();

        if (added.Count > 0)
            await g.RunAsync(["rm", "-q", "-f", "--pathspec-from-file=-", "--pathspec-file-nul"], stdin: GitRepo.Nul(added));
        if (restore.Count > 0)
            await g.RunAsync(["restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"],
                stdin: GitRepo.Nul(restore));
        foreach (var p in untracked)
        {
            var full = System.IO.Path.Combine(g.Path, p);
            if (File.Exists(full)) File.Delete(full);
            g.Log?.Invoke($"deleted untracked {p}");
        }
    }

    /// <summary>Restores the files captured in a backup commit into the working tree.</summary>
    public static async Task<int> RestoreBackupAsync(GitRepo g, string commit)
    {
        var parent = await g.ResolveAsync(commit + "^");
        List<(char Status, string Path)> files;
        if (parent is null)
            files = (await g.RunAsync("ls-tree", "-r", "--name-only", "-z", commit))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => ('A', p)).ToList();
        else
            files = PorcelainV2.ParseNameStatus((await g.TryAsync("diff", "--name-status", "-z", "--no-renames", parent, commit)).StdOut);

        var present = files.Where(f => f.Status != 'D').Select(f => f.Path).ToList();
        if (present.Count > 0)
            await g.RunAsync(["restore", $"--source={commit}", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"],
                stdin: GitRepo.Nul(present));
        foreach (var (_, path) in files.Where(f => f.Status == 'D'))
        {
            var full = System.IO.Path.Combine(g.Path, path);
            if (File.Exists(full)) File.Delete(full);
        }
        return files.Count;
    }

    public static Task SetConfigAsync(GitRepo g, string branch, string variable, string value) =>
        g.RunAsync("config", "--local", $"branch.{branch}.{variable}", value);

    public static async Task SetMetaAsync(GitRepo g, string key, string description, string type)
    {
        var branch = TicketKeys.WipBranch(key);
        await SetConfigAsync(g, branch, "description", description.Trim());
        await SetConfigAsync(g, branch, "ticketflowType", TicketKeys.NormalizeType(type));
    }

    /// <summary>
    /// Rewrites <paramref name="branch"/> into exactly one commit on top of <paramref name="onto"/> with
    /// <paramref name="message"/>, without touching the working tree (the branch must not be checked out).
    /// Throws <see cref="RebaseConflictException"/> when the changes don't apply cleanly.
    /// </summary>
    public static async Task<string> NormalizeOffTreeAsync(GitRepo g, string repoName, string branch, string onto, string message)
    {
        var tip = await g.RunAsync("rev-parse", $"refs/heads/{branch}");
        var ontoSha = await g.RunAsync("rev-parse", onto + "^{commit}");
        var mb = await g.RunAsync("merge-base", ontoSha, tip);
        var ahead = int.Parse(await g.RunAsync("rev-list", "--count", $"{mb}..{tip}"));
        if (ahead == 0) throw new GitException($"{branch} has no commits to push.");
        var currentMsg = (await g.RunAsync("log", "-1", "--format=%B", tip)).Trim();
        var needsRebase = mb != ontoSha;
        if (ahead == 1 && !needsRebase && currentMsg == message.Trim()) return tip;

        var author = (await g.RunAsync("log", "-1", "--format=%an%x00%ae%x00%ad", "--date=raw", tip)).Split('\0');
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author[0],
            ["GIT_AUTHOR_EMAIL"] = author[1],
            ["GIT_AUTHOR_DATE"] = author[2],
        };

        var tree = needsRebase
            ? await ApplyOntoAsync(g, repoName, mb, tip, ontoSha)
            : await g.RunAsync("rev-parse", tip + "^{tree}");
        var commit = await g.RunAsync(["commit-tree", tree, "-p", ontoSha, "-m", message.Trim()], env: env);
        await g.RunAsync("update-ref", $"refs/heads/{branch}", commit, tip);
        return commit;
    }

    /// <summary>Computes the tree of (onto + diff(from..to)). Fast path: temp index + git apply; fallback: temp worktree squash-merge.</summary>
    private static async Task<string> ApplyOntoAsync(GitRepo g, string repoName, string from, string to, string onto)
    {
        var tmpIdx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticketflow-idx-{Guid.NewGuid():N}");
        var patch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticketflow-{Guid.NewGuid():N}.patch");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tmpIdx };
        try
        {
            await g.RunAsync(["read-tree", onto], env: env);
            await g.RunAsync("diff", "--binary", "--full-index", $"--output={patch}", from, to);
            var apply = await g.TryAsync(["apply", "--cached", "--binary", patch], env: env);
            if (apply.Ok) return await g.RunAsync(["write-tree"], env: env);
            g.Log?.Invoke("Patch does not apply directly; retrying with a 3-way merge in a temporary worktree.");
        }
        finally
        {
            try { File.Delete(tmpIdx); } catch { /* ignore */ }
            try { File.Delete(patch); } catch { /* ignore */ }
        }

        var wt = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ticketflow-wt-{Guid.NewGuid():N}");
        try
        {
            await g.RunAsync("worktree", "add", "--quiet", "--detach", wt, onto);
            var w = new GitRepo(g.Cli, wt, g.Log);
            var merge = await w.TryAsync("merge", "--squash", "--no-commit", to);
            if (!merge.Ok)
            {
                var conflicted = (await w.TryAsync("diff", "--name-only", "--diff-filter=U", "-z")).StdOut
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                throw new RebaseConflictException(repoName, conflicted.Count > 0 ? conflicted : ["(see log)"]);
            }
            return await w.RunAsync("write-tree");
        }
        finally
        {
            await g.TryAsync("worktree", "remove", "--force", wt);
            await g.TryAsync("worktree", "prune");
        }
    }

    public sealed record BackupInfo(string Ref, string Sha, DateTimeOffset Date, string Subject, string Body);

    public static async Task<List<BackupInfo>> ListRefsAsync(GitRepo g, string prefix)
    {
        var r = await g.TryAsync("for-each-ref", "--format=%(refname)%00%(objectname)%00%(creatordate:iso-strict)%00%(subject)%00%(body)%00%01", prefix);
        var list = new List<BackupInfo>();
        if (!r.Ok) return list;
        foreach (var rec in r.StdOut.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.TrimStart('\n', '\r').Split('\0');
            if (f.Length < 4 || f[0].Length == 0) continue;
            list.Add(new BackupInfo(f[0], f[1], DateTimeOffset.TryParse(f[2], out var d) ? d : DateTimeOffset.MinValue, f[3], f.Length > 4 ? f[4].Trim() : ""));
        }
        return list.OrderByDescending(b => b.Date).ToList();
    }
}
