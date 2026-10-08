using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinIsland.Core.Claude;

public enum ApprovalDecision
{
    /// <summary>Let the tool run.</summary>
    Allow,

    /// <summary>Refuse it (Claude is told why).</summary>
    Deny,

    /// <summary>No decision: Claude Code shows its normal prompt in the terminal.</summary>
    Ask,
}

/// <summary>A Claude Code permission prompt forwarded by the PermissionRequest hook.</summary>
public sealed class ApprovalRequest
{
    private readonly TaskCompletionSource<ApprovalDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalRequest(string toolName, string summary, string project, string? sessionId)
    {
        ToolName = toolName;
        Summary = summary;
        Project = project;
        SessionId = sessionId;
    }

    public string ToolName { get; }

    /// <summary>What will run: the command, the file, the URL…</summary>
    public string Summary { get; }

    public string Project { get; }

    public string? SessionId { get; }

    /// <summary>"Run a command", "Edit a file"…</summary>
    public string Action => ToolName switch
    {
        "Bash" or "PowerShell" => "Run a command",
        "Edit" or "MultiEdit" => "Edit a file",
        "Write" => "Write a file",
        "NotebookEdit" => "Edit a notebook",
        "Read" => "Read a file",
        "WebFetch" => "Fetch a web page",
        "WebSearch" => "Search the web",
        _ when ToolName.StartsWith("mcp__", StringComparison.Ordinal) => "Use a tool",
        _ => $"Use {ToolName}",
    };

    /// <summary>Display name of the tool ("Bash", "github · create_issue").</summary>
    public string ToolLabel => ToolName.StartsWith("mcp__", StringComparison.Ordinal)
        ? string.Join(" · ", ToolName["mcp__".Length..].Split("__", 2))
        : ToolName;

    public Task<ApprovalDecision> Decision => _decision.Task;

    public bool IsResolved => _decision.Task.IsCompleted;

    /// <summary>Raised once the request is decided or abandoned.</summary>
    public event EventHandler? Resolved;

