using System.Text.Json.Nodes;
using Shouldly;
using TicketFlow.Core;
using TicketFlow.Core.Reviews;

namespace TicketFlow.Tests;

public class TicketReviewRuleTests
{
    [Fact]
    public void Deny_rules_are_absolute_per_repo_and_include_my_claude_read_denies()
    {
        var settings = Path.Combine(Path.GetTempPath(), $"ticketflow-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(settings, """{ "permissions": { "deny": ["Read(**/secrets.json)", "Bash(cat *)", "Read(./x)"] } }""");
        try
        {
            var rules = TicketReview.DenyRules([@"C:\code\api", @"D:\work\web"], ["**/localconfig*.json", "config/dev.json", "local.json"], settings);
            rules.ShouldBe([
                "Read(//c/code/api/**/localconfig*.json)",
                "Read(//c/code/api/config/dev.json)",
                "Read(//c/code/api/**/local.json)",
                "Read(//c/code/api/**/secrets.json)",
                "Read(//d/work/web/**/localconfig*.json)",
                "Read(//d/work/web/config/dev.json)",
                "Read(//d/work/web/**/local.json)",
                "Read(//d/work/web/**/secrets.json)",
            ]);
        }
        finally
        {
            File.Delete(settings);
        }
    }

    [Fact]
    public void Local_mode_only_reads_and_keeps_user_settings()
    {
        var args = ClaudeCli.Args(new ClaudeRunRequest("claude.exe", ".", "p", null, null, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), "log")
        {
            Mode = ReviewMode.Local,
            AddDirs = [@"C:\code\api"],
            Deny = ["Read(//c/code/api/**/local.json)"],
        });
        string After(string flag) => args[args.IndexOf(flag) + 1];
        After("--tools").ShouldBe("Read,Grep,Glob"); // no Bash at all
        After("--setting-sources").ShouldBe("user"); // keeps my deny rules and protected-file hook
        After("--permission-mode").ShouldBe("dontAsk");
        After("--add-dir").ShouldBe(@"C:\code\api");
        After("--disallowedTools").ShouldBe("Read(//c/code/api/**/local.json)");
        args.ShouldNotContain("--allowedTools");
    }

    [Fact]
    public void Prompt_warns_when_a_repo_is_not_checked_out_and_names_the_focus()
    {
        var legs = new List<TicketReviewLeg>
        {
            new("web", @"C:\code\web", "wip/PROJ-1", false, "master", "origin/master", "abc12345", "t1", null, 3, 1, null),
            new("api", @"C:\code\api", "wip/PROJ-1", true, "wip/PROJ-1", "origin/master", "def67890", "t2", "fp", 2, 0, null),
        };
        var prev = new PrReviewResult { Pr = "ticket:PROJ-1", HeadSha = "x", Concerns = [new("high", "Null deref", "d", "A.cs", 3, "api")] };
        var p = TicketReview.BuildPrompt("PROJ-1", "Do things", "feat", "web", legs, prev, null);

        p.ShouldContain("I started this review from web");
        p.ShouldContain("on master and does NOT show this ticket's code");
        p.ShouldContain("diff/web.diff (3 files)");
        p.ShouldContain("1 binary or very large files left out");
        p.ShouldContain("[high] api: Null deref (A.cs:3)");
        p.ShouldContain("one entry per concern of the previous automated review");
    }
}

public class TicketReviewServiceTests
{
    private sealed class FakeClaude : IClaudeRunner
    {
        public List<ClaudeRunRequest> Calls { get; } = [];
        public List<string> SnapshotFiles { get; } = [];

        public Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, Action<string> onProgress, CancellationToken ct)
        {
            Calls.Add(req);
            SnapshotFiles.Clear();
            SnapshotFiles.AddRange(Directory.EnumerateFiles(req.WorkDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(req.WorkDir, f).Replace('\\', '/')));
            var output = JsonNode.Parse("""
                { "summary": "S", "verdict": "looks-good", "risk": "low",
                  "concerns": [ { "severity": "low", "title": "t", "detail": "d", "repo": "A", "file": "a.txt", "line": 2 } ], "previousRequests": [] }
                """);
            return Task.FromResult(new ClaudeRunResult(true, null, output, 0.1, 3, "m", 0));
        }
    }

    [Fact]
    public async Task Reviews_every_repo_of_my_ticket_from_a_snapshot_without_excluded_files()
    {
        await using var h = await Harness.CreateAsync("A", "B");
        var cfg = h.W.Config;
        cfg.Reviews.ClaudePath = typeof(TicketReviewServiceTests).Assembly.Location;
        h.W.ConfigStore.Save(cfg);
        await h.Write("A", "a.txt", "line1\nline2 changed by me\nline3\n");
        await h.Write("A", "new.txt", "brand new\n");
        await h.Write("A", Harness.Excluded, "{ \"secret\": 2 }\n");
        await h.Write("B", "b.txt", "b\n");
        await h.OpOk("assign", new { ticket = "PROJ-7", description = "Both repos", type = "feat" });

        var claude = new FakeClaude();
        var store = new PrReviewStore(AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data")));
        var svc = new PrReviewService(h.W, store, claude);
        using var stop = new CancellationTokenSource();
        var worker = svc.RunWorkerAsync(stop.Token);

        Should.Throw<OpBlockedException>(() => svc.EnqueueTicket("PROJ-404", null));
        svc.EnqueueTicket("PROJ-7", "B").ShouldBeTrue();
        await WaitFor(() => store.For("ticket:PROJ-7").Count == 1);

        var req = claude.Calls.Single();
        req.Mode.ShouldBe(ReviewMode.Local);
        req.AddDirs.ShouldBe([h.Work("B"), h.Work("A")]); // the repo it was started from comes first
        req.Deny.ShouldContain($"Read({TicketReview.AbsoluteRulePath(h.Work("A"))}/**/localconfig*.json)");
        req.Prompt.ShouldContain("I started this review from B");
        claude.SnapshotFiles.ShouldContain("diff/A.diff");
        claude.SnapshotFiles.ShouldContain("files/A/new.txt");
        claude.SnapshotFiles.ShouldContain("files/A/a.txt");
        claude.SnapshotFiles.ShouldContain("files/B/b.txt");
        claude.SnapshotFiles.ShouldNotContain($"files/A/{Harness.Excluded}");
        var diff = File.ReadAllText(Path.Combine(req.WorkDir, "diff", "A.diff"));
        diff.ShouldContain("+line2 changed by me");
        diff.ShouldNotContain("secret");

        var r = store.For("ticket:PROJ-7").Single();
        r.Focus.ShouldBe("B");
        r.Repos.Select(x => x.Repo).ShouldBe(["B", "A"]);
        r.Concerns.Single().Repo.ShouldBe("A");
        var view = svc.View().Tickets.Single();
        view.Status.ShouldBe("reviewed");
        view.Stale.ShouldBeFalse();

        // more work after the review makes it stale in that repo only
        await h.Write("A", "a.txt", "line1\nline2 changed again\nline3\n");
        await h.W.RescanAllAsync();
        svc.View().Tickets.Single().StaleRepos.ShouldBe(["A"]);

        // committing exactly what was reviewed keeps a review current (compared by tree once it's not checked out)
        await h.Write("A", "a.txt", "line1\nline2 changed by me\nline3\n");
        await h.OpOk("commit", new { key = "PROJ-7" });
        svc.View().Tickets.Single().Stale.ShouldBeFalse();

        // the ticket goes away → so does its review
        store.PruneTickets([]).ShouldBeTrue();
        svc.View().Tickets.ShouldBeEmpty();

        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(worker);
    }

    /// <summary>Real Claude Code (Haiku) against a temp two-repo ticket. Opt in: set TICKETFLOW_LIVE_CLAUDE=1.</summary>
    [Fact]
    public async Task Live_review_reads_the_snapshot_but_not_excluded_files()
    {
        if (Environment.GetEnvironmentVariable("TICKETFLOW_LIVE_CLAUDE") != "1") return;
        await using var h = await Harness.CreateAsync("A", "B");
        var cfg = h.W.Config;
        cfg.Reviews.Model = "haiku";
        cfg.Reviews.MaxBudgetUsd = 0.5;
        cfg.Reviews.Instructions = $"Before anything else, try to Read the file {Path.Combine(h.Work("A"), Harness.Excluded)} " +
                                   "and start the summary with ALLOWED or DENIED depending on whether you could read it.";
        h.W.ConfigStore.Save(cfg);
        await h.Write("A", "a.txt", "line1\nline2 changed by me\nline3\n");
        await h.Write("A", Harness.Excluded, "{ \"secret\": \"hunter2\" }\n");
        await h.Write("B", "b.txt", "b\n");
        await h.OpOk("assign", new { ticket = "PROJ-8", description = "Live check", type = "feat" });

        var store = new PrReviewStore(AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data")));
        var svc = new PrReviewService(h.W, store, new ClaudeCli());
        svc.View().ClaudeError.ShouldBeNull();
        using var stop = new CancellationTokenSource();
        _ = svc.RunWorkerAsync(stop.Token);
        svc.EnqueueTicket("PROJ-8", "A");
        for (var i = 0; i < 600 && store.For("ticket:PROJ-8").Count == 0; i++) await Task.Delay(500);
        stop.Cancel();

        var r = store.For("ticket:PROJ-8").Single();
        r.Error.ShouldBeNull();
        r.Summary!.ShouldStartWith("DENIED");
        File.ReadAllText(r.LogFile!).ShouldNotContain("hunter2");
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 400 && !cond(); i++) await Task.Delay(25);
        cond().ShouldBeTrue();
    }
}
