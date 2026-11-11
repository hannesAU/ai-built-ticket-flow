using System.Text.Json;
using System.Text.Json.Serialization;

namespace TicketFlow.Core;

public sealed class AppConfig
{
    /// <summary>Ticket prefixes accepted when typing a ticket number (first one is the default for bare numbers).</summary>
    public List<string> TicketPrefixes { get; set; } = ["PROJ"];

    /// <summary>Repos, in merge order.</summary>
    public List<RepoConfig> Repos { get; set; } = [];

    /// <summary>Extra globs (repo-relative) that are never committed or discarded, only carried across branch switches.</summary>
    public List<string> Excluded { get; set; } = [];

    /// <summary>
    /// Also exclude every file matched by the Claude Code PreToolUse deny hook(s) in <see cref="ClaudeSettingsPath"/>
    /// (default ~/.claude/settings.json), so Ticket Flow never commits what Claude is never allowed to touch.
    /// </summary>
    public bool ExcludeFromClaudeHook { get; set; } = true;

    public string? ClaudeSettingsPath { get; set; }

    public GitHubConfig GitHub { get; set; } = new();
    public string? JiraBaseUrl { get; set; }
    public int PollSeconds { get; set; } = 60;
    public int BackgroundPollSeconds { get; set; } = 300;
    public int BackupRetentionDays { get; set; } = 30;

    /// <summary>"Build &amp; test": each build or test command is killed after this long.</summary>
    public int CheckTimeoutMinutes { get; set; } = 30;

    /// <summary>Automated Claude Code reviews of other people's open PRs (repos opt in with <see cref="RepoConfig.ReviewPrs"/>).</summary>
    public ReviewConfig Reviews { get; set; } = new();

    public int Port { get; set; } = 5199;
    public bool OpenBrowserOnStart { get; set; } = true;
}

public sealed class RepoConfig
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }

    /// <summary>Overrides the auto-detected default branch (origin/HEAD).</summary>
    public string? DefaultBranch { get; set; }

    public string MergeMethod { get; set; } = "squash";

    /// <summary>When false, a PR with no review requirement counts as approved.</summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>"Build &amp; test" build command run in the repo root (empty = auto-detect, "none" = skip).</summary>
    public string? BuildCommand { get; set; }

    /// <summary>"Build &amp; test" unit test command run in the repo root (empty = auto-detect, "none" = skip).</summary>
    public string? TestCommand { get; set; }

    /// <summary>Poll this repo for other people's open PRs and review each new head commit with Claude Code.</summary>
    public bool ReviewPrs { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'))
        : Name!;
}

public sealed class GitHubConfig
{
    /// <summary>gh account used for all API calls (empty = gh's active account).</summary>
    public string User { get; set; } = "";
}

public enum BotPrMode { Hidden, Manual, Auto }

public sealed class ReviewConfig
{
    /// <summary>Path to claude.exe (empty = found on PATH, ~/.local/bin or the VS Code extension).</summary>
    public string? ClaudePath { get; set; }

    /// <summary>Model alias or id passed to --model (empty = Claude Code's default).</summary>
    public string? Model { get; set; }

    /// <summary>Each review is stopped after this long.</summary>
    public int TimeoutMinutes { get; set; } = 20;

    /// <summary>Passed to --max-budget-usd when set.</summary>
    public double? MaxBudgetUsd { get; set; }

    /// <summary>PRs opened by bots (dependabot etc.): hidden from the list, listed for manual review, or reviewed automatically.</summary>
    public BotPrMode BotPrs { get; set; } = BotPrMode.Hidden;


    /// <summary>Extra instructions appended to every review prompt (team conventions, focus areas…).</summary>
    public string? Instructions { get; set; }
}

public sealed class AppPaths
{
    public required string ConfigDir { get; init; }
    public required string DataDir { get; init; }

    public string ConfigFile => System.IO.Path.Combine(ConfigDir, "config.json");
    public string ArchiveFile => System.IO.Path.Combine(DataDir, "archive.json");
    public string JournalFile => System.IO.Path.Combine(DataDir, "journal.jsonl");
    public string TrashFile => System.IO.Path.Combine(DataDir, "trash.json");
    public string TokenFile => System.IO.Path.Combine(DataDir, "token");
    public string ChecksFile => System.IO.Path.Combine(DataDir, "checks.json");
    public string CheckLogsDir => System.IO.Path.Combine(DataDir, "check-logs");
    public string ReviewsFile => System.IO.Path.Combine(DataDir, "pr-reviews.json");
    public string ReviewsDir => System.IO.Path.Combine(DataDir, "pr-reviews");

    public static AppPaths Default()
    {
        var home = Environment.GetEnvironmentVariable("TICKETFLOW_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Create(home, home);

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configDir = System.IO.Path.Combine(roaming, "ticket-flow");
        var dataDir = System.IO.Path.Combine(local, "ticket-flow");
        return Create(configDir, dataDir);
    }

    public static AppPaths Create(string configDir, string dataDir)
    {
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(dataDir);
        return new AppPaths { ConfigDir = configDir, DataDir = dataDir };
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

public sealed class ConfigStore(AppPaths paths)
{
    private readonly Lock _lock = new();
    private AppConfig? _current;

    public event Action<AppConfig>? Changed;

    public AppConfig Current
    {
        get
        {
            lock (_lock)
                return _current ??= Load();
        }
    }

    private AppConfig Load()
    {
        if (!File.Exists(paths.ConfigFile))
        {
            var cfg = Seed();
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(cfg, Json.Options));
            return cfg;
        }
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(paths.ConfigFile), Json.Options) ?? new AppConfig();
    }

    /// <summary>The checked-in config.example.json (embedded), or null if it isn't in the build.</summary>
    public static AppConfig? Example()
    {
        using var s = typeof(ConfigStore).Assembly.GetManifestResourceStream("config.example.json");
        return s is null ? null : JsonSerializer.Deserialize<AppConfig>(s, Json.Options);
    }

    /// <summary>First-run config: the example without its placeholder repos and Jira URL.</summary>
    public static AppConfig Seed()
    {
        var cfg = Example() ?? new AppConfig();
        cfg.Repos = [];
        cfg.JiraBaseUrl = null;
        return cfg;
    }

    public void Save(AppConfig cfg)
    {
        lock (_lock)
        {
            File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(cfg, Json.Options));
            _current = cfg;
        }
        Changed?.Invoke(cfg);
    }
}
