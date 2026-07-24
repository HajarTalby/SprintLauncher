using System.Text;
using System.Text.Json;

namespace SprintLauncher.CodexWatcher;

public sealed class OffsetStore
{
    private readonly string _path;
    private readonly Dictionary<string, OffsetEntry> _entries;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public OffsetStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public OffsetEntry Get(string rolloutPath)
    {
        lock (_gate)
        {
            var key = Normalize(rolloutPath);
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new OffsetEntry();
            return entry;
        }
    }

    /// <summary>Returns only UTF-8 lines ending in LF and advances the persisted byte cursor.</summary>
    public IReadOnlyList<string> ReadNewLines(string rolloutPath)
    {
        lock (_gate)
        {
            var entry = Get(rolloutPath);
            if (!File.Exists(rolloutPath)) return [];

            try
            {
                var creationTimeUtcTicks = File.GetCreationTimeUtc(rolloutPath).Ticks;
                if (entry.FileCreationTimeUtcTicks != 0 && entry.FileCreationTimeUtcTicks != creationTimeUtcTicks)
                    entry.Reset(); // replacement under the same filename, before it has become shorter than the old cursor.
                entry.FileCreationTimeUtcTicks = creationTimeUtcTicks;

                using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length < entry.Offset)
                    entry.Reset(); // truncation or a replaced file: its identity and dedup set are no longer valid.
                if (stream.Length == entry.Offset) return [];

                stream.Seek(entry.Offset, SeekOrigin.Begin);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
                if (lastNewline < 0) return [];

                var completeLength = lastNewline + 1;
                var text = Encoding.UTF8.GetString(bytes, 0, completeLength);
                entry.Offset += completeLength;
                Save();
                return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions), Encoding.UTF8);
            File.Move(temporaryPath, _path, true);
        }
    }

    private static Dictionary<string, OffsetEntry> Load(string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OffsetEntry>>(File.ReadAllText(path));
            return loaded is null
                ? new Dictionary<string, OffsetEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, OffsetEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { return new Dictionary<string, OffsetEntry>(StringComparer.OrdinalIgnoreCase); }
        catch (JsonException) { return new Dictionary<string, OffsetEntry>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}

public sealed class OffsetEntry
{
    public long Offset { get; set; }
    public long FileCreationTimeUtcTicks { get; set; }
    public SessionMeta? SessionMeta { get; set; }
    public HashSet<string> SeenTurnIds { get; set; } = new(StringComparer.Ordinal);

    public void Reset()
    {
        Offset = 0;
        FileCreationTimeUtcTicks = 0;
        SessionMeta = null;
        SeenTurnIds.Clear();
    }
}
