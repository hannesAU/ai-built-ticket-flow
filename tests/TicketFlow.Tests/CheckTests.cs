using Shouldly;
using TicketFlow.Core;
using TicketFlow.Core.Checks;
using TicketFlow.Core.Git;

namespace TicketFlow.Tests;

/// <summary>Detection of what "Build &amp; test" runs in a repo, against throwaway folder layouts.</summary>
public sealed class CheckDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ticketflow-detect-" + Guid.NewGuid().ToString("N")[..8]);

    public CheckDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best effort */ }
    }

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private const string Lib = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
    private const string TestProj = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /></ItemGroup></Project>";

    private static string Sln(params string[] projects) =>
        "Microsoft Visual Studio Solution File, Format Version 12.00\n" + string.Concat(projects.Select((p, i) =>
            $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"P{i}\", \"{p}\", \"{{{Guid.NewGuid().ToString().ToUpper()}}}\"\nEndProject\n"));

    private CheckPlan Plan(string? build = null, string? test = null) =>
        CheckDetector.Plan(new RepoConfig { Path = _root, Name = "repo", BuildCommand = build, TestCommand = test });

    [Fact]
    public void Dotnet_solution_builds_once_and_runs_only_unit_test_projects()
    {
        Write("App.sln", Sln(@"src\App\App.csproj", @"tests\App.UnitTests\App.UnitTests.csproj", @"tests\App.IntegrationTests\App.IntegrationTests.csproj"));
        Write("src/App/App.csproj", Lib);
        Write("tests/App.UnitTests/App.UnitTests.csproj", TestProj);
        Write("tests/App.IntegrationTests/App.IntegrationTests.csproj", TestProj);
        Write("tests/Orphan.UnitTests/Orphan.UnitTests.csproj", TestProj); // not in the solution → not built, not run

        var plan = Plan();

        plan.Commands.Select(c => (c.Phase, c.Label)).ShouldBe([
            ("build", "Build App.sln"),
            ("test", "Unit tests: App.UnitTests"),
        ]);
        plan.Commands[0].Args.ShouldBe(["build", "App.sln", "--nologo", "-v", "q", "-clp:ErrorsOnly"]);
        plan.Commands[1].Args.ShouldBe(["test", Path.Combine("tests", "App.UnitTests", "App.UnitTests.csproj"), "--no-build", "--nologo", "-v", "q",
            "--logger", "console;verbosity=minimal"]);
        plan.Commands[1].Display.ShouldEndWith("--logger \"console;verbosity=minimal\"");
        plan.Commands.ShouldAllBe(c => c.Cwd == _root && c.FileName == "dotnet");
        plan.Notes.ShouldContain("Not run (not unit tests): App.IntegrationTests");
    }

    [Fact]
    public void Nested_solution_runs_in_its_folder_and_honours_testing_platform_opt_in()
    {
        Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Write("svc/Svc.slnx", """<Solution><Project Path="Svc/Svc.csproj" /><Folder Name="/t/"><Project Path="Svc.Tests/Svc.Tests.csproj" /></Folder></Solution>""");
        Write("svc/Svc/Svc.csproj", Lib);
        Write("svc/Svc.Tests/Svc.Tests.csproj", TestProj);
        Write("tools/deep/er/Other.sln", Sln()); // deeper than svc/ → ignored

        var plan = Plan();

        var dir = Path.Combine(_root, "svc");
        plan.Commands.Select(c => c.Label).ShouldBe(["Build Svc.slnx (in svc)", "Unit tests: Svc.Tests"]);
        plan.Commands.ShouldAllBe(c => c.Cwd == dir);
        plan.Commands[1].Args.ShouldBe(["test", "--project", Path.Combine("Svc.Tests", "Svc.Tests.csproj"), "--no-build", "--no-progress", "--no-ansi"]);
    }

    [Fact]
    public void Prefers_slnx_over_sln_of_the_same_name_and_ignores_dot_vs()
    {
        Write("A.sln", Sln(@"A\A.csproj"));
        Write("A.slnx", """<Solution><Project Path="A/A.csproj" /></Solution>""");
        Write("A/A.csproj", Lib);
        Write(".vs/A.slnx", "<Solution />");

        CheckDetector.FindSolutions(_root).Select(s => Path.GetFileName(s.Path)).ShouldBe(["A.slnx"]);
        Plan().Notes.ShouldContain("No test projects in A.slnx");
    }

    [Fact]
    public void Node_scripts_use_the_lockfile_package_manager_and_skip_npm_placeholder_test()
    {
        Write("package.json", """{ "scripts": { "build": "tsc", "test": "echo \"Error: no test specified\" && exit 1" } }""");
        Write("yarn.lock", "");

        var plan = Plan();

        var build = plan.Commands.ShouldHaveSingleItem();
        build.Display.ShouldBe("yarn run build");
        build.NotReady.ShouldNotBeNull().ShouldContain("yarn install");

        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        Plan().Commands.Single().NotReady.ShouldBeNull();
    }

    [Fact]
    public void Settings_override_or_turn_off_detection()
    {
        Write("App.sln", Sln(@"App\App.csproj"));
        Write("App/App.csproj", Lib);

        var plan = Plan(build: "none", test: "npm run test:unit");

        var test = plan.Commands.ShouldHaveSingleItem();
        test.Phase.ShouldBe("test");
        test.Display.ShouldBe("npm run test:unit");
        test.Cwd.ShouldBe(_root);
        plan.Notes.ShouldContain(n => n.Contains("Build is turned off"));
    }

    [Fact]
    public void Nothing_found_says_so()
    {
        Write("README.md", "infra only");
        var plan = Plan();
        plan.IsEmpty.ShouldBeTrue();
        plan.Notes.Single().ShouldContain("Settings");
    }

    [Theory]
    [InlineData("Contoso.API.UnitTests", true)]
    [InlineData("Contoso.Notifier.Tests", true)]
    [InlineData("UnitTests", true)]
    [InlineData("LoadBalancer.UnitTests", true)]
    [InlineData("Api.UnitTests", true)]
    [InlineData("Upload.Tests", true)]
    [InlineData("Foo.FileSystem.Tests", true)]
    [InlineData("Contoso.Contract.Tests", true)]
    [InlineData("Gui.Tests", true)]
    [InlineData("Contoso.API.IntegrationTests", false)]
    [InlineData("Contoso.API.PerformanceTests", false)]
    [InlineData("Shop.E2E.Tests", false)]
    [InlineData("Shop.E2ETests", false)]
    [InlineData("Shop.EndToEndTests", false)]
    [InlineData("Shop.Tests.Integration", false)]
    [InlineData("Shop.Benchmarks", false)]
    [InlineData("Load_Tests", false)]
    public void Recognises_unit_test_projects_by_name(string name, bool unit) =>
        CheckDetector.IsUnitTestProjectName(name).ShouldBe(unit);
}

