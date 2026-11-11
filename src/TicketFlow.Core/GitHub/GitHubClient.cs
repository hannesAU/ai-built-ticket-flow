using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TicketFlow.Core.GitHub;

public sealed record PrInfo(
    int Number,
    string Url,
    string Title,
    string State,          // OPEN | MERGED | CLOSED
    bool IsDraft,
    string? Mergeable,     // MERGEABLE | CONFLICTING | UNKNOWN
    string? MergeState,    // BEHIND | BLOCKED | CLEAN | DIRTY | DRAFT | HAS_HOOKS | UNKNOWN | UNSTABLE
    string? ReviewDecision,// APPROVED | CHANGES_REQUESTED | REVIEW_REQUIRED | null
    string? Checks,        // SUCCESS | FAILURE | ERROR | PENDING | EXPECTED | null
    string HeadSha,
    string BaseRef,
    DateTimeOffset? MergedAt);

public sealed record PrQuery(string Owner, string Repo, string HeadBranch);

public interface IGitHubClient
{
    Task<string> GetViewerAsync(CancellationToken ct = default);
    /// <summary>Returns the most relevant PR per query (open first, then merged, then closed); missing = no PR.</summary>
    Task<Dictionary<PrQuery, PrInfo>> GetPrsAsync(IReadOnlyCollection<PrQuery> queries, CancellationToken ct = default);
    Task<PrInfo> CreatePrAsync(string owner, string repo, string head, string @base, string title, string body, bool draft, CancellationToken ct = default);
    Task UpdatePrTitleAsync(string owner, string repo, int number, string title, CancellationToken ct = default);
    Task<string> MergePrAsync(string owner, string repo, int number, string method, string expectedHeadSha, string title, CancellationToken ct = default);
    Task ClosePrAsync(string owner, string repo, int number, CancellationToken ct = default);
    /// <summary>Deletes a remote branch; returns false when it was already gone.</summary>
    Task<bool> DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default);
    /// <summary>Open PRs per repo that <paramref name="viewer"/> did not author, with the viewer's latest review and review threads.</summary>
    Task<Dictionary<RepoRef, List<RemotePr>>> GetOpenPrsAsync(IReadOnlyCollection<RepoRef> repos, string viewer, CancellationToken ct = default);
    /// <summary>The token of the configured gh account (handed to child processes as GH_TOKEN).</summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);
}

