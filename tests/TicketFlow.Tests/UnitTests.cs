using Shouldly;
using TicketFlow.Core;
using TicketFlow.Core.Git;
using TicketFlow.Core.GitHub;

namespace TicketFlow.Tests;

public class TicketKeyTests
{
    [Theory]
    [InlineData("PROJ-1234", "PROJ-1234")]
    [InlineData("proj1234", "PROJ-1234")]
    [InlineData("PROJ_1234", "PROJ-1234")]
    [InlineData(" proj 1234 ", "PROJ-1234")]
    [InlineData("1234", "PROJ-1234")]
    [InlineData("abc-0042", "ABC-42")]
    public void Normalizes_ticket_input(string input, string expected) =>
        TicketKeys.NormalizeTicket(input, ["PROJ"]).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("PROJ-")]
    [InlineData("hello world")]
    public void Rejects_garbage(string input) => TicketKeys.NormalizeTicket(input, ["PROJ"]).ShouldBeNull();

    [Fact]
    public void Parses_keys_with_suffix()
    {
        TicketKeys.TryParseKey("PROJ-1234_3", out var t, out var n).ShouldBeTrue();
        t.ShouldBe("PROJ-1234");
        n.ShouldBe(3);
        TicketKeys.TicketOf("PROJ-9_2").ShouldBe("PROJ-9");
    }

    [Fact]
    public void Allocates_lowest_free_suffix()
    {
        var taken = new HashSet<string> { "PROJ-1", "PROJ-1_2" };
        TicketKeys.Allocate("PROJ-1", taken.Contains).ShouldBe("PROJ-1_3");
        TicketKeys.Allocate("PROJ-2", taken.Contains).ShouldBe("PROJ-2");
    }

    [Fact]
    public void Commit_message_uses_ticket_without_suffix()
    {
        TicketKeys.CommitMessage("fix", "PROJ-77_2", " Rounding ").ShouldBe("fix(PROJ-77): Rounding");
        TicketKeys.RemoteBranch("FIX", "PROJ-77_2").ShouldBe("fix/PROJ-77_2");
        TicketKeys.RemoteBranch("whatever", "PROJ-1").ShouldBe("feat/PROJ-1");
    }
}

public class ExcludeMatcherTests
{
    private readonly ExcludeMatcher _m = new(["**/localconfig*.json", "*Factory.cs", "private/", "/root-only.txt", "docs/*.md"]);

    [Theory]
    [InlineData("localconfig.json", true)]
    [InlineData("src/Api/localconfig.Development.json", true)]
    [InlineData("src/Api/LocalConfig.Local.json", true)]
    [InlineData("src/DataStore/MyDbFactory.cs", true)]
    [InlineData("private/key.txt", true)]
    [InlineData("a/private/key.txt", true)]
    [InlineData("root-only.txt", true)]
    [InlineData("sub/root-only.txt", false)]
    [InlineData("docs/readme.md", true)]
    [InlineData("docs/deep/readme.md", false)]
    [InlineData("src/Api/Program.cs", false)]
    [InlineData("src/localconfigx.txt", false)]
    public void Matches(string path, bool expected) => _m.IsExcluded(path).ShouldBe(expected);
}

public class PorcelainTests
{
    [Fact]
    public void Parses_all_record_types()
    {
        var output = string.Join('\0',
            "# branch.oid 1111111111111111111111111111111111111111",
            "# branch.head wip/PROJ-1",
            "# branch.upstream origin/wip/PROJ-1",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaa bbb src/file with spaces.cs",
            "1 A. N... 000000 100644 100644 000 bbb src/new.cs",
            "1 .D N... 100644 100644 000000 aaa aaa gone.txt",
            "2 R. N... 100644 100644 100644 aaa bbb R100 new/name.cs",
            "old/name.cs",
            "u UU N... 100644 100644 100644 100644 a b c conflict.cs",
            "? untracked é.txt",
            "");
        var s = PorcelainV2.Parse(output);
        s.Head.ShouldBe("wip/PROJ-1");
        s.Ahead.ShouldBe(2);
        s.Behind.ShouldBe(1);
        s.Changes.Select(c => (c.Path, c.Kind)).ShouldBe(
        [
            ("src/file with spaces.cs", ChangeKind.Modified),
            ("src/new.cs", ChangeKind.Added),
            ("gone.txt", ChangeKind.Deleted),
            ("new/name.cs", ChangeKind.Renamed),
            ("conflict.cs", ChangeKind.Conflicted),
            ("untracked é.txt", ChangeKind.Untracked),
        ]);
        s.Changes[3].OrigPath.ShouldBe("old/name.cs");
        s.Changes[3].Paths.ShouldBe(["new/name.cs", "old/name.cs"]);
    }