public class OutputAnalyzerTests
{
    private static OutputAnalyzer Feed(string root, params string[] lines)
    {
        var a = new OutputAnalyzer(root);
        foreach (var l in lines) a.Feed(l);
        a.Complete();
        return a;
    }

    // real `dotnet test -v q --logger "console;verbosity=minimal"` output (xunit 2)
    [Fact]
    public void Vstest_failures_keep_name_and_message_and_summaries_add_up()
    {
        var a = Feed(@"C:\r",
            @"Test run for C:\r\Foo.Tests\bin\Debug\net10.0\Foo.Tests.dll (.NETCoreApp,Version=v10.0)",
            "[xUnit.net 00:00:00.15]     Foo.Tests.CalcTests.Adds [FAIL]",
            "  Failed Foo.Tests.CalcTests.Adds [5 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure: Values differ",
            "Expected: 3",
            "Actual:   -1",
            "  Stack Trace:",
            @"     at Foo.Tests.CalcTests.Adds() in C:\r\Foo.Tests\CalcTests.cs:line 5",
            "  Failed Foo.Tests.CalcTests.Other [< 1 ms]",
            "  Error Message:",
            "   boom",
            "",
            "Failed!  - Failed:     2, Passed:    10, Skipped:     2, Total:    14, Duration: 1 s - Foo.Tests.dll (net8.0)",
            "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s - Bar.Tests.dll (net8.0)");
        a.Tests.ShouldBe(new TestCounts(19, 15, 2, 2));
        a.Errors.ShouldBe([
            "Failed Foo.Tests.CalcTests.Adds\n  Assert.Equal() Failure: Values differ\n  Expected: 3\n  Actual:   -1",
            "Failed Foo.Tests.CalcTests.Other\n  boom",
        ]);
    }

