using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;
using TicketFlow.Core.Ops;

namespace TicketFlow.Tests;

/// <summary>In-memory GitHub that reads branch heads from the local bare "origin" repos.</summary>
public sealed class FakeGitHub(Func<string, string, string?> headOf) : IGitHubClient
{
    private int _next = 1;
    public Dictionary<PrQuery, PrInfo> Prs { get; } = new();
    public List<(string Repo, int Number, string Sha)> Merges { get; } = [];
    public List<string> DeletedBranches { get; } = [];

    public Task<string> GetViewerAsync(CancellationToken ct = default) => Task.FromResult("tester");

    private PrInfo Refresh(PrQuery q, PrInfo p) =>
        p.State == "OPEN" ? p with { HeadSha = headOf(q.Owner, q.HeadBranch) ?? p.HeadSha } : p;

    public Task<Dictionary<PrQuery, PrInfo>> GetPrsAsync(IReadOnlyCollection<PrQuery> queries, CancellationToken ct = default)
    {
        var d = new Dictionary<PrQuery, PrInfo>();
        foreach (var q in queries)
            if (Prs.TryGetValue(q, out var p))
                d[q] = Prs[q] = Refresh(q, p);
        return Task.FromResult(d);
    }

    public Task<PrInfo> CreatePrAsync(string owner, string repo, string head, string @base, string title, string body, bool draft, CancellationToken ct = default)
    {
        var q = new PrQuery(owner, repo, head);
        var pr = new PrInfo(_next++, $"https://example/{owner}/{repo}/pull/{_next - 1}", title, "OPEN", draft, "MERGEABLE", "BLOCKED",
            "REVIEW_REQUIRED", "SUCCESS", headOf(owner, head) ?? "", @base, null);
        Prs[q] = pr;
        return Task.FromResult(pr);
    }

    public Task UpdatePrTitleAsync(string owner, string repo, int number, string title, CancellationToken ct = default)
    {
        var (q, p) = Prs.First(kv => kv.Key.Owner == owner && kv.Value.Number == number);
        Prs[q] = p with { Title = title };
        return Task.CompletedTask;
    }

    public Task<string> MergePrAsync(string owner, string repo, int number, string method, string expectedHeadSha, string title, CancellationToken ct = default)
    {
        var (q, p) = Prs.First(kv => kv.Key.Owner == owner && kv.Value.Number == number);
        if (p.HeadSha != expectedHeadSha) throw new GitHubException("Head branch was modified");
        Prs[q] = p with { State = "MERGED" };
        Merges.Add((owner, number, expectedHeadSha));
        return Task.FromResult("merged-" + number);
    }

    public Task ClosePrAsync(string owner, string repo, int number, CancellationToken ct = default)
    {
        var (q, p) = Prs.First(kv => kv.Key.Owner == owner && kv.Value.Number == number);
        Prs[q] = p with { State = "CLOSED" };
        return Task.CompletedTask;
    }

    public Task<bool> DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
    {
        DeletedBranches.Add($"{owner}/{branch}");
        return Task.FromResult(true);
    }

    public Dictionary<RepoRef, List<RemotePr>> OpenPrs { get; } = new();

    public Task<Dictionary<RepoRef, List<RemotePr>>> GetOpenPrsAsync(IReadOnlyCollection<RepoRef> repos, string viewer, CancellationToken ct = default) =>
        Task.FromResult(repos.ToDictionary(r => r, r => OpenPrs.GetValueOrDefault(r) ?? []));

    public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("test-token");

    public void Approve(string owner, string head)
    {
        var (q, p) = Prs.First(kv => kv.Key.Owner == owner && kv.Key.HeadBranch == head);
        Prs[q] = p with { ReviewDecision = "APPROVED", MergeState = "CLEAN", Mergeable = "MERGEABLE" };
    }
}

/// <summary>Temp workspace with N repos, each a clone of its own bare origin.</summary>
public sealed class Harness : IAsyncDisposable
{
    public const string Excluded = "localconfig.json";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ticketflow-test-" + Guid.NewGuid().ToString("N")[..8]);
    public GitCli Git { get; } = new();
    public Workspace W { get; private set; } = null!;
    public OpRunner Runner { get; private set; } = null!;
    public FakeGitHub GitHub { get; private set; } = null!;

    public string Work(string repo) => Path.Combine(Root, repo, "work");
    public string Bare(string repo) => Path.Combine(Root, repo, "origin.git");

