using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TicketFlow.Core.Checks;

public sealed record CommandOutcome(int ExitCode, bool TimedOut, TimeSpan Elapsed)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs a build/test command, streaming its output line by line; kills the whole process tree on cancel or timeout.</summary>
public static partial class CommandRunner
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Ansi();

    // set by `dotnet run`/the SDK; inheriting them can make a child build resolve the wrong SDK
    private static readonly string[] StripEnv = ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly", "DOTNET_LAUNCH_PROFILE"];

    public static string StripAnsi(string s) => s.Contains('\x1B') ? Ansi().Replace(s, "") : s;

    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled (the process is killed first).</exception>
    public static async Task<CommandOutcome> RunAsync(CheckCommand cmd, Action<string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cmd.FileName)
        {
            WorkingDirectory = cmd.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (cmd.RawArgs is not null) psi.Arguments = cmd.RawArgs;
        else foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        foreach (var k in StripEnv) psi.Environment.Remove(k);
        psi.Environment["CI"] = "true"; // no watch modes, no interactive prompts
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en"; // output is parsed for test counts
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["FORCE_COLOR"] = "0";

        var sw = Stopwatch.StartNew();
        var gate = new Lock();
        void Emit(string line)
        {
            lock (gate) onLine(StripAnsi(line));
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Emit($"Could not start {cmd.FileName}: {ex.Message}");
            return new CommandOutcome(-1, false, sw.Elapsed);
        }
        proc.StandardInput.Close();
        var pumps = Task.WhenAll(Pump(proc.StandardOutput, Emit), Pump(proc.StandardError, Emit));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await Task.WhenAny(pumps, Task.Delay(3000, CancellationToken.None));
            ct.ThrowIfCancellationRequested();
            Emit($"Timed out after {timeout.TotalMinutes:0.#} min — stopped");
            return new CommandOutcome(-1, true, sw.Elapsed);
        }
        // a lingering child (e.g. a build server) can hold the pipes open; don't wait on it forever
        await Task.WhenAny(pumps, Task.Delay(5000, CancellationToken.None));
        return new CommandOutcome(proc.ExitCode, false, sw.Elapsed);
    }

    private static async Task Pump(StreamReader reader, Action<string> emit)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line) emit(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* process killed */ }
    }
}

/// <summary>Streams a command's output to pick out test counts and the lines worth showing when it fails.</summary>
public sealed partial class OutputAnalyzer(string repoRoot)
{
    public const int MaxErrors = 30;
    private const int TailLines = 25;

