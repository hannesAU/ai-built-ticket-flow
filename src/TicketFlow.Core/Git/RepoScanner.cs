using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TicketFlow.Core.Git;

public sealed class WipBranch
{
    public required string Key { get; init; }
    public string Branch => TicketKeys.WipBranch(Key);
    public required string Sha { get; init; }
    /// <summary>Tree id of the branch tip (compared with what "Build &amp; test" tested).</summary>
    public string? Tree { get; init; }
    public string? MergeBase { get; set; }
    /// <summary>Commits on the branch not on the base (origin/default).</summary>
    public int Ahead { get; set; }
    /// <summary>Commits on the base not on the branch.</summary>
    public int Behind { get; set; }
    public string Subject { get; set; } = "";
    public string? Description { get; set; }
    public string? Type { get; set; }
    /// <summary>Last sha Ticket Flow pushed to the remote branch.</summary>
    public string? PushedSha { get; set; }
    /// <summary>Remote branch name recorded at first push.</summary>
    public string? RemoteName { get; set; }
    public bool IsCurrent { get; set; }
    public List<(char Status, string Path)> Files { get; set; } = [];

    public string EffectiveType => TicketKeys.NormalizeType(Type);
    public string RemoteBranch => RemoteName ?? TicketKeys.RemoteBranch(EffectiveType, Key);
}

public sealed class RepoSnapshot
{
    public required RepoConfig Config { get; init; }
    public string Name => Config.DisplayName;
    public string Path => Config.Path;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string DefaultBranch { get; set; } = "master";
    public bool HasOriginDefault { get; set; }
    public string BaseRef => HasOriginDefault ? $"origin/{DefaultBranch}" : DefaultBranch;
    public string? CurrentBranch { get; set; }
    public string? HeadSha { get; set; }
    public List<FileChange> Changes { get; set; } = [];
    public int DefaultAhead { get; set; }
    public int DefaultBehind { get; set; }
    public string? InProgress { get; set; }
    public string? GitHubOwner { get; set; }
    public string? GitHubRepo { get; set; }
    public List<WipBranch> Wips { get; set; } = [];
    /// <summary>Remote-tracking feat/…, fix/… and chore/… branch names (without "origin/").</summary>
    public HashSet<string> RemoteTicketBranches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Hash of HEAD + the content of every included change; only computed while a wip branch is checked out.</summary>
    public string? Fingerprint { get; set; }

    public IEnumerable<FileChange> Included => Changes.Where(c => !c.Excluded);
    public IEnumerable<FileChange> ExcludedChanges => Changes.Where(c => c.Excluded);
    public bool HasIncluded => Changes.Any(c => !c.Excluded);
    public string? CurrentWipKey => TicketKeys.TryKeyFromBranch(CurrentBranch, out var k) ? k : null;
    public bool IsOnWip => CurrentWipKey is not null;
    public bool HasUnassigned => Ok && !IsOnWip && HasIncluded;
    public WipBranch? Wip(string key) => Wips.FirstOrDefault(w => w.Key == key);
}

public sealed partial class RepoScanner(GitCli cli)
{
    private readonly ConcurrentDictionary<string, (string GitDir, string? Owner, string? Repo, string? Default)> _cache = new();

    public void ClearCache() => _cache.Clear();

