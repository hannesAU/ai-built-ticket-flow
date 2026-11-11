using System.Text;
using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;

namespace TicketFlow.Core.Ops;

/// <summary>
/// "Build &amp; test": builds and unit tests every repo of a checked-out ticket, in the working copy. It holds the
/// op gate like any other op, so nothing else (branch switches, commits, pushes) can change the repos meanwhile.
/// The result is stored per ticket and reported on the board; failures fail the op so they stand out.
/// </summary>
public sealed class CheckOp : Operation<KeyRequest>
{
    public override string Kind => "check";
    public override bool Cancellable => true;
    public override string? KeyOf(KeyRequest req) => req.Key;

    private sealed record Leg(RepoSnapshot Repo, CheckPlan Plan);

    private static (List<Leg> Legs, List<string> Blockers) Resolve(Workspace w, string key)
    {
        var blockers = new List<string>();
        var legs = w.Legs(key);
        if (legs.Count == 0) return ([], [$"No in-flight ticket {key}"]);
        var notOut = legs.Where(l => !l.Wip.IsCurrent).Select(l => l.Repo.Name).ToList();
        if (notOut.Count > 0)
            blockers.Add($"{key} isn't checked out in {string.Join(", ", notOut)} — resume it first; checks build your working copy");
        foreach (var (r, _) in legs)
        {
            if (!r.Ok) blockers.Add($"{r.Name}: {r.Error}");
            else if (r.InProgress is not null) blockers.Add($"{r.Name}: {r.InProgress} in progress");
        }
        var planned = legs.Select(l => new Leg(l.Repo, CheckDetector.Plan(l.Repo.Config))).ToList();
        if (planned.All(p => p.Plan.IsEmpty))
            blockers.Add("Nothing to build or test in these repos — set build/test commands under Settings → Repositories");
        return (planned, blockers);
    }

    public override Task<OpPreview> PreviewAsync(Workspace w, KeyRequest req)
    {
        var (legs, blockers) = Resolve(w, req.Key);
        var steps = new List<PreviewStep>();
        var warnings = new List<string>();
        foreach (var (r, plan) in legs)
        {
            foreach (var c in plan.Commands) steps.Add(P.S(r.Name, c.Label, $"cd {c.Cwd}", c.Display));
            foreach (var n in plan.Notes) steps.Add(P.S(r.Name, n));
            if (plan.IsEmpty && plan.Notes.Count == 0) steps.Add(P.S(r.Name, "Nothing to build or test — skipped"));
            warnings.AddRange(plan.Commands.Where(c => c.NotReady is not null).Select(c => $"{r.Name}: {c.NotReady}"));
        }
        if (blockers.Count == 0)
            warnings.Add($"Other actions wait until this finishes (each command stops after {w.Config.CheckTimeoutMinutes} min). You can hide this window or cancel.");
        return Task.FromResult(P.Make(Kind, $"Build & test {req.Key}", "", steps, warnings, blockers, "Run checks"));
    }

