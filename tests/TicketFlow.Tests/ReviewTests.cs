using System.Diagnostics;
using System.Text.Json.Nodes;
using Shouldly;
using TicketFlow.Core;
using TicketFlow.Core.GitHub;
using TicketFlow.Core.Reviews;

namespace TicketFlow.Tests;

public class ReviewRuleTests
{
    internal static RemotePr Pr(string head = "h2", bool draft = false, bool bot = false, MyReview? mine = null, List<MyThread>? threads = null) =>
        new("acme", "api", 7, "https://github.com/acme/api/pull/7", "Add things", "alice", bot, draft, "feat/x", "master", head, "b1",
            10, 2, 3, DateTimeOffset.Now, mine, threads ?? []);

    private static PrReviewResult Done(string head, string status = "ok") =>
        new() { Pr = "acme/api#7", HeadSha = head, Status = status, At = DateTimeOffset.Now };

    [Fact]
    public void Reviews_each_new_head_once()
    {
        ReviewPrompt.Need(Pr(), []).ShouldBe(new ReviewNeed(true, "new", null));
        ReviewPrompt.Need(Pr("h2"), [Done("h1")]).ShouldBe(new ReviewNeed(true, "commits", null));
        ReviewPrompt.Need(Pr("h2"), [Done("h2")]).Run.ShouldBeFalse();
        // a failed review is retried by hand only, never in a loop
        ReviewPrompt.Need(Pr("h2"), [Done("h2", "failed")]).Run.ShouldBeFalse();
    }

    [Fact]
    public void Skips_drafts_and_bots()
    {
        ReviewPrompt.Need(Pr(draft: true), []).Run.ShouldBeFalse();
        ReviewPrompt.Need(Pr(bot: true), []).Run.ShouldBeFalse();
        ReviewPrompt.Need(Pr(bot: true), [], BotPrMode.Manual).Run.ShouldBeFalse();
        ReviewPrompt.Need(Pr(bot: true), [], BotPrMode.Auto).Run.ShouldBeTrue();
    }

    [Fact]
    public void Since_prefers_my_own_review_commit()
    {
        var mine = new MyReview("CHANGES_REQUESTED", DateTimeOffset.Now, "mine1", "fix it", null);
        ReviewPrompt.SinceSha(Pr("h3", mine: mine), [Done("h2")]).ShouldBe("mine1");
        ReviewPrompt.SinceSha(Pr("h3"), [Done("h2", "failed"), Done("h1")]).ShouldBe("h1");
        ReviewPrompt.SinceSha(Pr("h3"), []).ShouldBeNull();
        // my review is of the current head: nothing new since then
        ReviewPrompt.SinceSha(Pr("h3", mine: mine with { CommitSha = "h3" }), []).ShouldBeNull();
    }

    [Fact]
    public void Prompt_asks_to_check_my_requests_after_changes_requested()
    {
        var mine = new MyReview("CHANGES_REQUESTED", DateTimeOffset.Now, "abcdef123456", "Please add a null check.", null);
        var threads = new List<MyThread> { new("src/A.cs", 12, false, false, [new("me", "Rename this"), new("alice", "done")]) };
        var prompt = ReviewPrompt.Build(Pr(mine: mine, threads: threads), "abcdef123456", null, "Focus on SQL.");

        prompt.ShouldContain("I requested changes");
        prompt.ShouldContain("> Please add a null check.");
        prompt.ShouldContain("src/A.cs:12 (open)");
        prompt.ShouldContain("@alice: done");
        prompt.ShouldContain("./pr.sh since");
        prompt.ShouldContain("previousRequests");
        prompt.ShouldContain("Focus on SQL.");
    }

    [Fact]
    public void Prompt_for_a_first_review_has_no_requests()
    {
        var prompt = ReviewPrompt.Build(Pr(), null, null, null);
        prompt.ShouldNotContain("My earlier review");
        prompt.ShouldNotContain("./pr.sh since");
        prompt.ShouldContain("previousRequests is empty");
    }

