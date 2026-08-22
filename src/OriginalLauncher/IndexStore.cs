using System.IO;
using System.Text.Json;

namespace OriginalLauncher;

/// <summary>
/// ファイル走査結果 (IndexedEntry の一覧) を %AppData%\OriginalLauncher\index.json に永続化する。
/// 起動のたびにドライブ全体を走査し直すと重いため、キャッシュがあれば次回起動時はそこから読み込むだけにする。
/// </summary>
public static class IndexStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OriginalLauncher",
        "index.json");

    public static IReadOnlyList<IndexedEntry>? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<IndexedEntry>>(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, IReadOnlyList<IndexedEntry> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries);
        File.WriteAllText(path, json);
    }
}
