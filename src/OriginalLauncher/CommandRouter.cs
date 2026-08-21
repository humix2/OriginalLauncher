namespace OriginalLauncher;

/// <summary>
/// クエリが引数なしの特殊コマンド（例: "/s", "/o"）と完全一致したら実行する。
/// 検索ショートカット（"//" 等、引数＝検索語を伴うもの）は別途 MainWindow が
/// 「入力欄クリア＋待機状態」として扱うため、ここでは扱わない。
/// 1 打鍵ごとに照合される（Enter を待たず、一致した瞬間に即実行される）。
/// </summary>
public sealed class CommandRouter
{
    private readonly List<(string Command, Action Execute)> _entries = [];

    public void RegisterExact(string command, Action execute)
    {
        _entries.Add((command, execute));
    }

    /// <summary>
    /// 登録済みのコマンドをすべて削除する。設定変更後の再登録に使う。
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// クエリがいずれかのコマンドに完全一致すれば実行して true を返す。一致しなければ何もせず false。
    /// </summary>
    public bool TryExecute(string query)
    {
        foreach (var (command, execute) in _entries)
        {
            if (query == command)
            {
                execute();
                return true;
            }
        }

        return false;
    }
}