public sealed class GitHubException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>GitHub REST + GraphQL client authenticated with the gh CLI's token for the configured account.</summary>
public sealed class GitHubClient(Func<string> user) : IGitHubClient
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
    private (string User, string Token, DateTime At)? _token;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var u = user();
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } t && t.User == u && DateTime.UtcNow - t.At < TimeSpan.FromMinutes(10)) return t.Token;
            var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            psi.ArgumentList.Add("--hostname");
            psi.ArgumentList.Add("github.com");
            if (!string.IsNullOrWhiteSpace(u))
            {
                psi.ArgumentList.Add("--user");
                psi.ArgumentList.Add(u);
            }
            using var p = Process.Start(psi) ?? throw new GitHubException("Could not start gh");
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                throw new GitHubException($"gh auth token failed{(string.IsNullOrWhiteSpace(u) ? "" : $" for {u}")}: {stderr.Trim()}");
            _token = (u, stdout.Trim(), DateTime.UtcNow);
            return _token.Value.Token;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitHubException("GitHub CLI (gh) is not installed or not on PATH.");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct, bool allow404 = false)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        req.Headers.UserAgent.ParseAdd("ticket-flow/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (allow404 && resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return null;
        if (!resp.IsSuccessStatusCode)
        {
            string msg;
            try
            {
                var n = JsonNode.Parse(text);
                msg = n?["message"]?.GetValue<string>() ?? text;
                var errs = n?["errors"]?.AsArray().Select(e => e?["message"]?.GetValue<string>() ?? e?.ToJsonString()).Where(s => s is not null);
                if (errs?.Any() == true) msg += " — " + string.Join("; ", errs);
            }
            catch { msg = text; }
            throw new GitHubException($"GitHub {(int)resp.StatusCode}: {msg}", resp.StatusCode);
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async Task<string> GetViewerAsync(CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, "user", null, ct))?["login"]?.GetValue<string>() ?? "";

    private const string PrFields =
        "number url title state isDraft mergeable mergeStateStatus reviewDecision headRefOid baseRefName mergedAt " +
        "commits(last:1){nodes{commit{statusCheckRollup{state}}}}";

    public async Task<Dictionary<PrQuery, PrInfo>> GetPrsAsync(IReadOnlyCollection<PrQuery> queries, CancellationToken ct = default)
    {
        var result = new Dictionary<PrQuery, PrInfo>();
        if (queries.Count == 0) return result;
        var list = queries.Distinct().ToList();
        foreach (var chunk in list.Chunk(25))
        {
            var sb = new StringBuilder("query{");
            var byRepo = chunk.Select((q, i) => (q, i)).GroupBy(x => (x.q.Owner, x.q.Repo)).ToList();
            for (var r = 0; r < byRepo.Count; r++)
            {
                var (owner, repo) = byRepo[r].Key;
                sb.Append($"r{r}:repository(owner:{JsonSerializer.Serialize(owner)},name:{JsonSerializer.Serialize(repo)}){{");
                foreach (var (q, i) in byRepo[r])
                    sb.Append($"p{i}:pullRequests(headRefName:{JsonSerializer.Serialize(q.HeadBranch)},first:10,orderBy:{{field:CREATED_AT,direction:DESC}}){{nodes{{{PrFields}}}}}");
                sb.Append('}');
            }
            sb.Append('}');

            var resp = await SendAsync(HttpMethod.Post, "graphql", new { query = sb.ToString() }, ct);
            var data = resp?["data"];
            if (data is null)
                throw new GitHubException("GraphQL: " + (resp?["errors"]?.ToJsonString() ?? "no data"));

            for (var r = 0; r < byRepo.Count; r++)
            {
                var repoNode = data[$"r{r}"];
                if (repoNode is null) continue;
                foreach (var (q, i) in byRepo[r])
                {
                    var nodes = repoNode[$"p{i}"]?["nodes"]?.AsArray();
                    if (nodes is null || nodes.Count == 0) continue;
                    var prs = nodes.Where(n => n is not null).Select(n => ParsePr(n!)).ToList();
                    var best = prs.FirstOrDefault(p => p.State == "OPEN") ?? prs.FirstOrDefault(p => p.State == "MERGED") ?? prs[0];
                    result[q] = best;
                }
            }
        }
        return result;
    }

    private static PrInfo ParsePr(JsonNode n) => new(
        n["number"]!.GetValue<int>(),
        n["url"]!.GetValue<string>(),
        n["title"]?.GetValue<string>() ?? "",
        n["state"]!.GetValue<string>(),
        n["isDraft"]?.GetValue<bool>() ?? false,
        n["mergeable"]?.GetValue<string>(),
        n["mergeStateStatus"]?.GetValue<string>(),
        n["reviewDecision"]?.GetValue<string>(),
        n["commits"]?["nodes"]?.AsArray().FirstOrDefault()?["commit"]?["statusCheckRollup"]?["state"]?.GetValue<string>(),
        n["headRefOid"]?.GetValue<string>() ?? "",
        n["baseRefName"]?.GetValue<string>() ?? "",
        n["mergedAt"]?.GetValue<string>() is { } m ? DateTimeOffset.Parse(m) : null);

    private static PrInfo ParseRestPr(JsonNode n) => new(
        n["number"]!.GetValue<int>(),
        n["html_url"]!.GetValue<string>(),
        n["title"]?.GetValue<string>() ?? "",
        n["merged_at"] is not null && n["merged_at"]!.GetValueKind() == JsonValueKind.String ? "MERGED" : n["state"]!.GetValue<string>().ToUpperInvariant(),
        n["draft"]?.GetValue<bool>() ?? false,
        null, null, null, null,
        n["head"]?["sha"]?.GetValue<string>() ?? "",
        n["base"]?["ref"]?.GetValue<string>() ?? "",
        null);

    public async Task<PrInfo> CreatePrAsync(string owner, string repo, string head, string @base, string title, string body, bool draft, CancellationToken ct = default)
    {
        var n = await SendAsync(HttpMethod.Post, $"repos/{owner}/{repo}/pulls", new { title, head, @base, body, draft }, ct);
        return ParseRestPr(n!);
    }

    public async Task UpdatePrTitleAsync(string owner, string repo, int number, string title, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Patch, $"repos/{owner}/{repo}/pulls/{number}", new { title }, ct);

    public async Task<string> MergePrAsync(string owner, string repo, int number, string method, string expectedHeadSha, string title, CancellationToken ct = default)
    {
        var n = await SendAsync(HttpMethod.Put, $"repos/{owner}/{repo}/pulls/{number}/merge",
            new { merge_method = method, sha = expectedHeadSha, commit_title = title }, ct);
        return n?["sha"]?.GetValue<string>() ?? "";
    }

    public async Task ClosePrAsync(string owner, string repo, int number, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Patch, $"repos/{owner}/{repo}/pulls/{number}", new { state = "closed" }, ct);

    public Task<string> GetTokenAsync(CancellationToken ct = default) => TokenAsync(ct);

    private const string OpenPrFields =
        "number url title isDraft updatedAt additions deletions changedFiles headRefName baseRefName headRefOid baseRefOid " +
        "author{__typename login} " +
        "reviews(last:30){nodes{author{login} state submittedAt body url commit{oid}}} " +
        "reviewThreads(last:40){nodes{isResolved isOutdated path line originalLine comments(first:8){nodes{author{login} body}}}}";

    public async Task<Dictionary<RepoRef, List<RemotePr>>> GetOpenPrsAsync(IReadOnlyCollection<RepoRef> repos, string viewer, CancellationToken ct = default)
    {
        var result = new Dictionary<RepoRef, List<RemotePr>>();
        foreach (var chunk in repos.Distinct().Chunk(5))
        {
            var sb = new StringBuilder("query{");
            for (var i = 0; i < chunk.Length; i++)
                sb.Append($"r{i}:repository(owner:{JsonSerializer.Serialize(chunk[i].Owner)},name:{JsonSerializer.Serialize(chunk[i].Repo)}){{" +
                          $"pullRequests(states:OPEN,first:40,orderBy:{{field:UPDATED_AT,direction:DESC}}){{nodes{{{OpenPrFields}}}}}}}");
            sb.Append('}');
            var resp = await SendAsync(HttpMethod.Post, "graphql", new { query = sb.ToString() }, ct);
            var data = resp?["data"] ?? throw new GitHubException("GraphQL: " + (resp?["errors"]?.ToJsonString() ?? "no data"));
            for (var i = 0; i < chunk.Length; i++)
            {
                var nodes = data[$"r{i}"]?["pullRequests"]?["nodes"]?.AsArray();
                if (nodes is null) continue;
                result[chunk[i]] = nodes.Where(n => n is not null)
                    .Select(n => ParseRemotePr(chunk[i], n!, viewer))
                    .Where(p => !string.Equals(p.Author, viewer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
        return result;
    }

    public static RemotePr ParseRemotePr(RepoRef repo, JsonNode n, string viewer)
    {
        bool Mine(JsonNode? author) => string.Equals(author?["login"]?.GetValue<string>(), viewer, StringComparison.OrdinalIgnoreCase);
        static DateTimeOffset? Date(JsonNode? v) => v?.GetValue<string>() is { } s ? DateTimeOffset.Parse(s) : null;

        var mine = n["reviews"]?["nodes"]?.AsArray()
            .Where(r => r is not null && Mine(r["author"]) && r["state"]?.GetValue<string>() is { } st && st != "PENDING")
            .LastOrDefault();
        var review = mine is null ? null : new MyReview(
            mine["state"]!.GetValue<string>(), Date(mine["submittedAt"]), mine["commit"]?["oid"]?.GetValue<string>(),
            mine["body"]?.GetValue<string>() ?? "", mine["url"]?.GetValue<string>());

        var threads = new List<MyThread>();
        foreach (var t in n["reviewThreads"]?["nodes"]?.AsArray() ?? [])
        {
            var comments = t?["comments"]?["nodes"]?.AsArray();
            if (t is null || comments is null || comments.Count == 0 || !Mine(comments[0]?["author"])) continue;
            threads.Add(new MyThread(
                t["path"]?.GetValue<string>() ?? "",
                t["line"]?.GetValue<int?>() ?? t["originalLine"]?.GetValue<int?>(),
                t["isResolved"]?.GetValue<bool>() ?? false,
                t["isOutdated"]?.GetValue<bool>() ?? false,
                comments.Where(c => c is not null)
                    .Select(c => new ThreadComment(c!["author"]?["login"]?.GetValue<string>() ?? "ghost", c["body"]?.GetValue<string>() ?? ""))
                    .ToList()));
        }

        return new RemotePr(
            repo.Owner, repo.Repo,
            n["number"]!.GetValue<int>(),
            n["url"]!.GetValue<string>(),
            n["title"]?.GetValue<string>() ?? "",
            n["author"]?["login"]?.GetValue<string>() ?? "ghost",
            n["author"]?["__typename"]?.GetValue<string>() == "Bot",
            n["isDraft"]?.GetValue<bool>() ?? false,
            n["headRefName"]?.GetValue<string>() ?? "",
            n["baseRefName"]?.GetValue<string>() ?? "",
            n["headRefOid"]?.GetValue<string>() ?? "",
            n["baseRefOid"]?.GetValue<string>() ?? "",
            n["additions"]?.GetValue<int>() ?? 0,
            n["deletions"]?.GetValue<int>() ?? 0,
            n["changedFiles"]?.GetValue<int>() ?? 0,
            Date(n["updatedAt"]) ?? DateTimeOffset.Now,
            review,
            threads);
    }

    public async Task<bool> DeleteBranchAsync(string owner, string repo, string branch, CancellationToken ct = default)
    {
        var path = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"repos/{owner}/{repo}/git/refs/heads/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        req.Headers.UserAgent.ParseAdd("ticket-flow/1.0");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return false;
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException($"GitHub {(int)resp.StatusCode} deleting {branch}: {await resp.Content.ReadAsStringAsync(ct)}", resp.StatusCode);
        return true;
    }
}
