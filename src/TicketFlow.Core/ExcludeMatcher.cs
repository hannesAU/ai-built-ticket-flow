using System.Text;
using System.Text.RegularExpressions;

namespace TicketFlow.Core;

/// <summary>
/// Gitignore-flavoured glob matching against repo-relative paths (forward slashes).
/// "*" = within a segment, "**" = across segments, "?" = one char. A pattern without "/" matches the
/// file name at any depth. A pattern matching a directory also matches everything below it.
/// Case-insensitive (Windows).
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly Regex[] _patterns;

    private readonly Regex[] _regexes;

    /// <param name="globs">Glob patterns (anchored, see class remarks).</param>
    /// <param name="regexes">Raw regexes matched anywhere in the path (grep-style), e.g. from the Claude deny hook.</param>
    public ExcludeMatcher(IEnumerable<string> globs, IEnumerable<Regex>? regexes = null)
    {
        _patterns = globs
            .Select(g => g.Trim())
            .Where(g => g.Length > 0 && !g.StartsWith('#'))
            .Select(Compile)
            .ToArray();
        _regexes = regexes?.ToArray() ?? [];
    }

    public static ExcludeMatcher FromConfig(AppConfig cfg) =>
        new(cfg.Excluded, cfg.ExcludeFromClaudeHook ? ClaudeHookExclusions.Load(cfg.ClaudeSettingsPath).Patterns : null);

    public bool IsExcluded(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        foreach (var re in _patterns)
            if (re.IsMatch(p))
                return true;
        foreach (var re in _regexes)
            if (re.IsMatch(p))
                return true;
        return false;
    }

    internal static Regex Compile(string glob)
    {
        var g = glob.Replace('\\', '/');
        if (g.EndsWith('/')) g = g.TrimEnd('/');
        if (g.StartsWith('/')) g = g[1..];
        else if (!g.Contains('/')) g = "**/" + g;

        var sb = new StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                var dbl = i + 1 < g.Length && g[i + 1] == '*';
                if (dbl)
                {
                    var slash = i + 2 < g.Length && g[i + 2] == '/';
                    sb.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append("(?:/.*)?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