    [Fact]
    public void Parses_my_latest_review_and_my_threads()
    {
        var json = JsonNode.Parse("""
            {
              "number": 7, "url": "https://github.com/acme/api/pull/7", "title": "T", "isDraft": false, "updatedAt": "2026-11-01T00:00:00Z",
              "additions": 1, "deletions": 2, "changedFiles": 3, "headRefName": "f", "baseRefName": "master", "headRefOid": "h", "baseRefOid": "b",
              "author": { "__typename": "User", "login": "alice" },
              "reviews": { "nodes": [
                { "author": { "login": "Me" }, "state": "COMMENTED", "submittedAt": "2026-11-01T00:00:00Z", "body": "", "commit": { "oid": "c1" } },
                { "author": { "login": "bob" }, "state": "APPROVED", "submittedAt": "2026-11-02T00:00:00Z", "body": "", "commit": { "oid": "c2" } },
                { "author": { "login": "me" }, "state": "CHANGES_REQUESTED", "submittedAt": "2026-11-03T00:00:00Z", "body": "fix", "commit": { "oid": "c3" } },
                { "author": { "login": "me" }, "state": "PENDING", "submittedAt": null, "body": "draft", "commit": { "oid": "c4" } }
              ] },
              "reviewThreads": { "nodes": [
                { "isResolved": false, "isOutdated": false, "path": "a.cs", "line": 5, "comments": { "nodes": [ { "author": { "login": "me" }, "body": "x" }, { "author": { "login": "alice" }, "body": "y" } ] } },
                { "isResolved": false, "isOutdated": false, "path": "b.cs", "line": 1, "comments": { "nodes": [ { "author": { "login": "bob" }, "body": "z" } ] } }
              ] }
            }
            """)!;
        var pr = GitHubClient.ParseRemotePr(new RepoRef("acme", "api"), json, "me");

        pr.MyReview!.State.ShouldBe("CHANGES_REQUESTED");
        pr.MyReview.CommitSha.ShouldBe("c3");
        pr.MyThreads.Count.ShouldBe(1);
        pr.MyThreads[0].Comments.Select(c => c.Author).ShouldBe(["me", "alice"]);
        pr.AuthorIsBot.ShouldBeFalse();
        pr.FilesUrl.ShouldBe("https://github.com/acme/api/pull/7/files");
    }

    [Fact]
    public void Maps_structured_output_highest_severity_first()
    {
        var r = PrReviewService.FromOutput(JsonNode.Parse("""
            { "summary": "S", "verdict": "needs-changes", "risk": "high",
              "concerns": [ { "severity": "low", "title": "l", "detail": "d" }, { "severity": "high", "title": "h", "detail": "d", "file": "a.cs", "line": 3 } ],
              "previousRequests": [ { "request": "null check", "status": "addressed", "note": "added in A.cs" } ] }
            """)!);
        r.Summary.ShouldBe("S");
        r.Concerns.Select(c => c.Title).ShouldBe(["h", "l"]);
        r.Concerns[0].Line.ShouldBe(3);
        r.PreviousRequests.Single().Status.ShouldBe("addressed");
    }

    [Fact]
    public void Stream_parser_reads_progress_output_and_result()
    {
        var progress = new List<string>();
        var p = new ClaudeCli.StreamParser(progress.Add);
        p.Feed("""{"type":"system","subtype":"init","model":"claude-x"}""");
        p.Feed("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"./pr.sh files"}}]}}""");
        p.Feed("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"StructuredOutput","input":{"summary":"S"}}]}}""");
        p.Feed("""{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.12,"num_turns":5,"permission_denials":[{}],"structured_output":{"summary":"S2"}}""");

        progress.ShouldContain("$ ./pr.sh files");
        p.Model.ShouldBe("claude-x");
        p.Output!["summary"]!.GetValue<string>().ShouldBe("S2");
        p.Cost.ShouldBe(0.12);
        p.Turns.ShouldBe(5);
        p.Denials.ShouldBe(1);
        p.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Args_lock_the_session_down()
    {
        var args = ClaudeCli.Args(new ClaudeRunRequest("claude.exe", ".", "p", "sonnet", 2.5, TimeSpan.FromMinutes(1), new Dictionary<string, string>(), "log"));
        string After(string flag) => args[args.IndexOf(flag) + 1];
        After("--tools").ShouldBe("Bash");
        After("--permission-mode").ShouldBe("dontAsk");
        After("--setting-sources").ShouldBe("local"); // never the user's allow rules
        args.ShouldContain("--strict-mcp-config");
        After("--model").ShouldBe("sonnet");
        After("--max-budget-usd").ShouldBe("2.5");
        args.SkipWhile(a => a != "--allowedTools").Skip(1).ShouldAllBe(a => a.Contains("pr.sh"));
    }
}

/// <summary>Runs the generated pr.sh under Git Bash with a fake gh that prints its arguments.</summary>
public class ReviewScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ticketflow-prsh-" + Guid.NewGuid().ToString("N")[..8]);

