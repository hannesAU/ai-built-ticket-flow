namespace TicketFlow.Core.GitHub;

/// <summary>An open PR in a watched repo, opened by someone else.</summary>
public sealed record RemotePr(
    string Owner,
    string Repo,
    int Number,
    string Url,
    string Title,
    string Author,
    bool AuthorIsBot,
    bool IsDraft,
    string HeadRef,
    string BaseRef,
    string HeadSha,
    string BaseSha,
    int Additions,
    int Deletions,
    int ChangedFiles,
    DateTimeOffset UpdatedAt,
    MyReview? MyReview,
    List<MyThread> MyThreads)
{
    public string Key => $"{Owner}/{Repo}#{Number}";
    public string FilesUrl => Url.TrimEnd('/') + "/files";
}

/// <summary>The viewer's latest submitted review of a PR. State: APPROVED | CHANGES_REQUESTED | COMMENTED | DISMISSED.</summary>
public sealed record MyReview(string State, DateTimeOffset? At, string? CommitSha, string Body, string? Url);

/// <summary>An inline review thread started by the viewer, with the replies to it.</summary>
public sealed record MyThread(string Path, int? Line, bool IsResolved, bool IsOutdated, List<ThreadComment> Comments);

public sealed record ThreadComment(string Author, string Body);

public sealed record RepoRef(string Owner, string Repo);