    public override async Task RunAsync(OpContext ctx, KeyRequest req)
    {
        var w = ctx.W;
        var (legs, _) = Resolve(w, req.Key);
        var started = DateTimeOffset.Now;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, ctx.Config.CheckTimeoutMinutes));
        var runDir = w.Checks.NewRunDir(req.Key, ctx.Tag);
        Directory.CreateDirectory(runDir);
        var results = new List<RepoCheck>();
        try
        {
            foreach (var (r, plan) in legs)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                // record what is being tested before anything runs: edits made during the run make the result stale
                var tree = await GitOps.WorkTreeAsync(new GitRepo(w.Git, r.Path), OpUtil.Paths(r.Included));
                results.Add(await RunRepoAsync(ctx, r, plan, tree, runDir, timeout));
            }
        }
        catch (OperationCanceledException)
        {
            try { Directory.Delete(runDir, true); } catch { /* best effort */ }
            throw;
        }

        var run = new CheckRun(req.Key, results.Any(x => x.Status == "failed") ? "failed" : "passed", started, DateTimeOffset.Now, results);
        w.Checks.Save(run, runDir, w.Snapshots.SelectMany(s => s.Wips.Select(x => x.Key)).Distinct().ToList());
        var text = DashboardBuilder.CheckSummary(run);
        w.Notify(new Notification($"{req.Key} · Build & test", text, run.Status == "passed" ? "ok" : "bad", null));
        if (run.Status == "failed") throw new OpBlockedException(text);
    }

    private static async Task<RepoCheck> RunRepoAsync(OpContext ctx, RepoSnapshot r, CheckPlan plan, string? tree, string runDir, TimeSpan timeout)
    {
        var phases = new List<CheckPhase>();
        if (plan.IsEmpty)
        {
            ctx.Step(r.Name, "Nothing to build or test");
            ctx.EndStep("skipped", plan.Notes.FirstOrDefault());
            return new RepoCheck(r.Name, r.Fingerprint, tree, "skipped", "nothing to run", phases);
        }

        var buildFailed = false;
        for (var i = 0; i < plan.Commands.Count; i++)
        {
            var c = plan.Commands[i];
            ctx.Step(r.Name, c.Label);
            if (buildFailed && c.Phase == CheckCommand.Test)
            {
                ctx.EndStep("skipped", "build failed");
                phases.Add(new CheckPhase(c.Phase, c.Label, c.Display, "skipped", null, 0, "skipped (build failed)", null, [], null));
                continue;
            }
            if (c.NotReady is not null)
            {
                ctx.Log(c.NotReady);
                ctx.EndStep("failed", "not run");
                phases.Add(new CheckPhase(c.Phase, c.Label, c.Display, "failed", null, 0, c.NotReady, null, [c.NotReady], null));
                buildFailed |= c.Phase == CheckCommand.Build;
                continue;
            }

            var phase = await RunCommandAsync(ctx, r, c, Path.Combine(runDir, $"{ChecksStore.Safe(r.Name)}-{i + 1}-{c.Phase}.log"), timeout);
            ctx.EndStep(phase.Status, phase.Summary);
            phases.Add(phase);
            buildFailed |= c.Phase == CheckCommand.Build && phase.Status == "failed";
        }

        var failed = phases.Where(p => p.Status == "failed").ToList();
        var tests = phases.Select(p => p.Tests).OfType<TestCounts>().ToList();
        string summary;
        if (failed.Count == 0)
            summary = tests.Count > 0 ? OpUtil.Plural(tests.Sum(t => t.Passed), "test") + " passed" : "build succeeded";
        else if (buildFailed)
            summary = "build failed";
        else if (tests.Sum(t => t.Failed) is var nFailed and > 0)
            summary = OpUtil.Plural(nFailed, "test") + " failed";
        else
            summary = failed[0].Summary;
        return new RepoCheck(r.Name, r.Fingerprint, tree, failed.Count > 0 ? "failed" : "passed", summary, phases);
    }

    private static async Task<CheckPhase> RunCommandAsync(OpContext ctx, RepoSnapshot r, CheckCommand c, string logFile, TimeSpan timeout)
    {
        var analyzer = new OutputAnalyzer(r.Path);
        CommandOutcome outcome;
        await using (var log = new StreamWriter(logFile, false, new UTF8Encoding(false)))
        {
            await log.WriteLineAsync($"> {c.Display}");
            await log.WriteLineAsync($"  (in {c.Cwd})");
            await log.WriteLineAsync();
            ctx.Log($"$ {c.Display}");
            var closed = false; // a lingering child process can still write after the command has returned
            outcome = await CommandRunner.RunAsync(c, line =>
            {
                if (closed) return;
                log.WriteLine(line);
                analyzer.Feed(line);
                ctx.Log(line);
            }, timeout, ctx.Cancellation);
            closed = true;
            analyzer.Complete();
            await log.WriteLineAsync();
            await log.WriteLineAsync(outcome.TimedOut ? "(timed out)" : $"(exit code {outcome.ExitCode}, {outcome.Elapsed.TotalSeconds:0.#} s)");
        }

        var tests = analyzer.Tests;
        var ok = outcome.Ok;
        // Microsoft.Testing.Platform exits with 8 when a test project has no tests
        if (!ok && !outcome.TimedOut && c.Phase == CheckCommand.Test && outcome.ExitCode == 8 && tests is { Total: 0 }) ok = true;

        string summary;
        if (outcome.TimedOut) summary = $"timed out after {timeout.TotalMinutes:0.#} min";
        else if (c.Phase == CheckCommand.Build) summary = ok ? "build succeeded" : "build failed";
        else if (tests is { Failed: > 0 }) summary = $"{tests.Failed} of {OpUtil.Plural(tests.Total, "test")} failed";
        else if (tests is not null && ok) summary = tests.Total == 0 ? "no tests ran" : $"{tests.Passed} passed" + (tests.Skipped > 0 ? $", {tests.Skipped} skipped" : "");
        else summary = ok ? "passed" : $"failed (exit code {outcome.ExitCode})";

        var errors = analyzer.Errors.ToList();
        if (!ok && errors.Count == 0) errors = analyzer.Tail.Where(l => l.Trim().Length > 0).ToList();
        return new CheckPhase(c.Phase, c.Label, c.Display, ok ? "ok" : "failed", outcome.TimedOut ? null : outcome.ExitCode,
            Math.Round(outcome.Elapsed.TotalSeconds, 1), summary, tests, errors, logFile);
    }
}
