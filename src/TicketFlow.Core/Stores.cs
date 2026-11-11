using System.Text.Json;

namespace TicketFlow.Core;

public sealed record ArchivedLeg(string Repo, string RemoteBranch, int? PrNumber, string? PrUrl, string? MergeSha);

public sealed record ArchivedTicket(
    string Key,
    string Ticket,
    string Description,
    string Type,
    DateTimeOffset CompletedAt,
    List<ArchivedLeg> Legs);

public sealed record TrashEntry(string Repo, string RepoPath, string Key, string Ref, string Sha, string? Description, string? Type, string? RemoteName, DateTimeOffset At);

public sealed record JournalEntry(
    string Id,
    string Kind,
    string Title,
    string? Key,
    string Status,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    List<OpStepView> Steps);

/// <summary>Small JSON-file backed list store (single user, low volume).</summary>
public class JsonListStore<T>(string file)
{
    private readonly Lock _lock = new();

    public List<T> All()
    {
        lock (_lock)
        {
            if (!File.Exists(file)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), Json.Options) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Mutate(Action<List<T>> change)
    {
        lock (_lock)
        {
            var list = File.Exists(file) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), Json.Options) ?? [] : [];
            change(list);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json.Options));
            File.Move(tmp, file, true);
        }
    }

    public void Add(T item) => Mutate(l => l.Add(item));
}

public sealed class ArchiveStore(AppPaths p) : JsonListStore<ArchivedTicket>(p.ArchiveFile)
{
    public bool HasKey(string key) => All().Any(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
}

public sealed class TrashStore(AppPaths p) : JsonListStore<TrashEntry>(p.TrashFile);

public sealed class JournalStore(AppPaths paths)
{
    private readonly Lock _lock = new();

    public void Append(JournalEntry e)
    {
        lock (_lock)
            File.AppendAllText(paths.JournalFile, JsonSerializer.Serialize(e, Json.Compact) + "\n");
    }

    public List<JournalEntry> Read(string? key = null, int take = 100)
    {
        lock (_lock)
        {
            if (!File.Exists(paths.JournalFile)) return [];
            var list = new List<JournalEntry>();
            foreach (var line in File.ReadLines(paths.JournalFile).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JournalEntry? e;
                try { e = JsonSerializer.Deserialize<JournalEntry>(line, Json.Options); }
                catch { continue; }
                if (e is null || (key is not null && !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(e);
                if (list.Count >= take) break;
            }
            return list;
        }
    }
}
