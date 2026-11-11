using System.Text.Json;
using System.Text.RegularExpressions;

namespace TicketFlow.Core;

/// <summary>
/// Derives file exclusions from the Claude Code PreToolUse hooks that deny access to protected files,
/// i.e. hook commands of the form <c>grep -qiE '&lt;regex&gt;' &amp;&amp; echo '{…"permissionDecision":"deny"…}'</c>.
/// The regex is applied to repo-relative paths (substring match, like grep).
/// </summary>
public static partial class ClaudeHookExclusions
{
    public sealed record Result(List<Regex> Patterns, List<string> Sources, string SettingsPath, string? Error);

    [GeneratedRegex(@"grep\s+(?:-[A-Za-z]+\s+)*-(?<flags>[A-Za-z]*E[A-Za-z]*)\s+'(?<re>[^']+)'")]
    private static partial Regex GrepRegex();

    public static string DefaultSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly Lock CacheLock = new();
    private static (string Path, DateTime Stamp, Result Result)? _cache;

    /// <summary>Reads (and caches by file timestamp) the deny-hook patterns from Claude's settings.</summary>
    public static Result Load(string? settingsPath = null)
    {
        var path = string.IsNullOrWhiteSpace(settingsPath) ? DefaultSettingsPath() : settingsPath!;
        if (!File.Exists(path)) return new([], [], path, "Claude settings file not found");
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (CacheLock)
        {
            if (_cache is { } c && c.Path == path && c.Stamp == stamp) return c.Result;
        }
        Result result;
        try
        {
            result = Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            result = new([], [], path, $"Could not read Claude settings: {ex.Message}");
        }
        lock (CacheLock) _cache = (path, stamp, result);
        return result;
    }

    public static Result Parse(string settingsJson, string path = "")
    {
        using var doc = JsonDocument.Parse(settingsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var patterns = new List<Regex>();
        var sources = new List<string>();
        if (doc.RootElement.TryGetProperty("hooks", out var hooks) &&
            hooks.TryGetProperty("PreToolUse", out var pre) && pre.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in pre.EnumerateArray())
            {
                if (!group.TryGetProperty("hooks", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var h in list.EnumerateArray())
                {
                    if (!h.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String) continue;
                    var cmd = cmdEl.GetString()!;
                    if (!cmd.Contains("deny", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (Match m in GrepRegex().Matches(cmd))
                    {
                        var opts = RegexOptions.CultureInvariant;
                        if (m.Groups["flags"].Value.Contains('i')) opts |= RegexOptions.IgnoreCase;
                        try
                        {
                            patterns.Add(new Regex(m.Groups["re"].Value, opts));
                            sources.Add(m.Groups["re"].Value);
                        }
                        catch (ArgumentException) { /* not a .NET-compatible regex; skip */ }
                    }
                }
            }
        }
        return new(patterns, sources, path, patterns.Count == 0 ? "No deny hook with a grep pattern found" : null);
    }
}
