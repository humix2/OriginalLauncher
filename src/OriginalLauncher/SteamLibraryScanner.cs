using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OriginalLauncher;

/// <summary>
/// インストール済み Steam ゲームを検出する。
///
/// Steam はゲームをフォルダ走査で見つけるのではなく、以下のメタデータを読んで把握している:
/// 1. レジストリ (HKCU\Software\Valve\Steam の SteamPath) から Steam 本体のインストール先を特定
/// 2. "&lt;Steam&gt;\steamapps\libraryfolders.vdf" で、ゲームを分散インストールできる
///    ライブラリフォルダ（別ドライブ等）の一覧を取得
/// 3. 各ライブラリの "steamapps\appmanifest_&lt;appid&gt;.acf" が、インストール済み1タイトルごとの
///    メタデータ（appid・表示名・installdir）を持つ
///
/// "steam://rungameid/&lt;appid&gt;" はここで読んだ appid をそのまま埋め込んだ URI で、Steam が
/// インストール時に登録するプロトコルハンドラ (HKCR\steam) 経由で起動される。
///
/// VDF/ACF は Valve 独自のネスト key-value テキスト形式だが、ここで必要な appid/name/installdir は
/// いずれも appmanifest のトップレベル直下にフラットに並んでいるため、フルパーサーは実装せず
/// 正規表現で "key" "value" ペアを拾うだけで十分実用的に読み取れる。
/// </summary>
public static class SteamLibraryScanner
{
    private static readonly Regex KeyValueRegex = new("\"(?<key>[^\"]+)\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex LibraryPathRegex = new("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled);

    // 現行 Steam は "librarycache\<appid>\" 配下に header.jpg / library_600x900.jpg / logo.png 等の
    // 決まった名前のプロモーション画像と一緒に、40桁16進ハッシュ名の .jpg を1つだけ置いており、
    // それが実際のゲームアイコン（小さい正方形画像）。命名は appinfo.vdf（バイナリ形式）が持つ
    // ハッシュ値でここではパースしないため、拡張子とハッシュらしい名前の形だけで判別する。
    private static readonly Regex HashedIconFileRegex = new(@"^[0-9a-f]{20,}\.(jpg|jpeg|png|ico)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<IndexedEntry> BuildIndex()
    {
        var steamPath = FindSteamPath();
        if (steamPath is null)
        {
            return [];
        }

        var entries = new List<IndexedEntry>();
        foreach (var libraryPath in FindLibraryPaths(steamPath))
        {
            var steamAppsDir = Path.Combine(libraryPath, "steamapps");
            foreach (var manifestPath in EnumerateManifests(steamAppsDir))
            {
                var entry = TryReadManifest(manifestPath, steamPath, steamAppsDir);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static IEnumerable<string> EnumerateManifests(string steamAppsDir)
    {
        try
        {
            return Directory.Exists(steamAppsDir)
                ? Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf")
                : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static IndexedEntry? TryReadManifest(string manifestPath, string steamPath, string steamAppsDir)
    {
        string content;
        try
        {
            content = File.ReadAllText(manifestPath);
        }
        catch (IOException)
        {
            return null;
        }

        var values = ParseFlatKeyValues(content);
        if (!values.TryGetValue("appid", out var appId) ||
            !values.TryGetValue("name", out var name) ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var installDir = values.GetValueOrDefault("installdir");
        var fullPath = installDir is null ? steamAppsDir : Path.Combine(steamAppsDir, "common", installDir);

        return new IndexedEntry(name, fullPath, IndexedEntryKind.SteamGame)
        {
            SteamAppId = appId,
            IconOverridePath = FindGameIconPath(steamPath, appId),
        };
    }

    private static string? FindGameIconPath(string steamPath, string appId)
    {
        // 旧 Steam は "librarycache\<appid>_icon.jpg" というフラットな単一ファイルだった。
        // 残っていればそちらを優先する（現行フォルダ構成より単純で確実なため）。
        var legacyPath = Path.Combine(steamPath, "appcache", "librarycache", $"{appId}_icon.jpg");
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        var appCacheDir = Path.Combine(steamPath, "appcache", "librarycache", appId);
        if (!Directory.Exists(appCacheDir))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(appCacheDir)
                .FirstOrDefault(f => HashedIconFileRegex.IsMatch(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseFlatKeyValues(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in KeyValueRegex.Matches(content))
        {
            var key = match.Groups["key"].Value;
            // トップレベルの appid/name/installdir だけを狙っているが、正規表現はネスト無視で
            // 全件マッチするため、ネストしたセクション内に同名キーがあっても最初の出現（＝トップレベル）
            // を優先して上書きしない。
            result.TryAdd(key, match.Groups["value"].Value);
        }

        return result;
    }

    private static string? FindSteamPath()
    {
        var path =
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ??
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        path = path.Replace('/', '\\');
        return Directory.Exists(path) ? path : null;
    }

    private static List<string> FindLibraryPaths(string steamPath)
    {
        var paths = new List<string> { steamPath };
        var seen = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        string content;
        try
        {
            if (!File.Exists(vdfPath))
            {
                return paths;
            }

            content = File.ReadAllText(vdfPath);
        }
        catch (IOException)
        {
            return paths;
        }

        foreach (Match match in LibraryPathRegex.Matches(content))
        {
            var libraryPath = match.Groups["path"].Value.Replace("\\\\", "\\");
            if (Directory.Exists(libraryPath) && seen.Add(libraryPath))
            {
                paths.Add(libraryPath);
            }
        }

        return paths;
    }
}