    public async Task<RepoSnapshot> ScanAsync(RepoConfig rc, ExcludeMatcher excluded, CancellationToken ct = default)
    {
        var snap = new RepoSnapshot { Config = rc };
        try
        {
            if (!Directory.Exists(rc.Path))
            {
                snap.Error = "Folder not found";
                return snap;
            }
            var g = new GitRepo(cli, rc.Path) { ReadOnly = true };

            if (!_cache.TryGetValue(rc.Path, out var info))
            {
                var gitDir = await g.TryAsync("rev-parse", "--absolute-git-dir");
                if (!gitDir.Ok)
                {
                    snap.Error = "Not a git repository";
                    return snap;
                }
                var url = (await g.TryAsync("remote", "get-url", "origin")).Out;
                var (owner, repo) = ParseRemote(url);
                var def = await DetectDefaultBranchAsync(g);
                info = (gitDir.Out, owner, repo, def);
                _cache[rc.Path] = info;
            }
            snap.GitHubOwner = info.Owner;
            snap.GitHubRepo = info.Repo;
            snap.DefaultBranch = string.IsNullOrWhiteSpace(rc.DefaultBranch) ? info.Default ?? "master" : rc.DefaultBranch!;

            var status = PorcelainV2.Parse((await g.RunAsync(PorcelainV2.Args)));
            snap.CurrentBranch = status.Head;
            snap.HeadSha = status.Oid;
            snap.Changes = status.Changes
                .Select(c => c with { Excluded = excluded.IsExcluded(c.Path) && (c.OrigPath is null || excluded.IsExcluded(c.OrigPath)) })
                .ToList();

            snap.InProgress = DetectInProgress(info.GitDir);
            snap.HasOriginDefault = await g.RefExistsAsync($"refs/remotes/origin/{snap.DefaultBranch}");

            if (snap.HasOriginDefault && await g.RefExistsAsync($"refs/heads/{snap.DefaultBranch}"))
            {
                var ab = await g.TryAsync("rev-list", "--left-right", "--count", $"{snap.DefaultBranch}...origin/{snap.DefaultBranch}");
                if (ab.Ok && TryCounts(ab.Out, out var left, out var right))
                {
                    snap.DefaultAhead = left;
                    snap.DefaultBehind = right;
                }
            }

            var rb = await g.TryAsync(["for-each-ref", "--format=%(refname:short)", .. TicketKeys.Types.Select(t => $"refs/remotes/origin/{t}/")]);
            snap.RemoteTicketBranches = rb.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.StartsWith("origin/") ? x["origin/".Length..] : x)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            snap.Wips = await ScanWipsAsync(g, snap);
            if (snap.IsOnWip) snap.Fingerprint = WorkFingerprint(rc.Path, snap.HeadSha, snap.Included);
            snap.Ok = true;
        }
        catch (Exception ex)
        {
            snap.Error = ex.Message;
        }
        snap.ScannedAt = DateTimeOffset.Now;
        return snap;
    }

    private static async Task<List<WipBranch>> ScanWipsAsync(GitRepo g, RepoSnapshot snap)
    {
        var list = new List<WipBranch>();
        var refs = await g.TryAsync("for-each-ref", "--format=%(refname:short)%00%(objectname)%00%(tree)%00%(subject)", "refs/heads/wip/");
        if (!refs.Ok || refs.Out.Length == 0) return list;

        var meta = await ReadMetaAsync(g);

        foreach (var line in refs.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\0', 4);
            if (f.Length < 2 || !TicketKeys.TryKeyFromBranch(f[0], out var key)) continue;
            var w = new WipBranch
            {
                Key = key,
                Sha = f[1],
                Tree = f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                Subject = f.Length > 3 ? f[3] : "",
                IsCurrent = snap.CurrentBranch == f[0],
            };
            if (meta.TryGetValue(f[0], out var m))
            {
                w.Description = m.GetValueOrDefault("description");
                w.Type = m.GetValueOrDefault("ticketflowtype");
                w.PushedSha = m.GetValueOrDefault("ticketflowpushed");
                w.RemoteName = m.GetValueOrDefault("ticketflowremote");
            }

            var mb = await g.TryAsync("merge-base", snap.BaseRef, f[0]);
            if (mb.Ok)
            {
                w.MergeBase = mb.Out;
                var ab = await g.TryAsync("rev-list", "--left-right", "--count", $"{snap.BaseRef}...{f[0]}");
                if (ab.Ok && TryCounts(ab.Out, out var behind, out var ahead))
                {
                    w.Behind = behind;
                    w.Ahead = ahead;
                }
                if (w.Ahead > 0)
                {
                    var ns = await g.TryAsync("diff", "--name-status", "-z", "--no-renames", w.MergeBase, w.Sha);
                    if (ns.Ok) w.Files = PorcelainV2.ParseNameStatus(ns.StdOut);
                }
            }
            list.Add(w);
        }
        return list;
    }

    /// <summary>branch name → (lowercased variable → value) from <c>branch.wip/*.*</c> git config.</summary>
    public static async Task<Dictionary<string, Dictionary<string, string>>> ReadMetaAsync(GitRepo g)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var cfg = await g.TryAsync("config", "--local", "-z", "--get-regexp", @"^branch\.wip/");
        if (!cfg.Ok) return result;
        foreach (var entry in cfg.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = entry.IndexOf('\n');
            var name = nl < 0 ? entry : entry[..nl];
            var value = nl < 0 ? "" : entry[(nl + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot <= "branch.".Length) continue;
            var branch = name["branch.".Length..dot];
            var variable = name[(dot + 1)..].ToLowerInvariant();
            if (!result.TryGetValue(branch, out var d)) result[branch] = d = new(StringComparer.Ordinal);
            d[variable] = value;
        }
        return result;
    }

    private static async Task<string?> DetectDefaultBranchAsync(GitRepo g)
    {
        var sym = await g.TryAsync("symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (sym.Ok && sym.Out.StartsWith("origin/")) return sym.Out["origin/".Length..];
        foreach (var candidate in new[] { "master", "main" })
            if (await g.RefExistsAsync($"refs/remotes/origin/{candidate}") || await g.RefExistsAsync($"refs/heads/{candidate}"))
                return candidate;
        return null;
    }

    private static string? DetectInProgress(string gitDir)
    {
        if (Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-apply"))) return "rebase";
        if (File.Exists(System.IO.Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (File.Exists(System.IO.Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(System.IO.Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        if (File.Exists(System.IO.Path.Combine(gitDir, "index.lock"))) return "index.lock";
        return null;
    }

    private const long MaxHashedFileBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Cheap identity of "the code as it is now": HEAD plus the path and bytes of every included change
    /// (excluded local files don't count). Staging a file doesn't change it; editing one does.
    /// </summary>
    public static string WorkFingerprint(string root, string? head, IEnumerable<FileChange> included)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string s) => h.AppendData(System.Text.Encoding.UTF8.GetBytes(s + "\0"));
        Add(head ?? "-");
        foreach (var c in included.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            Add(c.Path);
            Add(c.OrigPath ?? "");
            try
            {
                var fi = new FileInfo(System.IO.Path.Combine(root, c.Path));
                if (!fi.Exists) Add("<deleted>");
                else if (fi.Length > MaxHashedFileBytes) Add($"<large:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}>");
                else h.AppendData(File.ReadAllBytes(fi.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Add("<unreadable>");
            }
        }
        return Convert.ToHexString(h.GetHashAndReset())[..20].ToLowerInvariant();
    }

    private static bool TryCounts(string s, out int left, out int right)
    {
        left = right = 0;
        var p = s.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 2 && int.TryParse(p[0], out left) && int.TryParse(p[1], out right);
    }

    [GeneratedRegex(@"[:/]([^/:]+)/([^/]+?)(?:\.git)?/?$")]
    private static partial Regex RemoteRegex();

    public static (string? Owner, string? Repo) ParseRemote(string url)
    {
        var m = RemoteRegex().Match(url.Trim());
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (null, null);
    }
}
