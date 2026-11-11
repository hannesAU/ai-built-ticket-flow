using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TicketFlow.Core.Reviews;

public sealed record ClaudeRunRequest(
    string Exe,
    string WorkDir,
    string Prompt,
    string? Model,
    double? MaxBudgetUsd,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string> Env,
    string LogFile)
{
    /// <summary>Local = your own ticket: read-only file tools over the snapshot plus <see cref="AddDirs"/>; Remote = someone else's PR through ./pr.sh.</summary>
    public ReviewMode Mode { get; init; } = ReviewMode.Remote;
    /// <summary>Local mode: repo folders Claude may read for context.</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];
    /// <summary>Local mode: extra deny rules (files Claude must never read).</summary>
    public IReadOnlyList<string> Deny { get; init; } = [];
}

public enum ReviewMode { Remote, Local }

/// <summary>What one headless run produced. Output is the structured output (null when the run failed).</summary>
public sealed record ClaudeRunResult(bool Ok, string? Error, JsonNode? Output, double? CostUsd, int? Turns, string? Model, int Denials);

/// <summary>Runs a review session; a test double replaces it.</summary>
public interface IClaudeRunner
{
    /// <param name="onProgress">One short line per step (a command Claude runs, or its commentary).</param>
    Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, Action<string> onProgress, CancellationToken ct);
}

/// <summary>
/// Headless <c>claude -p</c> locked down for reviewing untrusted PRs: only the Bash tool, only the generated ./pr.sh,
/// dontAsk mode (everything else is denied, never prompted), no user/project settings (their allow rules would widen
/// access), no MCP servers and no saved session.
/// </summary>
public sealed class ClaudeCli : IClaudeRunner
{
    // set when Ticket Flow is started from inside a Claude Code session; a nested session may refuse to start
    private static readonly string[] StripEnv = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT"];

