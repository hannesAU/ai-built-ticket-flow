using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TicketFlow.Core.Checks;

/// <summary>
/// One command of a repo's "Build &amp; test" check. <see cref="Display"/> is what the user sees; the process is
/// <see cref="FileName"/> with <see cref="Args"/> (or the raw <see cref="RawArgs"/> string for user-typed commands).
/// </summary>
public sealed record CheckCommand(
    string Phase,
    string Label,
    string Display,
    string Cwd,
    string FileName,
    IReadOnlyList<string> Args,
    string? RawArgs = null,
    string? NotReady = null)
{
    public const string Build = "build";
    public const string Test = "test";
}

/// <summary>What "Build &amp; test" runs in one repo: builds first, then unit tests.</summary>
public sealed record CheckPlan(string Repo, List<CheckCommand> Commands, List<string> Notes)
{
    public bool IsEmpty => Commands.Count == 0;
}

/// <summary>
/// Works out how to build and unit test a repo: per-repo commands from config win, otherwise
/// .NET solutions (unit test projects only) and package.json build/test scripts are detected.
/// </summary>
public static partial class CheckDetector
{
    public const string Skip = "none";
    private const int MaxDepth = 3;

    private static readonly HashSet<string> Pruned = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".vscode", ".github", "node_modules", "bin", "obj", "packages", "dist", "TestResults", "artifacts",
    };

    /// <summary>Test projects whose name says they're not unit tests (IntegrationTests, E2E.Tests, Tests.Performance, …) are not run.</summary>
    private static readonly HashSet<string> NonUnitKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "integration", "e2e", "endtoend", "functional", "acceptance", "performance", "perf", "load", "stress", "smoke",
        "scenario", "scenarios", "benchmark", "benchmarks", "ui",
    };

    // word boundaries: separators, "fooBar", "E2ETests" (acronym followed by a word)
    [GeneratedRegex(@"[._\-\s]+|(?<=[a-z0-9])(?=[A-Z][a-z])|(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBreak();

    [GeneratedRegex(@"Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true|MSTest\.Sdk|""xunit\.v3|""TUnit""|NUnit3TestAdapter", RegexOptions.IgnoreCase)]
    private static partial Regex TestProjectMarker();

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.(?:cs|fs|vb)proj)""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SlnProjectLine();

    public static bool IsSkip(string? command) => string.Equals(command?.Trim(), Skip, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnitTestProjectName(string projectName)
    {
        var words = WordBreak().Split(projectName).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return true;
        if (projectName.Contains("EndToEnd", StringComparison.OrdinalIgnoreCase)) return false;
        var last = words[^1];
        if (NonUnitKinds.Contains(last)) return false; // Shop.Tests.Integration, Shop.Benchmarks
        var isTests = last.Equals("tests", StringComparison.OrdinalIgnoreCase) || last.Equals("test", StringComparison.OrdinalIgnoreCase);
        return !(isTests && words.Count >= 2 && NonUnitKinds.Contains(words[^2])); // IntegrationTests, E2E.Tests
    }

    public static CheckPlan Plan(RepoConfig rc)
    {
        var root = Path.GetFullPath(rc.Path);
        var notes = new List<string>();
        var commands = new List<CheckCommand>();
        if (!Directory.Exists(root)) return new CheckPlan(rc.DisplayName, commands, ["Folder not found"]);

        var autoBuild = string.IsNullOrWhiteSpace(rc.BuildCommand);
        var autoTest = string.IsNullOrWhiteSpace(rc.TestCommand);
        var solutions = autoBuild || autoTest ? FindSolutions(root) : [];
        var node = autoBuild || autoTest ? NodeProject.TryLoad(root) : null;

        if (IsSkip(rc.BuildCommand)) notes.Add("Build is turned off for this repo (Settings)");
        else if (!autoBuild) commands.Add(Shell(CheckCommand.Build, "Build", rc.BuildCommand!.Trim(), root));
        else
        {
            foreach (var sln in solutions) commands.Add(DotnetBuild(root, sln));
            if (node?.HasBuild == true) commands.Add(node.Command(CheckCommand.Build, "Build (package.json)", "run", "build"));
        }

        if (IsSkip(rc.TestCommand)) notes.Add("Unit tests are turned off for this repo (Settings)");
        else if (!autoTest) commands.Add(Shell(CheckCommand.Test, "Unit tests", rc.TestCommand!.Trim(), root));
        else
        {
            foreach (var sln in solutions)
            {
                var tests = sln.Projects.Where(IsTestProject).ToList();
                var unit = tests.Where(p => IsUnitTestProjectName(Path.GetFileNameWithoutExtension(p))).ToList();
                var other = tests.Except(unit).Select(Path.GetFileNameWithoutExtension).ToList();
                if (other.Count > 0) notes.Add($"Not run (not unit tests): {string.Join(", ", other)}");
                if (tests.Count == 0) notes.Add($"No test projects in {Rel(root, sln.Path)}");
                var mtp = UsesTestingPlatform(root, sln.Dir);
                foreach (var p in unit) commands.Add(DotnetTest(sln, p, mtp));
            }
            if (node?.HasTest == true) commands.Add(node.Command(CheckCommand.Test, "Unit tests (package.json)", "test"));
        }

        if (commands.Count == 0 && notes.Count == 0)
            notes.Add("Nothing to build or test was found — set build/test commands for this repo in Settings");
        return new CheckPlan(rc.DisplayName, commands, notes);
    }

    /// <summary>One-line description of the plan (Settings shows it as the auto-detected default).</summary>
    public static (string? Build, string? Test) Describe(RepoConfig rc)
    {
        var auto = new RepoConfig { Path = rc.Path, Name = rc.Name };
        var plan = Plan(auto);
        string? Join(string phase)
        {
            var list = plan.Commands.Where(c => c.Phase == phase).ToList();
            if (list.Count == 0) return null;
            if (phase == CheckCommand.Test && list.Count > 1 && list.All(c => c.FileName == "dotnet"))
                return $"dotnet test ({list.Count} unit test projects)";
            return string.Join(" && ", list.Select(c => c.Display));
        }
        return (Join(CheckCommand.Build), Join(CheckCommand.Test));
    }

    // ------------------------------------------------------------------ .NET

    public sealed record Solution(string Path, List<string> Projects)
    {
        public string Dir => System.IO.Path.GetDirectoryName(Path)!;
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    /// <summary>The shallowest .sln/.slnx files (a folder with both keeps the .slnx); root .csproj files if there are none.</summary>
    public static List<Solution> FindSolutions(string root)
    {
        var found = new List<(int Depth, string Path)>();
        var queue = new Queue<(string Dir, int Depth)>([(root, 0)]);
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (found.Count > 0 && depth > found.Min(f => f.Depth)) break;
            IEnumerable<string> files, dirs;
            try
            {
                files = Directory.EnumerateFiles(dir).ToList();
                dirs = depth < MaxDepth ? Directory.EnumerateDirectories(dir).ToList() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            found.AddRange(files.Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                .Select(f => (depth, f)));
            foreach (var d in dirs.Where(d => !Pruned.Contains(Path.GetFileName(d))).Order(StringComparer.OrdinalIgnoreCase))
                queue.Enqueue((d, depth + 1));
        }

        if (found.Count == 0)
            return Directory.EnumerateFiles(root, "*.csproj").Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => new Solution(p, [p])).ToList();

        var shallowest = found.Min(f => f.Depth);
        return found.Where(f => f.Depth == shallowest).Select(f => f.Path)
            .GroupBy(p => Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).First())
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => new Solution(p, SolutionProjects(p)))
            .ToList();
    }

    /// <summary>Absolute paths of the existing projects listed in a .sln or .slnx.</summary>
    public static List<string> SolutionProjects(string slnPath)
    {
        var dir = Path.GetDirectoryName(slnPath)!;
        IEnumerable<string> rel;
        try
        {
            if (slnPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                rel = XDocument.Load(slnPath).Descendants()
                    .Where(e => e.Name.LocalName == "Project")
                    .Select(e => (string?)e.Attribute("Path"))
                    .Where(p => p is not null)!;
            else
                rel = SlnProjectLine().Matches(File.ReadAllText(slnPath)).Select(m => m.Groups[1].Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
        return rel
            .Select(r => Path.GetFullPath(Path.Combine(dir, r.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsTestProject(string projectPath)
    {
        try { return TestProjectMarker().IsMatch(File.ReadAllText(projectPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// True when the nearest global.json (searching up from <paramref name="dir"/> to the repo root) opts
    /// <c>dotnet test</c> into Microsoft.Testing.Platform, whose command line differs from VSTest's.
    /// </summary>
    public static bool UsesTestingPlatform(string root, string dir)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var d = Path.GetFullPath(dir); d is not null; d = Path.GetDirectoryName(d))
        {
            var gj = Path.Combine(d, "global.json");
            if (File.Exists(gj))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(gj), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    return doc.RootElement.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.Object &&
                           t.TryGetProperty("runner", out var r) && r.ValueKind == JsonValueKind.String &&
                           string.Equals(r.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is JsonException or IOException) { return false; }
            }
            if (string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static CheckCommand DotnetBuild(string root, Solution sln)
    {
        string[] args = ["build", sln.FileName, "--nologo", "-v", "q", "-clp:ErrorsOnly"];
        var where = Rel(root, sln.Dir);
        return new CheckCommand(CheckCommand.Build, $"Build {sln.FileName}" + (where.Length > 0 ? $" (in {where})" : ""),
            "dotnet " + string.Join(' ', args.Select(Quote)), sln.Dir, "dotnet", args);
    }

    private static CheckCommand DotnetTest(Solution sln, string project, bool testingPlatform)
    {
        var rel = Path.GetRelativePath(sln.Dir, project);
        // VSTest's "minimal" console logger prints each failing test with its message, but not the passing ones
        string[] args = testingPlatform
            ? ["test", "--project", rel, "--no-build", "--no-progress", "--no-ansi"]
            : ["test", rel, "--no-build", "--nologo", "-v", "q", "--logger", "console;verbosity=minimal"];
        return new CheckCommand(CheckCommand.Test, $"Unit tests: {Path.GetFileNameWithoutExtension(project)}",
            "dotnet " + string.Join(' ', args.Select(Quote)), sln.Dir, "dotnet", args);
    }

    // ------------------------------------------------------------------ node

    private sealed class NodeProject(string root, string manager, bool hasBuild, bool hasTest)
    {
        public bool HasBuild => hasBuild;
        public bool HasTest => hasTest;

        public static NodeProject? TryLoad(string root)
        {
            var pj = Path.Combine(root, "package.json");
            if (!File.Exists(pj)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return null;
                string? Script(string name) =>
                    scripts.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString() : null;
                var test = Script("test");
                // npm init's placeholder ("echo \"Error: no test specified\" && exit 1") isn't a test suite
                if (test is not null && test.Contains("no test specified", StringComparison.OrdinalIgnoreCase)) test = null;
                var manager = File.Exists(Path.Combine(root, "yarn.lock")) ? "yarn"
                    : File.Exists(Path.Combine(root, "pnpm-lock.yaml")) ? "pnpm"
                    : "npm";
                return new NodeProject(root, manager, Script("build") is not null, test is not null);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return null;
            }
        }

        public CheckCommand Command(string phase, string label, params string[] args)
        {
            var notReady = Directory.Exists(Path.Combine(root, "node_modules")) ? null : $"node_modules is missing — run '{manager} install' in this repo first";
            var display = $"{manager} {string.Join(' ', args)}";
            // npm/yarn/pnpm are .cmd shims on Windows, which only cmd.exe can start
            return OperatingSystem.IsWindows()
                ? new CheckCommand(phase, label, display, root, "cmd.exe", ["/d", "/c", manager, .. args], NotReady: notReady)
                : new CheckCommand(phase, label, display, root, manager, args, NotReady: notReady);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static CheckCommand Shell(string phase, string label, string command, string root) =>
        OperatingSystem.IsWindows()
            ? new CheckCommand(phase, label, command, root, "cmd.exe", [], RawArgs: $"/d /s /c \"{command}\"")
            : new CheckCommand(phase, label, command, root, "/bin/sh", ["-c", command]);

    private static string Rel(string root, string path)
    {
        var r = Path.GetRelativePath(root, path);
        return r == "." ? "" : r;
    }

    private static string Quote(string a) => a.IndexOfAny([' ', ';', '&', '|']) >= 0 ? $"\"{a}\"" : a;
}
