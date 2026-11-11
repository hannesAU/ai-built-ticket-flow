using System.Diagnostics;
using System.Text;

namespace TicketFlow.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Out => StdOut.TrimEnd('\r', '\n');
}

public class GitException(string message, GitResult? result = null) : Exception(message)
{
    public GitResult? Result { get; } = result;
}

/// <summary>Runs git as a child process with an argument list (never a shell string).</summary>
public sealed class GitCli
{
    public string Executable { get; init; } = "git";

    public async Task<GitResult> RunAsync(
        string cwd,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        bool readOnly = false,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=false");
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["LANG"] = "C";
        if (readOnly) psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new GitException($"Could not start git: {ex.Message}");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
            await proc.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        proc.StandardInput.Close();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            throw;
        }
        return new GitResult(proc.ExitCode, await outTask, await errTask);
    }
}

/// <summary>A repo-bound git runner that logs every command (used for op progress output).</summary>
public sealed class GitRepo(GitCli cli, string path, Action<string>? log = null)
{
    public string Path { get; } = path;
    public GitCli Cli { get; } = cli;
    public Action<string>? Log { get; set; } = log;
    public bool ReadOnly { get; init; }

    public static string Nul(IEnumerable<string> paths) => string.Concat(paths.Select(p => p + "\0"));

    public async Task<GitResult> TryAsync(IReadOnlyList<string> args, string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        if (!ReadOnly) Log?.Invoke("$ git " + string.Join(' ', args.Select(Quote)));
        var r = await Cli.RunAsync(Path, args, stdin, env, ReadOnly, ct);
        if (!ReadOnly)
        {
            var err = r.StdErr.Trim();
            if (err.Length > 0) Log?.Invoke(err);
        }
        return r;
    }

    public Task<GitResult> TryAsync(params string[] args) => TryAsync((IReadOnlyList<string>)args);

    public async Task<string> RunAsync(IReadOnlyList<string> args, string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var r = await TryAsync(args, stdin, env, ct);
        if (!r.Ok)
            throw new GitException(
                $"git {args.FirstOrDefault()} failed: {FirstLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut)}", r);
        return r.Out;
    }

    public Task<string> RunAsync(params string[] args) => RunAsync((IReadOnlyList<string>)args);

    public async Task<bool> RefExistsAsync(string refName) =>
        (await TryAsync("rev-parse", "--verify", "--quiet", refName + "^{commit}")).Ok;

    public async Task<string?> ResolveAsync(string refName)
    {
        var r = await TryAsync("rev-parse", "--verify", "--quiet", refName + "^{commit}");
        return r.Ok ? r.Out : null;
    }

    public async Task<string?> ResolveTreeAsync(string rev)
    {
        var r = await TryAsync("rev-parse", "--verify", "--quiet", rev + "^{tree}");
        return r.Ok ? r.Out : null;
    }

    private static string FirstLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var meaningful = lines.FirstOrDefault(l => l.StartsWith("error:") || l.StartsWith("fatal:")) ?? lines.FirstOrDefault();
        return meaningful ?? "(no output)";
    }

    private static string Quote(string a) => a.Length == 0 || a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a;
}