    /// <summary>The configured path, else claude.exe on PATH, in ~/.local/bin or the newest VS Code extension.</summary>
    public static (string? Path, string? Error) Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = configured.Trim().Trim('"');
            if (!File.Exists(p)) return (null, $"Claude Code not found at {p}");
            if (p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                return (null, $"{p} is a script; point Claude path at claude.exe instead");
            return (p, null);
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "claude.exe" : "claude");
                if (File.Exists(c)) return (c, null);
            }
            catch { /* bad PATH entry */ }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        if (File.Exists(local)) return (local, null);
        var ext = Path.Combine(home, ".vscode", "extensions");
        if (Directory.Exists(ext))
        {
            var best = Directory.EnumerateDirectories(ext, "anthropic.claude-code-*")
                .Select(d => (Dir: d, Exe: Path.Combine(d, "resources", "native-binary", OperatingSystem.IsWindows() ? "claude.exe" : "claude"),
                    Version: ParseVersion(Path.GetFileName(d))))
                .Where(x => File.Exists(x.Exe))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (best.Exe is not null) return (best.Exe, null);
        }
        return (null, "Claude Code (claude.exe) not found — install it or set its path under Settings → PR reviews");
    }

    private static Version ParseVersion(string dirName)
    {
        var parts = dirName.Split('-');
        return parts.Length > 2 && Version.TryParse(parts[2], out var v) ? v : new Version(0, 0);
    }

    /// <summary>
    /// Remote (someone else's PR): only Bash, only ./pr.sh, and no user settings — their allow rules (gh api, git push…)
    /// would widen what an untrusted PR could talk Claude into. Local (your own ticket): no Bash at all, only Read/Grep/Glob,
    /// with your user settings loaded so your deny rules and protected-file hook keep applying, plus extra deny rules.
    /// </summary>
    public static List<string> Args(ClaudeRunRequest req)
    {
        var local = req.Mode == ReviewMode.Local;
        var a = new List<string>
        {
            "-p",
            "--output-format", "stream-json", "--verbose",
            "--json-schema", ReviewPrompt.Schema,
            "--append-system-prompt", local ? TicketReview.SystemPrompt : ReviewPrompt.SystemPrompt,
            "--tools", local ? "Read,Grep,Glob" : "Bash",
            "--permission-mode", "dontAsk",
            "--setting-sources", local ? "user" : "local",
            "--strict-mcp-config",
            "--no-session-persistence",
        };
        if (local)
            foreach (var d in req.AddDirs) a.AddRange(["--add-dir", d]);
        if (!string.IsNullOrWhiteSpace(req.Model)) a.AddRange(["--model", req.Model.Trim()]);
        if (req.MaxBudgetUsd is > 0) a.AddRange(["--max-budget-usd", req.MaxBudgetUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (local)
        {
            if (req.Deny.Count > 0)
            {
                a.Add("--disallowedTools");
                a.AddRange(req.Deny);
            }
        }
        else
        {
            a.Add("--allowedTools");
            a.AddRange(ReviewPrompt.AllowedTools);
        }
        return a;
    }

    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest req, Action<string> onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(req.Exe)
        {
            WorkingDirectory = req.WorkDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in Args(req)) psi.ArgumentList.Add(arg);
        foreach (var k in StripEnv) psi.Environment.Remove(k);
        foreach (var (k, v) in req.Env) psi.Environment[k] = v;

        var parser = new StreamParser(onProgress);
        var stderr = new StringBuilder();
        await using var log = new StreamWriter(req.LogFile, false, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new Lock();
        void Log(string line)
        {
            lock (gate) log.WriteLine(line);
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ClaudeRunResult(false, $"Could not start {req.Exe}: {ex.Message}", null, null, null, null, 0);
        }
        await proc.StandardInput.WriteAsync(req.Prompt);
        proc.StandardInput.Close();

        var outPump = Task.Run(async () =>
        {
            while (await proc.StandardOutput.ReadLineAsync() is { } line)
            {
                Log(line);
                parser.Feed(line);
            }
        }, CancellationToken.None);
        var errPump = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync() is { } line)
            {
                Log("[stderr] " + line);
                lock (stderr) if (stderr.Length < 4000) stderr.AppendLine(line);
            }
        }, CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(req.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(3000, CancellationToken.None));
            ct.ThrowIfCancellationRequested();
            return new ClaudeRunResult(false, $"Timed out after {req.Timeout.TotalMinutes:0.#} min", null, parser.Cost, parser.Turns, parser.Model, parser.Denials);
        }
        await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(5000, CancellationToken.None));

        var err = stderr.ToString().Trim();
        if (parser.Output is not null && !parser.IsError)
            return new ClaudeRunResult(true, null, parser.Output, parser.Cost, parser.Turns, parser.Model, parser.Denials);
        var why = parser.ResultError ?? (err.Length > 0 ? err : $"claude exited with code {proc.ExitCode} without a review");
        return new ClaudeRunResult(false, why, null, parser.Cost, parser.Turns, parser.Model, parser.Denials);
    }

    /// <summary>Reads claude's stream-json output: progress from tool calls, the structured output and the final result line.</summary>
    public sealed class StreamParser(Action<string> onProgress)
    {
        public JsonNode? Output { get; private set; }
        public bool IsError { get; private set; }
        public string? ResultError { get; private set; }
        public double? Cost { get; private set; }
        public int? Turns { get; private set; }
        public string? Model { get; private set; }
        public int Denials { get; private set; }

        public void Feed(string line)
        {
            if (line.Length == 0 || line[0] != '{') return;
            JsonNode? n;
            try { n = JsonNode.Parse(line); }
            catch (JsonException) { return; }
            if (n is null) return;
            switch (n["type"]?.GetValue<string>())
            {
                case "system" when n["subtype"]?.GetValue<string>() == "init":
                    Model = n["model"]?.GetValue<string>();
                    break;
                case "assistant":
                    foreach (var c in n["message"]?["content"]?.AsArray() ?? [])
                    {
                        switch (c?["type"]?.GetValue<string>())
                        {
                            case "tool_use" when c["name"]?.GetValue<string>() == "StructuredOutput":
                                Output = c["input"]?.DeepClone();
                                onProgress("Writing the review");
                                break;
                            case "tool_use":
                                var cmd = c["input"]?["command"]?.GetValue<string>();
                                onProgress(cmd is null ? c["name"]?.GetValue<string>() ?? "tool" : "$ " + cmd);
                                break;
                            case "text" when c["text"]?.GetValue<string>() is { Length: > 0 } t:
                                var first = t.Trim().Split('\n')[0];
                                onProgress(first.Length > 160 ? first[..160] + "…" : first);
                                break;
                        }
                    }
                    break;
                case "result":
                    IsError = n["is_error"]?.GetValue<bool>() ?? false;
                    Cost = n["total_cost_usd"]?.GetValue<double>();
                    Turns = n["num_turns"]?.GetValue<int>();
                    Denials = n["permission_denials"]?.AsArray().Count ?? 0;
                    if (n["structured_output"] is { } so && so.GetValueKind() == JsonValueKind.Object) Output = so.DeepClone();
                    var subtype = n["subtype"]?.GetValue<string>();
                    if (IsError || subtype != "success")
                        ResultError = subtype switch
                        {
                            "error_max_turns" => "Stopped: too many turns",
                            "error_max_budget_usd" => "Stopped: the max budget was reached",
                            "error_max_structured_output_retries" => "Claude could not produce a valid review",
                            _ => n["result"]?.GetValue<string>() is { Length: > 0 } r ? r : $"Review failed ({subtype})",
                        };
                    break;
            }
        }
    }
}