    // real Microsoft.Testing.Platform output (xunit.v3)
    [Fact]
    public void Testing_platform_failures_and_summary()
    {
        var a = Feed(@"C:\r",
            @"Running tests from C:\r\T\bin\Debug\net10.0\T.dll (net10.0|x64)",
            "failed MathTests.Adds (88ms)",
            @"  from C:\r\T\bin\Debug\net10.0\T.dll (net10.0|x64)",
            "  Assert.Equal() Failure: Values differ",
            "  Expected: 3",
            "  Actual:   2",
            @"    at MathTests.Adds() in C:\r\T\Tests.cs:4",
            @"C:\r\T\bin\Debug\net10.0\T.dll (net10.0|x64) failed with 1 error(s) (865ms)",
            "Exit code: 2",
            "",
            "Test run summary: Failed!",
            "  total: 2",
            "  failed: 1",
            "  succeeded: 1",
            "  skipped: 0",
            "  duration: 1s 678ms");
        a.Tests.ShouldBe(new TestCounts(2, 1, 1, 0));
        a.Errors.ShouldBe(["Failed MathTests.Adds\n  Assert.Equal() Failure: Values differ\n  Expected: 3\n  Actual:   2"]);
    }

    [Fact]
    public void Reads_jest_summary()
    {
        Feed("/r", "Tests:       1 failed, 2 skipped, 12 passed, 15 total").Tests.ShouldBe(new TestCounts(15, 12, 1, 2));
    }

    [Fact]
    public void Compiler_errors_are_shortened_and_deduplicated()
    {
        const string line = @"C:\code\api\Api\Foo.cs(12,5): error CS1002: ; expected [C:\code\api\Api\Api.csproj]";
        var a = Feed(@"C:\code\api", line, line, "", "Build FAILED.");
        a.Errors.ShouldBe([@"Api\Foo.cs(12,5): error CS1002: ; expected"]);
        a.Tests.ShouldBeNull();
    }
}

