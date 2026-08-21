using System.IO;
using System.Text.Json;

namespace OriginalLauncher;

/// <summary>
/// 起動したエントリの使用回数・最終使用日時を記録し、検索結果のランキング(頻度→最近使った順)に使う。
/// %AppData%\OriginalLauncher\usage.json に永続化する。
/// </summary>
public sealed class UsageStore
{
    private sealed class UsageEntry
    {
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, UsageEntry> _entries;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OriginalLauncher", "usage.json");

    public UsageStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    private static Dictionary<string, UsageEntry> Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(json, JsonOptions);
                if (loaded is not null)
                {
                    return new Dictionary<string, UsageEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (JsonException)
            {
                // 壊れた usage.json は無視して空の状態から再開する。
            }
        }

        return new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// エントリが起動されたことを記録する。
    /// </summary>
    public void RecordLaunch(string fullPath)
    {
        if (_entries.TryGetValue(fullPath, out var entry))
        {
            entry.Count++;
            entry.LastUsedUtc = DateTime.UtcNow;
        }
        else
        {
            _entries[fullPath] = new UsageEntry { Count = 1, LastUsedUtc = DateTime.UtcNow };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
    }

    public int GetCount(string fullPath) =>
        _entries.TryGetValue(fullPath, out var entry) ? entry.Count : 0;

    public DateTime GetLastUsedUtc(string fullPath) =>
        _entries.TryGetValue(fullPath, out var entry) ? entry.LastUsedUtc : DateTime.MinValue;
}
