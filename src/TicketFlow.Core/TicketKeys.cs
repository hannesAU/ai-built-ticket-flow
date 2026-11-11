using System.Text.RegularExpressions;

namespace TicketFlow.Core;

/// <summary>
/// Ticket = "PROJ-1234". Key = ticket plus optional uniqueness suffix: "PROJ-1234" or "PROJ-1234_2".
/// Work without a ticket number gets a virtual ticket: a slug of its description ("bump-dependencies", "bump-dependencies_2").
/// Local branch = "wip/&lt;key&gt;". Remote branch = "&lt;feat|fix|chore&gt;/&lt;key&gt;".
/// </summary>
public static partial class TicketKeys
{
    public const string WipPrefix = "wip/";

    /// <summary>Change types; the first is the default.</summary>
    public static readonly IReadOnlyList<string> Types = ["feat", "fix", "chore"];

    [GeneratedRegex(@"_(\d+)$")]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex(@"^([A-Za-z]+)-(\d+)(?:_(\d+))?$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^\s*([A-Za-z]+)?\s*[-_ ]?\s*(\d+)\s*$")]
    private static partial Regex InputRegex();

    /// <summary>Normalises "proj1234", "PROJ_1234", "proj-1234", "1234" to "PROJ-1234". Returns null if unrecognisable.</summary>
    public static string? NormalizeTicket(string? input, IReadOnlyList<string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = InputRegex().Match(input);
        if (!m.Success) return null;
        var prefix = m.Groups[1].Success ? m.Groups[1].Value : prefixes.FirstOrDefault();
        if (string.IsNullOrEmpty(prefix)) return null;
        return $"{prefix.ToUpperInvariant()}-{long.Parse(m.Groups[2].Value)}";
    }

    public static bool TryParseKey(string key, out string ticket, out int suffix)
    {
        var m = KeyRegex().Match(key);
        if (!m.Success)
        {
            ticket = "";
            suffix = 0;
            return false;
        }
        ticket = $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        suffix = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 1;
        return true;
    }

    public static string TicketOf(string key) =>
        TryParseKey(key, out var t, out _) ? t : SuffixRegex().Replace(key, "");

    /// <summary>True for keys of work without a ticket number (they don't look like "PROJ-1234").</summary>
    public static bool IsVirtual(string key) => !KeyRegex().IsMatch(key);

    /// <summary>
    /// Virtual ticket for work without a ticket number: a branch-safe slug of the description, e.g. "Bump dependencies" →
    /// "bump-dependencies". Never looks like a real ticket ("node 18" → "chore-node-18").
    /// </summary>
    public static string VirtualTicket(string description, string type)
    {
        const int max = 40;
        var slug = NonSlugRegex().Replace(description.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > max)
        {
            var cut = slug.LastIndexOf('-', max);
            slug = slug[..(cut > 10 ? cut : max)].Trim('-');
        }
        var t = NormalizeType(type);
        if (slug.Length == 0) return t;
        return KeyRegex().IsMatch(slug) ? $"{t}-{slug}" : slug;
    }

    public static string MakeKey(string ticket, int suffix) => suffix <= 1 ? ticket : $"{ticket}_{suffix}";

    public static string WipBranch(string key) => WipPrefix + key;

    public static bool TryKeyFromBranch(string? branch, out string key)
    {
        key = "";
        if (branch is null || !branch.StartsWith(WipPrefix, StringComparison.Ordinal)) return false;
        key = branch[WipPrefix.Length..];
        return key.Length > 0;
    }

    public static string NormalizeType(string? type) =>
        Types.FirstOrDefault(t => string.Equals(t, type?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Types[0];

    /// <summary>"feat(PROJ-1234): description", or "chore: description" for a virtual ticket.</summary>
    public static string CommitMessage(string type, string key, string description) =>
        IsVirtual(key)
            ? $"{NormalizeType(type)}: {description.Trim()}"
            : $"{NormalizeType(type)}({TicketOf(key)}): {description.Trim()}";

    /// <summary>Remote branch names a key could have been pushed as, one per type.</summary>
    public static IEnumerable<string> RemoteBranches(string key) => Types.Select(t => $"{t}/{key}");

    public static string RemoteBranch(string type, string key) => $"{NormalizeType(type)}/{key}";

    /// <summary>Picks the lowest suffix whose key is not taken.</summary>
    public static string Allocate(string ticket, Func<string, bool> isTaken)
    {
        for (var n = 1; n < 1000; n++)
        {
            var key = MakeKey(ticket, n);
            if (!isTaken(key)) return key;
        }
        throw new InvalidOperationException($"No free key for {ticket}");
    }
}