    public static async Task<Harness> CreateAsync(params string[] repos)
    {
        var h = new Harness();
        Directory.CreateDirectory(h.Root);
        foreach (var r in repos) await h.InitRepoAsync(r);

        var paths = AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data"));
        var store = new ConfigStore(paths);
        store.Save(new AppConfig
        {
            Repos = repos.Select(r => new RepoConfig { Path = h.Work(r), Name = r }).ToList(),
            Excluded = ["**/localconfig*.json"],
            ExcludeFromClaudeHook = false,
            GitHub = new GitHubConfig { User = "" },
        });
        h.GitHub = new FakeGitHub((owner, branch) =>
        {
            var r = h.Git.RunAsync(h.Bare(owner), ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"]).GetAwaiter().GetResult();
            return r.Ok ? r.Out : null;
        });
        h.W = new Workspace(store, h.Git, h.GitHub, new ArchiveStore(paths), new TrashStore(paths), new JournalStore(paths),
            new ChecksStore(paths));
        h.Runner = new OpRunner(h.W,
        [
            new AssignOp(), new CommitOp(), new ResumeOp(), new PushOp(), new CompleteOp(), new DiscardTicketOp(),
            new DiscardChangesOp(), new RestoreOp(), new SyncOp(), new ResolveOp(), new RebaseControlOp(), new MetaOp(),
            new CheckOp(),
        ]);
        await h.W.RescanAllAsync();
        return h;
    }

    private async Task InitRepoAsync(string name)
    {
        // owner is parsed from the remote URL's parent folder => the repo name
        Directory.CreateDirectory(Path.Combine(Root, name));
        await Run(Path.Combine(Root, name), "init", "--quiet", "--bare", "-b", "master", Bare(name));
        await Run(Path.Combine(Root, name), "clone", "--quiet", "--config", "core.autocrlf=false", Bare(name).Replace('\\', '/'), Work(name));
        await Configure(Work(name));
        await File.WriteAllTextAsync(Path.Combine(Work(name), "a.txt"), "line1\nline2\nline3\n");
        await File.WriteAllTextAsync(Path.Combine(Work(name), Excluded), "{ \"setting\": 1 }\n");
        await Run(Work(name), "add", "-A");
        await Run(Work(name), "commit", "--quiet", "-m", "initial");
        await Run(Work(name), "push", "--quiet", "-u", "origin", "master");
    }

    public async Task Configure(string dir)
    {
        await Run(dir, "config", "user.name", "Tester");
        await Run(dir, "config", "user.email", "tester@example.com");
        await Run(dir, "config", "core.autocrlf", "false");
    }

    public async Task<string> Run(string cwd, params string[] args)
    {
        var r = await Git.RunAsync(cwd, args);
        if (!r.Ok) throw new Exception($"git {string.Join(' ', args)} failed: {r.StdErr}");
        return r.Out;
    }

    public Task Write(string repo, string file, string content) =>
        File.WriteAllTextAsync(Path.Combine(Work(repo), file), content);

    public string Read(string repo, string file) => File.ReadAllText(Path.Combine(Work(repo), file));

    public Task<string> Branch(string repo) => Run(Work(repo), "rev-parse", "--abbrev-ref", "HEAD");

    /// <summary>Pushes a commit to origin/master from a second clone (simulates a teammate).</summary>
    public async Task TeammateCommit(string repo, string file, string content)
    {
        var other = Path.Combine(Root, repo, "teammate");
        if (!Directory.Exists(other))
        {
            await Run(Path.Combine(Root, repo), "clone", "--quiet", "--config", "core.autocrlf=false", Bare(repo).Replace('\\', '/'), other);
            await Configure(other);
        }
        await Run(other, "pull", "--quiet", "--ff-only");
        await File.WriteAllTextAsync(Path.Combine(other, file), content);
        await Run(other, "add", "-A");
        await Run(other, "commit", "--quiet", "-m", $"teammate edits {file}");
        await Run(other, "push", "--quiet", "origin", "master");
    }

    public async Task<OpPreview> Preview(string kind, object req)
    {
        await W.RescanAllAsync();
        return await Runner.PreviewAsync(kind, JsonSerializer.SerializeToElement(req, Json.Options));
    }

    public async Task<OpProgress> Op(string kind, object req)
    {
        var p = Runner.Start(kind, JsonSerializer.SerializeToElement(req, Json.Options));
        await Runner.LastTask;
        return p;
    }

    public async Task<OpProgress> OpOk(string kind, object req)
    {
        var p = await Op(kind, req);
        if (p.Status != "succeeded")
            throw new Exception($"{kind} failed: {p.Error}\n" +
                                string.Join("\n", p.Steps.Select(s => $"[{s.Status}] {s.Repo}: {s.Text}\n   " + string.Join("\n   ", s.Log))));
        return p;
    }

    public async Task<DashboardState> State()
    {
        await W.RescanAllAsync();
        return W.State();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { /* temp cleanup best effort */ }
    }
}
