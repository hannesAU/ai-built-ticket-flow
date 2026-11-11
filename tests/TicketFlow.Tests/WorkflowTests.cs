using Shouldly;
using TicketFlow.Core;
using TicketFlow.Core.Git;

namespace TicketFlow.Tests;

/// <summary>End-to-end flows against real git repos (bare origins in a temp folder) and a fake GitHub.</summary>
public class WorkflowTests
{
    private const string Ex = Harness.Excluded;

    [Fact]
    public async Task Full_lifecycle_across_two_repos()
    {
        await using var h = await Harness.CreateAsync("A", "B");

        // --- unassigned changes on master (plus an excluded file) ---
        await h.Write("A", "a.txt", "line1\nline2 changed by me\nline3\n");
        await h.Write("A", "new.txt", "brand new\n");
        await h.Write("A", Ex, "{ \"setting\": 2 }\n");
        await h.Write("B", "b.txt", "b\n");
        var s = await h.State();
        s.Unassigned.Select(u => u.Repo).ShouldBe(["A", "B"]);
        s.Unassigned[0].Files.Select(f => f.Path).ShouldBe(["a.txt", "new.txt"], ignoreOrder: true);
        s.Unassigned[0].ExcludedFiles.Single().Path.ShouldBe(Ex);

        // --- assign → wip branches ---
        await h.OpOk("assign", new { ticket = "proj 101", description = "First thing", type = "feat" });
        (await h.Branch("A")).ShouldBe("wip/PROJ-101");
        (await h.Branch("B")).ShouldBe("wip/PROJ-101");
        s = await h.State();
        s.Unassigned.ShouldBeEmpty();
        var t = s.Tickets.Single();
        t.Key.ShouldBe("PROJ-101");
        t.Stage.ShouldBe(Stages.Working);
        t.CommitMessage.ShouldBe("feat(PROJ-101): First thing");
        h.Read("A", Ex).ShouldContain("2");

        // --- commit locally → back on master, excluded file still modified & uncommitted ---
        await h.OpOk("commit", new { key = "PROJ-101" });
        (await h.Branch("A")).ShouldBe("master");
        (await h.Branch("B")).ShouldBe("master");
        h.Read("A", Ex).ShouldContain("2");
        h.Read("A", "a.txt").ShouldNotContain("changed by me");
        (await h.Run(h.Work("A"), "log", "-1", "--format=%s", "wip/PROJ-101")).ShouldBe("feat(PROJ-101): First thing");
        (await h.Run(h.Work("A"), "show", "--name-only", "--format=", "wip/PROJ-101")).ShouldNotContain(Ex);
        s = await h.State();
        s.Tickets.Single().Stage.ShouldBe(Stages.Parked);

        // --- new unassigned work blocks resuming PROJ-101 in that repo ---
        await h.Write("A", "a.txt", "line1\nline2\nline3 second ticket\n");
        (await h.Preview("resume", new { key = "PROJ-101" })).Blockers.ShouldContain(b => b.Contains("unassigned"));

        // --- second ticket in A ---
        await h.OpOk("assign", new { ticket = "PROJ-102", description = "Second", type = "fix" });
        (await h.Branch("A")).ShouldBe("wip/PROJ-102");

        // --- resume PROJ-101: auto-parks PROJ-102, un-commits PROJ-101 ---
        await h.OpOk("resume", new { key = "PROJ-101" });
        (await h.Branch("A")).ShouldBe("wip/PROJ-101");
        (await h.Branch("B")).ShouldBe("wip/PROJ-101");
        h.Read("A", "a.txt").ShouldContain("changed by me");
        (await h.Run(h.Work("A"), "log", "-1", "--format=%s", "wip/PROJ-102")).ShouldBe("fix(PROJ-102): Second");
        h.Read("A", Ex).ShouldContain("2");
        s = await h.State();
        s.Tickets.Single(x => x.Key == "PROJ-101").Stage.ShouldBe(Stages.Working);
        s.Tickets.Single(x => x.Key == "PROJ-102").Stage.ShouldBe(Stages.Parked);

        await h.OpOk("commit", new { key = "PROJ-101" });

        // --- teammate advances origin/master on a different file; push rebases off-tree ---
        await h.TeammateCommit("A", "other.txt", "teammate\n");
        var push = await h.OpOk("push", new { key = "PROJ-101" });
        push.Links.Count.ShouldBe(2);
        var originMaster = await h.Run(h.Bare("A"), "rev-parse", "master");
        (await h.Run(h.Bare("A"), "rev-parse", "feat/PROJ-101^")).ShouldBe(originMaster);
        (await h.Run(h.Work("A"), "rev-parse", "wip/PROJ-101")).ShouldBe(await h.Run(h.Bare("A"), "rev-parse", "feat/PROJ-101"));
        (await h.Branch("A")).ShouldBe("master");
        h.Read("A", Ex).ShouldContain("2");

        await h.W.PollPrsAsync();
        s = h.W.State();
        var t101 = s.Tickets.Single(x => x.Key == "PROJ-101");
        t101.Stage.ShouldBe(Stages.Review);
        t101.Legs.ShouldAllBe(l => l.Pr != null && !l.LocalAhead);
        t101.Actions["complete"].Enabled.ShouldBeFalse();

        // --- approve both → ready → complete ---
        h.GitHub.Approve("A", "feat/PROJ-101");
        h.GitHub.Approve("B", "feat/PROJ-101");
        await h.W.PollPrsAsync();
        h.W.State().Tickets.Single(x => x.Key == "PROJ-101").Stage.ShouldBe(Stages.Ready);
        await h.OpOk("complete", new { key = "PROJ-101" });
        h.GitHub.Merges.Count.ShouldBe(2);
        h.GitHub.DeletedBranches.ShouldBe(["A/feat/PROJ-101", "B/feat/PROJ-101"]);
        s = await h.State();
        s.Tickets.Select(x => x.Key).ShouldBe(["PROJ-102"]);
        h.W.Archive.All().Single().Key.ShouldBe("PROJ-101");
        (await h.Git.RunAsync(h.Work("A"), ["rev-parse", "--verify", "--quiet", "refs/heads/wip/PROJ-101"])).Ok.ShouldBeFalse();
        h.Read("A", Ex).ShouldContain("2");
    }

