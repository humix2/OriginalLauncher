using System.IO;

namespace OriginalLauncher;

public enum IndexedEntryKind
{
    Folder,
    Executable,
    Shortcut,
}

public sealed record IndexedEntry(string DisplayName, string FullPath, IndexedEntryKind Kind)
{
    public string Tag => Kind switch
    {
        IndexedEntryKind.Folder => "DIR",
        IndexedEntryKind.Executable => "EXE",
        IndexedEntryKind.Shortcut => "LNK",
        _ => "",
    };
}

/// <summary>
/// 設定済みルートを走査し、フォルダ・拡張子ホワイトリストに合致するファイルのみを収集する。
/// 既定除外名・ルート別除外パス・走査深度制限で VS 等が作る深いノイズフォルダを弾く。
/// </summary>
public sealed class FileIndexer(LauncherConfig config)
{
    public IReadOnlyList<IndexedEntry> BuildIndex()
    {
        var entries = new List<IndexedEntry>();
        foreach (var root in config.Roots)
        {
            if (Directory.Exists(root.Path))
            {
                Walk(root.Path, root, depth: 0, entries);
            }
        }

        return entries;
    }

    private void Walk(string directory, RootConfig root, int depth, List<IndexedEntry> entries)
    {
        if (depth > root.MaxDepth)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var childPath in children)
        {
            var name = Path.GetFileName(childPath);
            if (IsExcluded(childPath, name, root))
            {
                continue;
            }

            if (Directory.Exists(childPath))
            {
                entries.Add(new IndexedEntry(name, childPath, IndexedEntryKind.Folder));
                Walk(childPath, root, depth + 1, entries);
                continue;
            }

            var extension = Path.GetExtension(childPath);
            if (!config.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? IndexedEntryKind.Shortcut
                : IndexedEntryKind.Executable;
            entries.Add(new IndexedEntry(Path.GetFileNameWithoutExtension(childPath), childPath, kind));
        }
    }

    private bool IsExcluded(string fullPath, string name, RootConfig root)
    {
        if (config.ExcludeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var excluded in root.Excludes)
        {
            if (fullPath.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