    // VSTest: "Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 2 s - X.dll (net10.0)"
    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)")]
    private static partial Regex VsTestSummary();

    // Microsoft.Testing.Platform: "Test run summary: Passed!" then "  total: 144" / "  failed: 0" / "  succeeded: 143" / "  skipped: 1"
    [GeneratedRegex(@"^\s*(total|failed|succeeded|skipped):\s*(\d+)\s*$")]
    private static partial Regex MtpCount();

    // jest: "Tests:       1 failed, 2 skipped, 12 passed, 15 total"
    [GeneratedRegex(@"^\s*Tests:\s+(.*\d+ total)")]
    private static partial Regex JestSummary();

    [GeneratedRegex(@"(\d+) (failed|passed|skipped|todo|total)")]
    private static partial Regex JestCount();

    // compiler/build errors, jest failures, npm/yarn/node errors
    [GeneratedRegex(@"\berror [A-Z]{1,8}\d{2,6}\b|: error\s*:|^\s*●\s+\S|^\s*FAIL\s+\S|^npm ERR!|^error\s|^\s*Error:\s|Unhandled exception|Could not start")]
    private static partial Regex ErrorLine();

    // a failing test: VSTest "  Failed Ns.Class.Test [5 ms]" / testing platform "failed Ns.Class.Test (88ms)"
    [GeneratedRegex(@"^\s*Failed\s+(\S.*?)\s+\[[^\]]*\]\s*$")]
    private static partial Regex VsTestFailed();

    [GeneratedRegex(@"^failed\s+(\S.*?)\s+\([^)]*\)\s*$")]
    private static partial Regex MtpFailed();

    [GeneratedRegex(@"\s+\[[^\]]+\.(?:cs|fs|vb)proj(?:::[^\]]*)?\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectSuffix();

    private const int MaxFailureLines = 6;

    private int _total, _passed, _failed, _skipped;
    private bool _sawTests, _inMtpSummary;
    private readonly List<string> _errors = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _tail = new();
    private readonly string _root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    // the failing test being read: its name, then its message lines (no stack trace)
    private List<string>? _failure;
    private bool _failureIsMtp, _inMessage;

    /// <summary>Error lines and failing tests ("Failed Name" + message lines, as one entry each). Call <see cref="Complete"/> first.</summary>
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyCollection<string> Tail => _tail;
    public TestCounts? Tests => _sawTests ? new TestCounts(_total, _passed, _failed, _skipped) : null;

    /// <summary>End of output: keeps a failing test whose message ran to the end.</summary>
    public void Complete() => FlushFailure();

    private void FlushFailure()
    {
        if (_failure is null) return;
        AddError(string.Join("\n", _failure));
        _failure = null;
    }

    private void AddError(string entry)
    {
        if (_errors.Count < MaxErrors && _seen.Add(entry)) _errors.Add(entry);
    }

    /// <summary>True when the line belongs to the failing test being read.</summary>
    private bool ReadFailure(string line)
    {
        var vs = VsTestFailed().Match(line);
        var mtp = vs.Success ? Match.Empty : MtpFailed().Match(line);
        if (vs.Success || mtp.Success)
        {
            FlushFailure();
            _failure = ["Failed " + (vs.Success ? vs : mtp).Groups[1].Value];
            _failureIsMtp = mtp.Success;
            _inMessage = mtp.Success; // VSTest puts the message under "Error Message:"
            return true;
        }
        if (_failure is null) return false;

        var t = line.Trim();
        bool end;
        if (_failureIsMtp)
        {
            // MTP: indented "from <dll>", message lines, then "at ..." frames; anything unindented ends it
            if (t.StartsWith("from ", StringComparison.Ordinal) && _failure.Count == 1) return true;
            end = !line.StartsWith(' ') || t.StartsWith("at ", StringComparison.Ordinal);
        }
        else
        {
            // VSTest: "Error Message:" … "Stack Trace:" (message lines aren't always indented)
            if (t == "Error Message:") { _inMessage = true; return true; }
            end = t.StartsWith("Stack Trace:", StringComparison.Ordinal) || t.StartsWith("Standard Output", StringComparison.Ordinal) ||
                  VsTestSummary().IsMatch(line);
        }
        if (end)
        {
            FlushFailure();
            return false;
        }
        if (_inMessage && t.Length > 0 && _failure.Count <= MaxFailureLines) _failure.Add("  " + Shorten(t));
        return true;
    }

    public void Feed(string line)
    {
        _tail.Enqueue(line);
        if (_tail.Count > TailLines) _tail.Dequeue();
        if (ReadFailure(line)) return;

        if (VsTestSummary().Match(line) is { Success: true } v)
        {
            _sawTests = true;
            _failed += int.Parse(v.Groups[1].Value);
            _passed += int.Parse(v.Groups[2].Value);
            _skipped += int.Parse(v.Groups[3].Value);
            _total += int.Parse(v.Groups[4].Value);
            return;
        }
        if (line.Contains("Test run summary:", StringComparison.Ordinal))
        {
            _inMtpSummary = true;
            _sawTests = true;
            return;
        }
        if (_inMtpSummary)
        {
            if (MtpCount().Match(line) is { Success: true } m)
            {
                var n = int.Parse(m.Groups[2].Value);
                switch (m.Groups[1].Value)
                {
                    case "total": _total += n; break;
                    case "failed": _failed += n; break;
                    case "succeeded": _passed += n; break;
                    case "skipped": _skipped += n; break;
                }
                return;
            }
            if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("duration:", StringComparison.Ordinal)) _inMtpSummary = false;
        }
        if (JestSummary().Match(line) is { Success: true } j)
        {
            _sawTests = true;
            foreach (Match c in JestCount().Matches(j.Groups[1].Value))
            {
                var n = int.Parse(c.Groups[1].Value);
                switch (c.Groups[2].Value)
                {
                    case "total": _total += n; break;
                    case "failed": _failed += n; break;
                    case "passed": _passed += n; break;
                    default: _skipped += n; break;
                }
            }
            return;
        }

        if (ErrorLine().IsMatch(line)) AddError(Shorten(line));
    }

    /// <summary>Repo-relative paths and no trailing "[...csproj]" so error lines fit on screen.</summary>
    private string Shorten(string line)
    {
        var s = ProjectSuffix().Replace(line.Trim(), "");
        return s.Replace(_root, "", StringComparison.OrdinalIgnoreCase);
    }
}