    public void Resolve(ApprovalDecision decision)
    {
        if (_decision.TrySetResult(decision))
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A non-blocking note from Claude Code (e.g. it is waiting for input).</summary>
public sealed record ClaudeNotice(string Message, string Project);

/// <summary>Receives Claude Code hook calls (a named pipe on Windows).</summary>
public interface IClaudeHookServer
{
    /// <summary>Raised on a background thread; resolve the request to answer Claude Code.</summary>
    event EventHandler<ApprovalRequest>? PermissionRequested;

    /// <summary>Raised on a background thread.</summary>
    event EventHandler<ClaudeNotice>? NoticeReceived;
}

/// <summary>Claude Code hook JSON in and out.</summary>
public static class ClaudeHookPayload
{
    public static ApprovalRequest? ParsePermission(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string tool = root.TryGetProperty("tool_name", out JsonElement t) ? t.GetString() ?? "Tool" : "Tool";
            string summary = root.TryGetProperty("tool_input", out JsonElement input) ? Summarize(tool, input) : string.Empty;
            return new ApprovalRequest(tool, summary, ProjectOf(root), root.TryGetProperty("session_id", out JsonElement s) ? s.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ClaudeNotice? ParseNotice(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
            return new ClaudeNotice(message.Length > 0 ? message : "Claude is waiting for your input", ProjectOf(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The hook's stdout for a decision; empty for <see cref="ApprovalDecision.Ask"/> (normal prompt).</summary>
    public static string BuildResponse(ApprovalDecision decision)
    {
        if (decision == ApprovalDecision.Ask)
        {
            return string.Empty;
        }

        var decisionNode = new JsonObject { ["behavior"] = decision == ApprovalDecision.Allow ? "allow" : "deny" };
        if (decision == ApprovalDecision.Deny)
        {
            decisionNode["message"] = "The user denied this from WinIsland.";
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decisionNode,
            },
        }.ToJsonString();
    }

    internal static string Summarize(string tool, JsonElement input)
    {
        string? Get(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? text = tool switch
        {
            "Bash" or "PowerShell" => Get("command"),
            "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit" => Get("file_path") ?? Get("notebook_path"),
            "WebFetch" => Get("url"),
            "WebSearch" => Get("query"),
            _ => null,
        };

        text ??= input.ValueKind == JsonValueKind.Object ? input.GetRawText() : string.Empty;
        text = text.Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private static string ProjectOf(JsonElement root)
    {
        string cwd = root.TryGetProperty("cwd", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
        string trimmed = cwd.TrimEnd('\\', '/');
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}

/// <summary>
/// Adds or removes WinIsland's hooks in Claude Code's user settings (~/.claude/settings.json),
/// leaving every other setting and hook untouched. WinIsland's entries are recognised by the
/// "--claude-hook" argument.
/// </summary>
public static class ClaudeHooksInstaller
{
    public const string Marker = "--claude-hook";

    public static string DefaultSettingsPath()
    {
        string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string root = !string.IsNullOrWhiteSpace(configDir)
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, "settings.json");
    }

    public static bool IsInstalled(string settingsPath)
    {
        try
        {
            return File.Exists(settingsPath) && Load(settingsPath)["hooks"] is JsonObject hooks &&
                hooks.Any(e => e.Value is JsonArray groups && groups.Any(IsOurs));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the new settings JSON (pure; see <see cref="Install(string, string)"/> to write it).</summary>
    public static string Apply(string? existingJson, string exePath, bool install)
    {
        JsonObject root = string.IsNullOrWhiteSpace(existingJson)
            ? []
            : JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new JsonException("settings.json is not a JSON object.");

        JsonObject hooks = root["hooks"] as JsonObject ?? [];
        foreach ((string _, JsonNode? value) in hooks.ToList())
        {
            if (value is JsonArray groups)
            {
                foreach (JsonNode? ours in groups.Where(IsOurs).ToList())
                {
                    groups.Remove(ours);
                }
            }
        }

        if (install)
        {
            Add(hooks, "PermissionRequest", null, Handler(exePath, "permission", 300));
            Add(hooks, "Notification", "idle_prompt", Handler(exePath, "notify", 10));
        }

        foreach ((string name, JsonNode? value) in hooks.ToList())
        {
            if (value is JsonArray { Count: 0 })
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count > 0)
        {
            root["hooks"] = hooks;
        }
        else
        {
            root.Remove("hooks");
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Install(string settingsPath, string exePath) => Write(settingsPath, exePath, install: true);

    public static void Uninstall(string settingsPath, string exePath) => Write(settingsPath, exePath, install: false);

    /// <summary>
    /// A path without spaces works as a plain command in both Git Bash and PowerShell; with
    /// spaces, the exec form (command + args, no shell) avoids quoting problems.
    /// </summary>
    internal static JsonObject Handler(string exePath, string kind, int timeoutSeconds)
    {
        var handler = new JsonObject { ["type"] = "command" };
        if (exePath.Contains(' ', StringComparison.Ordinal))
        {
            handler["command"] = exePath;
            handler["args"] = new JsonArray(Marker, kind);
        }
        else
        {
            handler["command"] = $"{exePath.Replace('\\', '/')} {Marker} {kind}";
        }

        handler["timeout"] = timeoutSeconds;
        return handler;
    }

    private static void Add(JsonObject hooks, string eventName, string? matcher, JsonObject handler)
    {
        JsonArray groups = hooks[eventName] as JsonArray ?? [];
        var group = new JsonObject();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(handler);
        groups.Add((JsonNode)group);
        hooks[eventName] = groups;
    }

    private static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray handlers && handlers.Any(h =>
            (h?["command"]?.GetValue<string>() ?? string.Empty).Contains(Marker, StringComparison.Ordinal) ||
            (h?["args"] is JsonArray args && args.Any(a => a?.GetValue<string>() == Marker)));

    private static JsonObject Load(string settingsPath) =>
        JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];

    private static void Write(string settingsPath, string exePath, bool install)
    {
        string? existing = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        string updated = Apply(existing, exePath, install);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        if (existing is not null)
        {
            // One backup of the user's original file, never overwritten.
            string backup = settingsPath + ".winisland-backup";
            if (!File.Exists(backup))
            {
                File.WriteAllText(backup, existing);
            }
        }

        string temp = settingsPath + ".tmp";
        File.WriteAllText(temp, updated);
        File.Move(temp, settingsPath, overwrite: true);
    }
}