    private static string? Bash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
        foreach (var p in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    private (int Code, string Out) Run(params string[] args)
    {
        var bash = Bash()!;
        var psi = new ProcessStartInfo(bash) { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("PATH=\"$PWD/bin:$PATH\" ./pr.sh \"$@\"");
        psi.ArgumentList.Add("pr.sh");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Replace("\r\n", "\n").Trim());
    }

    private bool Setup()
    {
        if (Bash() is null) return false;
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "pr.sh"), ReviewPrompt.BuildScript(ReviewRuleTests.Pr("headsha"), "sincesha"));
        File.WriteAllText(Path.Combine(_dir, "bin", "gh"), "#!/usr/bin/env bash\necho \"gh $*\"\n");
        return true;
    }

    [Fact]
    public void Every_call_is_a_get_with_an_encoded_path()
    {
        if (!Setup()) return; // no bash on this machine

        Run("file", "src/My File.cs").Out.ShouldBe("gh api -X GET repos/acme/api/contents/src/My%20File.cs?ref=headsha -H Accept: application/vnd.github.raw");
        Run("file", "a.cs", "--base").Out.ShouldContain("?ref=b1");
        Run("file", "a.cs?ref=x&y").Out.ShouldContain("contents/a.cs%3Fref%3Dx%26y?ref=headsha");
        Run("since").Out.ShouldContain("-X GET repos/acme/api/compare/sincesha...headsha");
        Run("search", "Foo Bar").Out.ShouldBe("gh api -X GET search/code -f q=Foo Bar repo:acme/api -f per_page=30 --jq .items[] | .path");
        Run("view").Out.ShouldBe("gh pr view 7 -R acme/api");
    }

    [Fact]
    public void Rejects_path_traversal_and_unknown_commands()
    {
        if (!Setup()) return;

        var (code, output) = Run("file", "../../pulls/7/merge");
        code.ShouldBe(2);
        output.ShouldNotContain("gh api");
        Run("merge").Code.ShouldBe(2);
        Run("view", "--", "; gh pr merge 7").Out.ShouldBe("gh pr view 7 -R acme/api"); // extra args are ignored
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public class ReviewServiceTests
{
    private sealed class FakeClaude(Func<ClaudeRunRequest, ClaudeRunResult> respond) : IClaudeRunner
    {
        public List<ClaudeRunRequest> Calls { get; } = [];

        public Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, Action<string> onProgress, CancellationToken ct)
        {
            Calls.Add(req);
            onProgress("$ ./pr.sh files");
            return Task.FromResult(respond(req));
        }
    }

    private static readonly JsonNode Review = JsonNode.Parse("""
        { "summary": "Adds things", "verdict": "minor-issues", "risk": "low",
          "concerns": [ { "severity": "medium", "title": "Missing test", "detail": "d" } ], "previousRequests": [] }
        """)!;

    [Fact]
    public async Task Polls_queues_and_reviews_each_new_head_once()
    {
        await using var h = await Harness.CreateAsync("api", "web");
        var cfg = h.W.Config;
        cfg.Repos.First(r => r.Name == "api").ReviewPrs = true;
        cfg.Reviews.ClaudePath = typeof(ReviewServiceTests).Assembly.Location; // any existing file
        h.W.ConfigStore.Save(cfg);
        await h.W.RescanAllAsync();
        var snap = h.W.Snapshot("api")!;
        var repo = new RepoRef(snap.GitHubOwner!, snap.GitHubRepo!);
        var pr = ReviewRuleTests.Pr("h1") with { Owner = repo.Owner, Repo = repo.Repo };
        h.GitHub.OpenPrs[repo] = [pr, ReviewRuleTests.Pr("d1", draft: true) with { Owner = repo.Owner, Repo = repo.Repo, Number = 8 }];

        var claude = new FakeClaude(_ => new ClaudeRunResult(true, null, Review.DeepClone(), 0.5, 4, "m", 0));
        var store = new PrReviewStore(AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data")));
        var svc = new PrReviewService(h.W, store, claude);
        using var stop = new CancellationTokenSource();
        var worker = svc.RunWorkerAsync(stop.Token);

        await svc.PollAsync();
        await WaitFor(() => store.For(pr.Key).Count == 1);

        claude.Calls.Count.ShouldBe(1); // the draft is skipped
        claude.Calls[0].Env["GH_TOKEN"].ShouldBe("test-token");
        File.ReadAllText(Path.Combine(claude.Calls[0].WorkDir, "pr.sh")).ShouldContain("HEAD='h1'");
        var result = store.For(pr.Key)[0];
        result.Verdict.ShouldBe("minor-issues");
        result.Trigger.ShouldBe("new");
        var view = svc.View().Repos.Single();
        view.Name.ShouldBe("api");
        view.Prs.Single(p => p.Number == 7).Status.ShouldBe("reviewed");
        view.Prs.Single(p => p.Number == 8).Status.ShouldBe("draft");

        // same head: nothing new
        await svc.PollAsync("api");
        await Task.Delay(100);
        claude.Calls.Count.ShouldBe(1);

        // new commits after I requested changes: reviewed again, diffing from my review's commit
        h.GitHub.OpenPrs[repo] = [pr with { HeadSha = "h2", MyReview = new MyReview("CHANGES_REQUESTED", DateTimeOffset.Now, "h1", "add a test", null) }];
        await svc.PollAsync("api");
        await WaitFor(() => store.For(pr.Key).Count == 2);
        claude.Calls[1].Prompt.ShouldContain("I requested changes");
        claude.Calls[1].Prompt.ShouldContain("./pr.sh since");
        store.For(pr.Key)[0].Trigger.ShouldBe("commits");
        store.For(pr.Key)[0].SinceSha.ShouldBe("h1");

        // closed: its results are dropped on the next poll
        h.GitHub.OpenPrs[repo] = [];
        await svc.PollAsync("api");
        store.For(pr.Key).ShouldBeEmpty();

        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(worker);
    }

    [Fact]
    public async Task Bot_prs_are_hidden_listed_or_reviewed_per_setting()
    {
        await using var h = await Harness.CreateAsync("api");
        var cfg = h.W.Config;
        cfg.Repos[0].ReviewPrs = true;
        cfg.Reviews.ClaudePath = typeof(ReviewServiceTests).Assembly.Location;
        h.W.ConfigStore.Save(cfg);
        await h.W.RescanAllAsync();
        var snap = h.W.Snapshot("api")!;
        var repo = new RepoRef(snap.GitHubOwner!, snap.GitHubRepo!);
        var bot = ReviewRuleTests.Pr("b1", bot: true) with { Owner = repo.Owner, Repo = repo.Repo, Number = 9 };
        h.GitHub.OpenPrs[repo] = [bot];
        var claude = new FakeClaude(_ => new ClaudeRunResult(true, null, Review.DeepClone(), 0.5, 4, "m", 0));
        var store = new PrReviewStore(AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data")));
        var svc = new PrReviewService(h.W, store, claude);
        using var stop = new CancellationTokenSource();
        var worker = svc.RunWorkerAsync(stop.Token);

        // default: hidden — not listed, not queued
        await svc.PollAsync("api");
        svc.View().Repos.Single().Prs.ShouldBeEmpty();
        svc.View().Queue.ShouldBeEmpty();

        // manual: listed (already fetched, no re-poll needed) but only reviewed on request
        cfg.Reviews.BotPrs = BotPrMode.Manual;
        h.W.ConfigStore.Save(cfg);
        svc.View().Repos.Single().Prs.Single().Status.ShouldBe("skipped");
        await svc.PollAsync("api");
        await Task.Delay(100);
        claude.Calls.ShouldBeEmpty();

        // auto: reviewed like any other PR
        cfg.Reviews.BotPrs = BotPrMode.Auto;
        h.W.ConfigStore.Save(cfg);
        await svc.PollAsync("api");
        await WaitFor(() => store.For(bot.Key).Count == 1);

        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(worker);
    }

    [Fact]
    public async Task Poll_now_refuses_repos_that_have_not_opted_in()
    {
        await using var h = await Harness.CreateAsync("api");
        var svc = new PrReviewService(h.W, new PrReviewStore(AppPaths.Create(Path.Combine(h.Root, "_cfg"), Path.Combine(h.Root, "_data"))),
            new FakeClaude(_ => throw new InvalidOperationException()));
        await Should.ThrowAsync<OpBlockedException>(() => svc.PollAsync("api"));
        svc.View().AnyEnabled.ShouldBeFalse();
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(25);
        cond().ShouldBeTrue();
    }
}
