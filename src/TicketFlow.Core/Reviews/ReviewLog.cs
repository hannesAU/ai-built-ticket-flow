using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TicketFlow.Core.Reviews;

/// <summary>Turns a review run's stream-json log into a readable transcript: Claude's notes, the commands it ran and their output.</summary>
public static class ReviewLog
{
    private const int MaxOutputLines = 40;

    private static readonly JsonSerializerOptions Readable = new(Json.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(string file)
    {
        var sb = new StringBuilder();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("[stderr] ", StringComparison.Ordinal))
            {
                sb.AppendLine(line);
                continue;
            }
            JsonNode? n;
            try { n = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            switch (n?["type"]?.GetValue<string>())
            {
                case "system" when n["subtype"]?.GetValue<string>() == "init":
                    sb.AppendLine($"── model {n["model"]?.GetValue<string>()} · tools {n["tools"]?.ToJsonString(Readable)} · {n["permissionMode"]?.GetValue<string>()}");
                    break;
                case "assistant":
                    foreach (var c in n["message"]?["content"]?.AsArray() ?? [])
                        switch (c?["type"]?.GetValue<string>())
                        {
                            case "text":
                                sb.AppendLine().AppendLine(c["text"]?.GetValue<string>()?.Trim());
                                break;
                            case "tool_use" when c["name"]?.GetValue<string>() == "StructuredOutput":
                                sb.AppendLine().AppendLine("── review").AppendLine(c["input"]?.ToJsonString(Readable));
                                break;
                            case "tool_use":
                                sb.AppendLine().AppendLine("$ " + (c["input"]?["command"]?.GetValue<string>() ?? c["name"]?.GetValue<string>()));
                                break;
                        }
                    break;
                case "user":
                    foreach (var c in n["message"]?["content"]?.AsArray() ?? [])
                    {
                        if (c?["type"]?.GetValue<string>() != "tool_result") continue;
                        var text = c["content"] switch
                        {
                            JsonValue v when v.TryGetValue<string>(out var s) => s,
                            JsonArray a => string.Join("\n", a.Select(x => x?["text"]?.GetValue<string>()).Where(x => x is not null)),
                            _ => "",
                        };
                        var lines = text.Replace("\r\n", "\n").Split('\n');
                        foreach (var l in lines.Take(MaxOutputLines)) sb.AppendLine("  " + l);
                        if (lines.Length > MaxOutputLines) sb.AppendLine($"  … {lines.Length - MaxOutputLines} more lines");
                    }
                    break;
                case "result":
                    sb.AppendLine().AppendLine(
                        $"── {n["subtype"]?.GetValue<string>()} · {n["num_turns"]?.GetValue<int>()} turns · " +
                        $"${n["total_cost_usd"]?.GetValue<double>():0.00} · {n["permission_denials"]?.AsArray().Count ?? 0} denied tool calls");
                    break;
            }
        }
        return sb.Length == 0 ? "(empty log)" : sb.ToString();
    }
}
