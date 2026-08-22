using System.IO;
using System.Text.Json;

namespace OriginalLauncher;

/// <summary>
/// インデックス対象ルート・除外パターン・拡張子ホワイトリストの設定。
/// %AppData%\OriginalLauncher\config.json に保存し、なければ既定値で新規作成する。
/// </summary>
public sealed class LauncherConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<RootConfig> Roots { get; set; } = [];

    // VS 等が作る深いノイズフォルダを既定で除外する（設計メモの検討事項）。
    public List<string> ExcludeNames { get; set; } =
    [
        "node_modules", "obj", "bin", "packages", ".git", ".vs", ".idea", "bower_components", "dist", "$RECYCLE.BIN",
    ];

    // フォルダは常に対象。ファイルはここに列挙した拡張子のみを表示対象にする。
    public List<string> Extensions { get; set; } = [".exe", ".lnk"];

    // ポップアップの起動キー。既定は CapsLock（修飾キーなし）。
    public HotkeyConfig Hotkey { get; set; } = new();

    // 検索結果の最大表示件数。
    public int MaxResults { get; set; } = 20;

    // 検索結果一覧（表示名・種別タグ）のフォント。既定は BIZ UDPゴシック（Windows 10 1809+ 標準搭載）。
    // インストールされていないフォント名を指定した場合は WPF のフォントフォールバックに任せる。
    public string ResultFontFamily { get; set; } = "BIZ UDPゴシック";

    // "/m" → GoogleMap 検索、"/a" → Amazon 検索のような、プレフィックス+検索語をURLに載せて
    // ブラウザで開くショートカット。fenrir 時代の運用を踏襲する。
    public List<SearchShortcutConfig> SearchShortcuts { get; set; } =
    [
        new SearchShortcutConfig
        {
            Prefix = "//",
            UrlTemplate = "https://www.google.com/search?q={query}",
            Description = "ブラウザ検索 (Google)",
        },
    ];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OriginalLauncher", "config.json");

    public static LauncherConfig LoadOrCreateDefault(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions) ?? CreateDefault();
        }

        var config = CreateDefault();
        config.Save(path);
        return config;
    }

    private static LauncherConfig CreateDefault()
    {
        // 既定はデスクトップとスタートメニュー（exe/lnk が自然に集まる場所）のみ。
        // ドライブ全体などを索引したい場合は config.json に手動で root を追加する。
        return new LauncherConfig
        {
            Roots =
            [
                new RootConfig { Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), MaxDepth = 3 },
                new RootConfig { Path = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), MaxDepth = 5 },
                new RootConfig { Path = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), MaxDepth = 5 },
            ],
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

public sealed class RootConfig
{
    public string Path { get; set; } = "";
    public int MaxDepth { get; set; } = 4;

    // fenrir の "-" 除外指定に相当。この配下に前方一致するパスは走査しない。
    public List<string> Excludes { get; set; } = [];
}

public sealed class HotkeyConfig
{
    // "CapsLock" のほか、System.Windows.Input.Key の名前（例: "Space", "F13", "OemTilde"）を指定できる。
    public string Key { get; set; } = "CapsLock";

    // "Alt" / "Control" / "Shift" / "Windows" を組み合わせて指定する。既定は修飾キーなし。
    public List<string> Modifiers { get; set; } = [];
}

public sealed class SearchShortcutConfig
{
    // クエリ先頭に付けるプレフィックス（例: "/m", "/a", "//"）。プレフィックスちょうどを入力すると
    // 入力欄がクリアされ、続く文字列が検索語として {query} に埋め込まれる。
    public string Prefix { get; set; } = "";

    // {query} をエンコード済みの検索語で置き換えて開く URL。
    public string UrlTemplate { get; set; } = "";

    // 何のショートカットかを表す短い説明。プレフィックス入力後、入力欄の右側に表示される
    // （今どの機能を使っているかを意識させるため）。
    public string Description { get; set; } = "";
}