    [Fact]
    public void Detached_head_is_null() =>
        PorcelainV2.Parse("# branch.oid abc\0# branch.head (detached)\0").Head.ShouldBeNull();

    [Fact]
    public void Parses_remote_urls()
    {
        RepoScanner.ParseRemote("git@github.com:acme/acme-api.git").ShouldBe(("acme", "acme-api"));
        RepoScanner.ParseRemote("https://github.com/org/repo").ShouldBe(("org", "repo"));
        RepoScanner.ParseRemote("git@github-ad:org/repo.git").ShouldBe(("org", "repo"));
    }
}

public class PrStatusTests
{
    private static PrInfo Pr(string state = "OPEN", bool draft = false, string? mergeable = "MERGEABLE", string? mss = "CLEAN",
        string? review = "APPROVED", string? checks = "SUCCESS") =>
        new(1, "u", "t", state, draft, mergeable, mss, review, checks, "sha", "master", null);

    [Fact]
    public void Maps_statuses()
    {
        PrStatusMapper.Map(null, true).ShouldBe(PrStatus.NotFound);
        PrStatusMapper.Map(Pr(state: "MERGED"), true).ShouldBe(PrStatus.Merged);
        PrStatusMapper.Map(Pr(state: "CLOSED"), true).ShouldBe(PrStatus.Closed);
        PrStatusMapper.Map(Pr(draft: true), true).ShouldBe(PrStatus.Draft);
        PrStatusMapper.Map(Pr(mergeable: "CONFLICTING", mss: "DIRTY"), true).ShouldBe(PrStatus.Conflicts);
        PrStatusMapper.Map(Pr(mss: "BEHIND"), true).ShouldBe(PrStatus.Behind);
        PrStatusMapper.Map(Pr(checks: "FAILURE"), true).ShouldBe(PrStatus.ChecksFailing);
        PrStatusMapper.Map(Pr(review: "CHANGES_REQUESTED"), true).ShouldBe(PrStatus.ChangesRequested);
        PrStatusMapper.Map(Pr(review: "REVIEW_REQUIRED", mss: "BLOCKED"), true).ShouldBe(PrStatus.ReviewRequired);
        PrStatusMapper.Map(Pr(checks: "PENDING"), true).ShouldBe(PrStatus.ChecksRunning);
        PrStatusMapper.Map(Pr(), true).ShouldBe(PrStatus.Approved);
    }

    [Fact]
    public void No_review_rules_respects_require_approval()
    {
        PrStatusMapper.Map(Pr(review: null), requireApproval: true).ShouldBe(PrStatus.ReviewRequired);
        PrStatusMapper.Map(Pr(review: null), requireApproval: false).ShouldBe(PrStatus.Approved);
    }
}

public class ClaudeHookExclusionTests
{
    // Same shape as a real deny hook, with neutral file names.
    private const string Settings = """
    {
      "hooks": {
        "PreToolUse": [
          { "matcher": "Read|Edit|Write|Bash",
            "hooks": [ { "type": "command", "shell": "bash",
              "command": "grep -qiE 'appsecrets[a-z0-9.]*\\.json|SecretKeys\\.cs' && echo '{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\"}}' || true" } ] },
          { "matcher": "Bash",
            "hooks": [ { "type": "command", "command": "grep -qE 'unrelated' && echo allow || true" } ] }
        ]
      }
    }
    """;

    [Fact]
    public void Extracts_deny_hook_regex_and_matches_like_grep()
    {
        var r = ClaudeHookExclusions.Parse(Settings);
        r.Error.ShouldBeNull();
        r.Sources.ShouldBe([@"appsecrets[a-z0-9.]*\.json|SecretKeys\.cs"]);

        var m = new ExcludeMatcher([], r.Patterns);
        m.IsExcluded("src/Api/appsecrets.json").ShouldBeTrue();
        m.IsExcluded("src/Api/AppSecrets.Development.json").ShouldBeTrue();
        m.IsExcluded("src/Data/MySecretKeys.cs").ShouldBeTrue();
        m.IsExcluded("src/Data/OtherFactory.cs").ShouldBeFalse();
        m.IsExcluded("src/Api/Program.cs").ShouldBeFalse();
    }

    [Fact]
    public void Missing_or_hookless_settings_yield_no_patterns()
    {
        ClaudeHookExclusions.Parse("{}").Patterns.ShouldBeEmpty();
        ClaudeHookExclusions.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.json")).Error.ShouldNotBeNull();
    }
}
