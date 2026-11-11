namespace TicketFlow.Core.Git;

public enum ChangeKind { Modified, Added, Deleted, Renamed, Untracked, Conflicted, TypeChanged }

public sealed record FileChange(string Path, string? OrigPath, char X, char Y, ChangeKind Kind)
{
    public bool Excluded { get; init; }

    /// <summary>Every path this change touches (for pathspecs): renames include the original path.</summary>
    public IEnumerable<string> Paths => OrigPath is null ? [Path] : [Path, OrigPath];

    public string StatusLetter => Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "?",
        ChangeKind.Conflicted => "U",
        ChangeKind.TypeChanged => "T",
        _ => "M",
    };
}

public sealed class StatusResult
{
    public string? Oid { get; set; }
    /// <summary>Current branch, or null when detached.</summary>
    public string? Head { get; set; }
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public List<FileChange> Changes { get; } = [];
}

/// <summary>Parser for <c>git status --porcelain=v2 -z --branch --untracked-files=all</c>.</summary>
public static class PorcelainV2
{
    public static readonly string[] Args = ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"];

    public static StatusResult Parse(string output)
    {
        var result = new StatusResult();
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0) continue;
            switch (t[0])
            {
                case '#':
                    ParseHeader(t, result);
                    break;
                case '1':
                {
                    var p = t.Split(' ', 9);
                    if (p.Length < 9) break;
                    result.Changes.Add(Make(p[8], null, p[1], renamed: false));
                    break;
                }
                case '2':
                {
                    var p = t.Split(' ', 10);
                    if (p.Length < 10) break;
                    var orig = i + 1 < tokens.Length ? tokens[++i] : null;
                    result.Changes.Add(Make(p[9], orig, p[1], renamed: true));
                    break;
                }
                case 'u':
                {
                    var p = t.Split(' ', 11);
                    if (p.Length < 11) break;
                    result.Changes.Add(new FileChange(p[10], null, p[1][0], p[1][1], ChangeKind.Conflicted));
                    break;
                }
                case '?':
                    result.Changes.Add(new FileChange(t[2..], null, '?', '?', ChangeKind.Untracked));
                    break;
            }
        }
        return result;
    }

    private static FileChange Make(string path, string? orig, string xy, bool renamed)
    {
        char x = xy[0], y = xy[1];
        var kind = renamed ? ChangeKind.Renamed
            : x == 'A' ? ChangeKind.Added
            : x == 'D' || y == 'D' ? ChangeKind.Deleted
            : x == 'T' || y == 'T' ? ChangeKind.TypeChanged
            : ChangeKind.Modified;
        return new FileChange(path, orig, x, y, kind);
    }

    private static void ParseHeader(string line, StatusResult r)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length < 3) return;
        switch (parts[1])
        {
            case "branch.oid":
                r.Oid = parts[2] == "(initial)" ? null : parts[2];
                break;
            case "branch.head":
                r.Head = parts[2] == "(detached)" ? null : parts[2];
                break;
            case "branch.upstream":
                r.Upstream = parts[2];
                break;
            case "branch.ab":
                var ab = parts[2].Split(' ');
                if (ab.Length == 2)
                {
                    r.Ahead = int.Parse(ab[0].TrimStart('+'));
                    r.Behind = int.Parse(ab[1].TrimStart('-'));
                }
                break;
        }
    }

    /// <summary>Parses NUL-separated <c>git diff --name-status -z --no-renames</c> output.</summary>
    public static List<(char Status, string Path)> ParseNameStatus(string output)
    {
        var list = new List<(char, string)>();
        var t = output.Split('\0');
        for (var i = 0; i + 1 < t.Length; i += 2)
        {
            if (t[i].Length == 0) break;
            list.Add((t[i][0], t[i + 1]));
        }
        return list;
    }
}