public class FingerprintTests
{
    [Fact]
    public void Changes_with_content_but_not_with_staging()
    {
        var root = Path.Combine(Path.GetTempPath(), "ticketflow-fp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "one");
            var untracked = new FileChange("a.txt", null, '?', '?', ChangeKind.Untracked);
            var added = new FileChange("a.txt", null, 'A', '.', ChangeKind.Added);

            var fp = RepoScanner.WorkFingerprint(root, "abc", [untracked]);
            RepoScanner.WorkFingerprint(root, "abc", [added]).ShouldBe(fp);
            RepoScanner.WorkFingerprint(root, "def", [untracked]).ShouldNotBe(fp);

            File.WriteAllText(Path.Combine(root, "a.txt"), "two");
            RepoScanner.WorkFingerprint(root, "abc", [untracked]).ShouldNotBe(fp);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/// <summary>"Build &amp; test" end to end against real repos; commands are cmd.exe one-liners standing in for builds.</summary>
public class CheckWorkflowTests
{
    private const string PassTests = "echo Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 1 s - X.Tests.dll";

    private static async Task SetCommands(Harness h, params (string Repo, string? Build, string? Test)[] repos)
    {
        var cfg = h.W.Config;
        foreach (var (repo, build, test) in repos)
        {
            var rc = cfg.Repos.Single(r => r.Name == repo);
            rc.BuildCommand = build;
            rc.TestCommand = test;
        }
        h.W.ConfigStore.Save(cfg);
        await h.W.RescanAllAsync();
    }

    private static async Task<Harness> WithTicket(params string[] repos)
    {
        var h = await Harness.CreateAsync(repos);
        foreach (var r in repos) await h.Write(r, "a.txt", $"line1\n{r} change\nline3\n");
        await h.OpOk("assign", new { ticket = "PROJ-7", description = "Checked thing", type = "feat" });
        return h;
    }

    [Fact]
    public async Task Passing_check_is_reported_goes_stale_on_edit_and_survives_commit()
    {
        await using var h = await WithTicket("A", "B");
        await SetCommands(h, ("A", "echo building A", PassTests), ("B", "echo building B", PassTests));

        var p = await h.OpOk("check", new { key = "PROJ-7" });
        p.Steps.Select(s => (s.Repo, s.Status)).ShouldBe([("A", "ok"), ("A", "ok"), ("B", "ok"), ("B", "ok")]);
        p.Steps[1].Text.ShouldBe("Unit tests — 3 passed");

        var t = (await h.State()).Tickets.Single();
        var c = t.Check.ShouldNotBeNull();
        c.Status.ShouldBe("passed");
        c.Stale.ShouldBeFalse();
        c.Text.ShouldBe("Builds pass · 6 tests pass");
        c.Repos.Select(r => r.Summary).ShouldBe(["3 tests passed", "3 tests passed"]);
        File.ReadAllText(c.Repos[0].Phases[0].LogFile!).ShouldContain("building A");

        // editing the ticket's code makes the result stale; editing an excluded local file doesn't
        await h.Write("A", Harness.Excluded, "{ \"local\": true }\n");
        (await h.State()).Tickets.Single().Check!.Stale.ShouldBeFalse();
        await h.Write("A", "a.txt", "line1\nA changed again\nline3\n");
        c = (await h.State()).Tickets.Single().Check!;
        c.Stale.ShouldBeTrue();
        c.StaleRepos.ShouldBe(["A"]);

        // re-check, then commit: the committed branch holds exactly what was tested → still current
        await h.OpOk("check", new { key = "PROJ-7" });
        await h.OpOk("commit", new { key = "PROJ-7" });
        t = (await h.State()).Tickets.Single();
        t.Stage.ShouldBe(Stages.Parked);
        t.Check!.Stale.ShouldBeFalse();

        // only the checked-out (active) ticket can be checked
        t.Actions["check"].Enabled.ShouldBeFalse();
        (await h.Preview("check", new { key = "PROJ-7" })).Blockers.ShouldContain(b => b.Contains("isn't checked out"));
    }

    [Fact]
    public async Task Failures_fail_the_op_skip_tests_after_a_failed_build_and_keep_going()
    {
        await using var h = await WithTicket("A", "B");
        await SetCommands(h,
            ("A", @"echo Foo.cs(1,1): error CS1002: ; expected & exit /b 1", PassTests),
            ("B", "echo ok", "echo   Failed Foo.Tests.Bar [5 ms] & echo   Error Message: & echo    Expected 1 & echo Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3 & exit /b 1"));

        var p = await h.Op("check", new { key = "PROJ-7" });

        p.Status.ShouldBe("failed");
        p.Error.ShouldBe("Failed in A (build failed), B (1 test failed)");
        p.Steps.Select(s => (s.Repo, s.Status)).ShouldBe([("A", "failed"), ("A", "skipped"), ("B", "ok"), ("B", "failed")]);

        var c = (await h.State()).Tickets.Single().Check!;
        c.Status.ShouldBe("failed");
        c.Repos[0].Phases[0].Errors.ShouldBe(["Foo.cs(1,1): error CS1002: ; expected"]);
        c.Repos[1].Phases[1].Summary.ShouldBe("1 of 3 tests failed");
        c.Repos[1].Phases[1].Errors.ShouldBe(["Failed Foo.Tests.Bar\n  Expected 1"]);
    }

    [Fact]
    public async Task Running_check_blocks_other_ops_and_can_be_cancelled()
    {
        await using var h = await WithTicket("A");
        await SetCommands(h, ("A", "ping -n 60 127.0.0.1 > nul", "none"));

        var p = h.Runner.Start("check", System.Text.Json.JsonSerializer.SerializeToElement(new { key = "PROJ-7" }, Json.Options));
        p.Cancellable.ShouldBeTrue();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!p.Steps.Any(s => s.Status == "running" && s.Text.StartsWith("Build")) && DateTime.UtcNow < until) await Task.Delay(50);

        Should.Throw<OpBlockedException>(() => h.Runner.Start("commit", System.Text.Json.JsonSerializer.SerializeToElement(new { key = "PROJ-7" }, Json.Options)))
            .Message.ShouldContain("still running");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Runner.Cancel().ShouldBeTrue();
        await h.Runner.LastTask;
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        p.Status.ShouldBe("failed");
        p.Error.ShouldBe("Cancelled");
        p.Cancellable.ShouldBeFalse();
        (await h.State()).Tickets.Single().Check.ShouldBeNull();
        h.Runner.Busy.ShouldBeFalse();
    }
}
