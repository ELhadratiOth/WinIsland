using System.Text;
using System.Text.Json;

namespace WinIsland.Core.Claude;

/// <summary>
/// Extracts lightweight metadata from a Claude Code JSONL transcript without loading the
/// whole file: only a bounded chunk from the end (and, if needed, the start) is read.
/// </summary>
public static class ClaudeTranscriptReader
{
    internal const int ChunkSize = 64 * 1024;

    public static string? ReadWorkingDirectory(string transcriptPath)
    {
        using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length == 0)
        {
            return null;
        }

        // Newest lines first: the latest cwd wins if the user changed directory mid-session.
        long tailStart = Math.Max(0, length - ChunkSize);
        string? cwd = FindCwd(ReadChunk(stream, tailStart, (int)(length - tailStart)), skipFirstLine: tailStart > 0, newestFirst: true);
        if (cwd is not null || tailStart == 0)
        {
            return cwd;
        }

        // Tail was dominated by a huge line (e.g. a large tool result); try the beginning.
        return FindCwd(ReadChunk(stream, 0, (int)Math.Min(ChunkSize, length)), skipFirstLine: false, newestFirst: false);
    }

    /// <summary>Best-effort project name when no cwd is recorded: the last segment of Claude's encoded directory name.</summary>
    public static string ProjectNameFromDirectory(string projectDirectoryName)
    {
        ArgumentNullException.ThrowIfNull(projectDirectoryName);
        string trimmed = projectDirectoryName.TrimEnd('-');
        int index = trimmed.LastIndexOf('-');
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }

    private static string ReadChunk(FileStream stream, long offset, int count)
    {
        byte[] buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        int read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? FindCwd(string text, bool skipFirstLine, bool newestFirst)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int start = skipFirstLine ? 1 : 0;

        // When reading from the start the last line may be truncated; JSON parsing rejects it anyway.
        for (int i = 0; i < lines.Length - start; i++)
        {
            string line = newestFirst ? lines[lines.Length - 1 - i] : lines[start + i];
            if (!line.Contains("\"cwd\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("cwd", out JsonElement cwd) &&
                    cwd.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(cwd.GetString()))
                {
                    return cwd.GetString();
                }
            }
            catch (JsonException)
            {
                // Partial line at a chunk boundary.
            }
        }

        return null;
    }
}