    [Fact]
    public async Task Pr_merged_outside_the_app_can_be_completed()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "line1\nMINE\nline3\n");
        await h.OpOk("assign", new { ticket = "PROJ-7", description = "Mine", type = "feat" });
        await h.OpOk("commit", new { key = "PROJ-7" });
        await h.OpOk("push", new { key = "PROJ-7" });

        // On GitHub: master moves on, the PR is rebased, approved, rebase-merged and its branch deleted.
        await h.TeammateCommit("A", "other.txt", "teammate\n");
        var q = h.GitHub.Prs.Keys.Single();
        h.GitHub.Prs[q] = h.GitHub.Prs[q] with { State = "MERGED", HeadSha = "0698705deadbeef", ReviewDecision = "APPROVED", MergeState = "UNKNOWN" };
        await h.Run(h.Bare("A"), "branch", "-D", "feat/PROJ-7");
        await h.Run(h.Work("A"), "fetch", "--quiet", "origin");
        await h.Run(h.Work("A"), "checkout", "--quiet", "wip/PROJ-7");

        await h.W.RescanAllAsync();
        await h.W.PollPrsAsync();
        var t = h.W.State().Tickets.Single();
        t.Stage.ShouldBe(Stages.Ready);
        t.Actions["complete"].Enabled.ShouldBeTrue();
        (await h.Preview("complete", new { key = "PROJ-7" })).Blockers.ShouldBeEmpty();

        await h.OpOk("complete", new { key = "PROJ-7" });
        h.GitHub.Merges.ShouldBeEmpty();
        (await h.State()).Tickets.ShouldBeEmpty();
        (await h.Branch("A")).ShouldBe("master");
        h.W.Archive.All().Single().Key.ShouldBe("PROJ-7");
    }

    [Fact]
    public async Task Rebase_conflict_is_reported_and_nothing_is_pushed()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "line1\nMINE\nline3\n");
        await h.OpOk("assign", new { ticket = "PROJ-5", description = "Mine", type = "feat" });
        await h.OpOk("commit", new { key = "PROJ-5" });
        await h.TeammateCommit("A", "a.txt", "line1\nTHEIRS\nline3\n");

        var p = await h.Op("push", new { key = "PROJ-5" });
        p.Status.ShouldBe("failed");
        p.Error!.ShouldContain("Rebase conflict");
        (await h.Git.RunAsync(h.Bare("A"), ["rev-parse", "--verify", "--quiet", "refs/heads/feat/PROJ-5"])).Ok.ShouldBeFalse();
        (await h.Run(h.Work("A"), "worktree", "list")).Split('\n').Length.ShouldBe(1);

        // guided resolution in the working copy
        await h.OpOk("resolve", new { key = "PROJ-5", repo = "A" });
        (await h.State()).Repos.Single().InProgress.ShouldBe("rebase");
        await h.Write("A", "a.txt", "line1\nTHEIRS and MINE\nline3\n");
        await h.OpOk("rebase", new { repo = "A", action = "continue" });
        (await h.State()).Repos.Single().InProgress.ShouldBeNull();

        await h.OpOk("push", new { key = "PROJ-5" });
        (await h.Run(h.Bare("A"), "show", "feat/PROJ-5:a.txt")).ShouldContain("THEIRS and MINE");
    }

    [Fact]
    public async Task Excluded_file_that_differs_between_branches_is_carried_via_stash()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "work\n");
        await h.OpOk("assign", new { ticket = "PROJ-7", description = "x", type = "feat" });
        await h.OpOk("commit", new { key = "PROJ-7" });

        // teammate changes the excluded file on master; local master fast-forwards on commit/sync
        await h.TeammateCommit("A", Ex, "{ \"setting\": 99 }\n");
        await h.OpOk("sync", new { });
        h.Read("A", Ex).ShouldContain("99");

        // local edit to the excluded file, then resume PROJ-7 (whose base has the old excluded content)
        await h.Write("A", Ex, "{ \"setting\": 99, \"local\": true }\n");
        await h.OpOk("resume", new { key = "PROJ-7" });
        (await h.Branch("A")).ShouldBe("wip/PROJ-7");
        h.Read("A", Ex).ShouldContain("local");
        (await h.Run(h.Work("A"), "stash", "list")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Discard_is_recoverable()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "precious\n");
        await h.Write("A", "untracked.txt", "also precious\n");

        var preview = await h.Preview("discard-changes", new { repo = "A", paths = new[] { "a.txt", "untracked.txt" } });
        preview.ConfirmText.ShouldBe("A");
        await h.OpOk("discard-changes", new { repo = "A", paths = new[] { "a.txt", "untracked.txt" } });
        h.Read("A", "a.txt").ShouldStartWith("line1");
        File.Exists(Path.Combine(h.Work("A"), "untracked.txt")).ShouldBeFalse();

        var backups = await GitOps.ListRefsAsync(new GitRepo(h.Git, h.Work("A")), GitOps.BackupRefPrefix);
        backups.Count.ShouldBe(1);
        await h.OpOk("restore", new { repo = "A", @ref = backups[0].Ref });
        h.Read("A", "a.txt").ShouldBe("precious\n");
        h.Read("A", "untracked.txt").ShouldBe("also precious\n");
    }

    [Fact]
    public async Task Discarded_ticket_can_be_restored_from_trash()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "feature\n");
        await h.OpOk("assign", new { ticket = "PROJ-9", description = "Nine", type = "fix" });
        await h.Write("A", "more.txt", "uncommitted extra\n");
        await h.OpOk("commit", new { key = "PROJ-9" });

        await h.OpOk("discard-ticket", new { key = "PROJ-9", closePrs = false, deleteRemote = false });
        (await h.State()).Tickets.ShouldBeEmpty();

        var trash = h.W.Trash.All().Single();
        await h.OpOk("restore", new { repo = "A", @ref = trash.Ref });
        var t = (await h.State()).Tickets.Single();
        t.Key.ShouldBe("PROJ-9");
        t.Description.ShouldBe("Nine");
        t.Type.ShouldBe("fix");
    }

    [Fact]
    public async Task Chore_without_ticket_number_gets_a_virtual_ticket()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "tidy\n");
        await h.OpOk("assign", new { ticket = "", description = "Tidy build scripts", type = "chore" });
        (await h.Branch("A")).ShouldBe("wip/tidy-build-scripts");
        var t = (await h.State()).Tickets.Single();
        t.Key.ShouldBe("tidy-build-scripts");
        t.Type.ShouldBe("chore");
        t.CommitMessage.ShouldBe("chore: Tidy build scripts");
        t.JiraUrl.ShouldBeNull();

        await h.OpOk("commit", new { key = t.Key });
        (await h.Run(h.Work("A"), "log", "-1", "--format=%s", "wip/tidy-build-scripts")).ShouldBe("chore: Tidy build scripts");

        await h.OpOk("push", new { key = t.Key });
        (await h.Run(h.Bare("A"), "log", "-1", "--format=%s", "chore/tidy-build-scripts")).ShouldBe("chore: Tidy build scripts");

        // same description again → separate virtual ticket with a suffix
        await h.Write("A", "b.txt", "more\n");
        (await h.Preview("assign", new { ticket = "", description = "Tidy build scripts", type = "chore" })).Title.ShouldContain("tidy-build-scripts_2");

        // a ticket number still works, and still has to be valid
        (await h.Preview("assign", new { ticket = "PROJ-", description = "x", type = "chore" })).Blockers.ShouldContain(b => b.Contains("ticket number"));
        await h.OpOk("assign", new { ticket = "PROJ-55", description = "Tidy more", type = "chore" });
        (await h.State()).Tickets.Single(x => x.Key == "PROJ-55").CommitMessage.ShouldBe("chore(PROJ-55): Tidy more");
    }

    [Fact]
    public async Task Same_ticket_twice_gets_a_suffix_and_existing_ticket_can_be_extended()
    {
        await using var h = await Harness.CreateAsync("A", "B");
        await h.Write("A", "a.txt", "one\n");
        await h.OpOk("assign", new { ticket = "PROJ-1", description = "One", type = "feat" });
        await h.OpOk("commit", new { key = "PROJ-1" });

        await h.Write("A", "c.txt", "two\n");
        var p = await h.Preview("assign", new { ticket = "PROJ-1", description = "Again", type = "feat" });
        p.Warnings.ShouldContain(w => w.Contains("already in flight"));
        p.Title.ShouldContain("PROJ-1_2");

        // instead, add B's changes to the existing PROJ-1 and A's changes too (re-opens A's commit)
        await h.Write("B", "b.txt", "b\n");
        await h.OpOk("assign", new { existingKey = "PROJ-1" });
        (await h.Branch("A")).ShouldBe("wip/PROJ-1");
        (await h.Branch("B")).ShouldBe("wip/PROJ-1");
        h.Read("A", "a.txt").ShouldBe("one\n");
        h.Read("A", "c.txt").ShouldBe("two\n");
        var t = (await h.State()).Tickets.Single();
        t.Legs.Count.ShouldBe(2);
        t.Description.ShouldBe("One");
    }

    [Fact]
    public async Task Remote_clash_in_another_repo_renames_ticket_to_next_suffix_on_push()
    {
        await using var h = await Harness.CreateAsync("A", "B");
        // someone else owns feat/PROJ-4 on B's origin; assign only checks A (the first repo with changes)
        await h.Run(h.Bare("B"), "branch", "feat/PROJ-4", "master");
        await h.Write("A", "a.txt", "a\n");
        await h.Write("B", "b.txt", "b\n");
        await h.OpOk("assign", new { ticket = "PROJ-4", description = "Four", type = "feat" });
        (await h.Branch("B")).ShouldBe("wip/PROJ-4");
        await h.OpOk("commit", new { key = "PROJ-4" });

        var p = await h.OpOk("push", new { key = "PROJ-4" });
        p.Key.ShouldBe("PROJ-4_2");
        p.Warnings.ShouldContain(w => w.Contains("renamed PROJ-4 to PROJ-4_2"));
        (await h.Run(h.Bare("A"), "show", "feat/PROJ-4_2:a.txt")).ShouldBe("a");
        (await h.Run(h.Bare("B"), "show", "feat/PROJ-4_2:b.txt")).ShouldBe("b");
        (await h.Run(h.Bare("B"), "rev-parse", "feat/PROJ-4")).ShouldBe(await h.Run(h.Bare("B"), "rev-parse", "master"));
        var t = (await h.State()).Tickets.Single();
        t.Key.ShouldBe("PROJ-4_2");
        t.Description.ShouldBe("Four");
        t.Legs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Push_refuses_to_overwrite_foreign_remote_changes()
    {
        await using var h = await Harness.CreateAsync("A");
        await h.Write("A", "a.txt", "mine\n");
        await h.OpOk("assign", new { ticket = "PROJ-3", description = "Three", type = "feat" });
        await h.OpOk("commit", new { key = "PROJ-3" });
        await h.OpOk("push", new { key = "PROJ-3" });

        // someone pushes to the PR branch
        var other = Path.Combine(h.Root, "A", "reviewer");
        await h.Run(Path.Combine(h.Root, "A"), "clone", "--quiet", "--config", "core.autocrlf=false", "-b", "feat/PROJ-3", h.Bare("A").Replace('\\', '/'), other);
        await h.Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other, "suggest.txt"), "x\n");
        await h.Run(other, "add", "-A");
        await h.Run(other, "commit", "--quiet", "-m", "suggestion");
        await h.Run(other, "push", "--quiet");

        await h.OpOk("resume", new { key = "PROJ-3" });
        await h.Write("A", "a.txt", "mine v2\n");
        await h.OpOk("commit", new { key = "PROJ-3" });
        var p = await h.Op("push", new { key = "PROJ-3" });
        p.Status.ShouldBe("failed");
        p.Error!.ShouldContain("changed on origin");

        await h.OpOk("push", new { key = "PROJ-3", overwriteRemote = true });
        (await h.Run(h.Bare("A"), "show", "feat/PROJ-3:a.txt")).ShouldBe("mine v2");
    }
}
