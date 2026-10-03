using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OriginalLauncher;

/// <summary>
/// ボーダレス検索ポップアップ本体。表示/非表示はホットキー・フォーカス喪失・Escで制御し、
/// 実際の Close はアプリ終了時 (App.AllowClose) のみ許可する。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan ForegroundGracePeriod = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(60);

    private readonly MigemoService? _migemo;
    private readonly LauncherConfig _config;
    private readonly UsageStore _usage;
    private readonly CommandRouter _commands = new();
    private readonly DispatcherTimer _foregroundWatcher;
    private readonly DispatcherTimer _searchDebounce;
    private readonly Action? _openSettings;
    private readonly Action<string>? _showNotice;
    private IReadOnlyList<IndexedEntry> _index;
    private DateTime _shownAtUtc;

    // "//" や "/a" のようなプレフィックスちょうどを入力すると、この待機状態に入る。
    // 入力欄はクリアされ、以降の入力はこのショートカットへの検索語として扱われる。
    private SearchShortcutConfig? _armedShortcut;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    public bool AllowClose { get; set; }

    public MainWindow() : this(null, new LauncherConfig(), new UsageStore(UsageStore.DefaultPath), null)
    {
    }

    public MainWindow(MigemoService? migemo, LauncherConfig config, UsageStore usage, Action? openSettings, Action<string>? showNotice = null)
    {
        InitializeComponent();
        _migemo = migemo;
        _config = config;
        _usage = usage;
        _openSettings = openSettings;
        _showNotice = showNotice;
        _index = [];
        ApplyResultFont();
        _ = LoadIndexAsync();

        _commands.RegisterExact("/s", RebuildIndex);
        if (_openSettings is not null)
        {
            _commands.RegisterExact("/o", _openSettings);
        }

        // Window.Activated/Deactivated は ForceActivate による強制フォーカスと組み合わせると
        // WM_ACTIVATE が不安定になり信頼できないため使わない。GetForegroundWindow を
        // 定期的にポーリングし、実際に前面でなくなったら閉じる（OS の生の状態を直接見る）。
        // 表示直後は ForceActivate の前面化がまだ確定していないことがあるため、
        // 表示から一定時間は判定をスキップする猶予期間を設ける。
        // 前面ウィンドウのハンドルを MainWindow 自身の Handle と比較すると、右クリックの
        // コンテキストメニューのような「自プロセス内の別ウィンドウ」に前面が移った瞬間も
        // 「他アプリに切り替わった」と誤判定してポップアップごと閉じてしまう
        // （メニューが開いた直後に消える不具合の原因）。前面ウィンドウの所有プロセスIDで比較し、
        // 自プロセス内の別ウィンドウ（コンテキストメニュー等）は前面が移っても閉じないようにする。
        _foregroundWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _foregroundWatcher.Tick += (_, _) =>
        {
            if (IsVisible && DateTime.UtcNow - _shownAtUtc > ForegroundGracePeriod)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcessId);
                if (foregroundProcessId != CurrentProcessId)
                {
                    HidePopup();
                }
            }
        };

        // 1 打鍵ごとにフル検索（正規表現マッチ + 並べ替え + アイコン取得）を即実行すると
        // タイプ入力に引っかかりを感じるため、入力が少し止まってからまとめて検索する。
        _searchDebounce = new DispatcherTimer { Interval = SearchDebounceInterval };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RunSearch();
        };
    }

    public void ShowPopup()
    {
        DisarmShortcut();
        QueryBox.Clear();
        Show();
        WindowActivator.ForceActivate(this, () =>
        {
            Keyboard.Focus(QueryBox);
            QueryBox.Focus();

            // 直前にIMEをONにしていたアプリからホットキーで切り替えた場合、ONのまま
            // 引き継がれて検索欄がすぐに使えないことがあるため、表示のたびに強制的にOFFにする。
            System.Windows.Input.InputMethod.Current!.ImeState = System.Windows.Input.InputMethodState.Off;
        });
        _shownAtUtc = DateTime.UtcNow;
        _foregroundWatcher.Start();
    }

    public void HidePopup()
    {
        _foregroundWatcher.Stop();
        _searchDebounce.Stop();
        DisarmShortcut();
        QueryBox.Clear();

        // Hide() は直前に描画されたフレームをそのまま保持するため、Clear() の変更が実際に
        // 描画される前に隠すと、次回 Show() した瞬間に古い入力内容が一瞬フラッシュ表示される。
        // Render 優先度までディスパッチャのキューを処理させてから隠す。
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HidePopup();
        }

        base.OnClosing(e);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // TextBox は単一行でも上下矢印キーの既定処理（内部 TextEditor の行移動コマンド）で
        // 自ら e.Handled = true にしてしまうため、通常の KeyDown（バブル）では届かない。
        // TextBox の既定処理より先に実行される PreviewKeyDown（トンネル）で拾う。
        switch (e.Key)
        {
            case Key.Escape:
                if (_armedShortcut is not null)
                {
                    // 待機状態だけ解除してポップアップは閉じない（誤入力からの一段階の後戻り）。
                    DisarmShortcut();
                    QueryBox.Clear();
                }
                else
                {
                    HidePopup();
                }

                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_armedShortcut is not null)
                {
                    if (!string.IsNullOrWhiteSpace(QueryBox.Text))
                    {
                        OpenSearchShortcut(_armedShortcut, QueryBox.Text);
                    }
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    OpenContainingFolderForSelected();
                }
                else if (!_commands.TryExecute(QueryBox.Text))
                {
                    LaunchSelected();
                }

                e.Handled = true;
                break;
        }
    }

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        var text = QueryBox.Text;

        if (_armedShortcut is null)
        {
            // "/s" のような引数なしコマンドは、一致した瞬間に即実行する（Enter を待たない）。
            if (_commands.TryExecute(text))
            {
                return;
            }

            // "//" や "/a" のようなプレフィックスちょうどを入力したら、待機状態に入る。
            var shortcut = _config.SearchShortcuts.FirstOrDefault(s => s.Prefix == text);
            if (shortcut is not null)
            {
                ArmShortcut(shortcut);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ResultsList.Items.Clear();
            return;
        }

        if (_armedShortcut is not null)
        {
            // 待機状態では通常のあいまい検索は行わない。入力はショートカットへの検索語として貯める。
            ResultsList.Items.Clear();
            return;
        }

        _searchDebounce.Start();
    }

    private void ArmShortcut(SearchShortcutConfig shortcut)
    {
        _armedShortcut = shortcut;
        ModeHintText.Text = shortcut.Description;
        ModeHintBadge.Visibility = Visibility.Visible;
        ResultsList.Items.Clear();
        QueryBox.Clear();
    }

    private void DisarmShortcut()
    {
        _armedShortcut = null;
        ModeHintText.Text = "";
        ModeHintBadge.Visibility = Visibility.Collapsed;
    }

    private void RunSearch()
    {
        ResultsList.Items.Clear();

        var query = QueryBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var matches = new List<IndexedEntry>();

        // fenrir 由来の慣習: 先頭文字を大文字で打った時だけ migemo のかな/ローマ字あいまい展開を使う
        // （"A" → "あ" 等も候補に含む）。先頭が小文字なら migemo を経由せず、入力文字列そのままの
        // 部分一致で検索する（"a" は "あ" を含まない）。
        if (char.IsUpper(query[0]) && _migemo is not null)
        {
            var patternText = _migemo.GetRegexPattern(query.ToLowerInvariant());
            if (string.IsNullOrEmpty(patternText))
            {
                return;
            }

            // パターンには cmigemo 辞書の「炭素=C」「タングステン=W」のような1文字英字のエントリや、
            // 入力されたローマ字そのもの（"re"/"Re" 等）も常に候補として含まれる。前者は IgnoreCase を
            // 付けないことである程度抑えられるが、後者は大文字小文字を区別しても "RetroArch" のように
            // 先頭が "Re" な英字名まで拾ってしまう。大文字始まりの migemo モードは「かな/カナ/漢字への
            // あいまい変換結果」だけを対象にしたい（ローマ字そのままの部分一致は小文字入力の通常検索で
            // 拾えるため）ので、表示名に日本語文字（ひらがな/カタカナ/漢字）が含まれるものだけを残す。
            var regex = new Regex(patternText);
            foreach (var entry in _index)
            {
                if (regex.IsMatch(entry.DisplayName) && ContainsJapaneseCharacter(entry.DisplayName))
                {
                    matches.Add(entry);
                }
            }
        }
        else
        {
            foreach (var entry in _index)
            {
                if (entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(entry);
                }
            }
        }

        // 表示名+種別グループ（+Folderのみドライブ）が同じもの（別ルートに同名のショートカット/
        // フォルダがあるケース、同じアプリが実行ファイル・ショートカット・ストアアプリ等複数の形で
        // 登録されているケース）は、グループ内で最も使われている/最近使ったものを代表として1件にまとめる。
        // 使用回数が多いもの、同数なら最近使ったものを優先する。未使用のものは既存の走査順のまま後ろに残る。
        // 種別グループ (AppGroupKind) は、起動対象を表す種別 (Executable/Shortcut/SteamGame/StoreApp) を
        // 同じグループとして扱い、名前が同じなら統合する。Folder だけは別グループとし、たまたま名前が
        // 同じフォルダとアプリを誤って統合しないようにする。
        // Appグループではドライブを無視する。ショートカットの FullPath は .lnk 自体の置き場所であり
        // 実体（exe等）のドライブとは無関係なため、ドライブで絞ると「実体はDドライブ、ショートカットは
        // Cドライブのスタートメニュー」のような普通のケースで統合できなくなってしまう。
        // Folderグループでは従来通りドライブを含め、C:\ と D:\ に同名の別フォルダがある場合に
        // 誤って統合しないようにする。
        // 使用回数・最終使用日時が同率の場合は、実行ファイル (.exe) > ショートカット (.lnk) > フォルダ
        // の優先順位にする。基本的に実行対象を示した方が使いやすいため。
        var ranked = matches
            .GroupBy(entry => (
                Name: entry.DisplayName.ToLowerInvariant(),
                GroupKind: AppGroupKind(entry.Kind),
                Drive: AppGroupKind(entry.Kind) == AppGroupKind(IndexedEntryKind.Executable) ? null : Path.GetPathRoot(entry.FullPath)))
            .Select(group => group
                .OrderByDescending(entry => _usage.GetCount(entry.FullPath))
                .ThenByDescending(entry => _usage.GetLastUsedUtc(entry.FullPath))
                .ThenBy(entry => KindRank(entry.Kind))
                .First())
            .OrderByDescending(entry => _usage.GetCount(entry.FullPath))
            .ThenByDescending(entry => _usage.GetLastUsedUtc(entry.FullPath))
            .ThenBy(entry => KindRank(entry.Kind))
            .Take(_config.MaxResults);

        foreach (var entry in ranked)
        {
            ResultsList.Items.Add(new ResultItem(entry));
        }

        if (ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    // ひらがな・カタカナ（半角含む）・CJK統合漢字のいずれかを含むかどうか。
    private static bool ContainsJapaneseCharacter(string text)
    {
        foreach (var ch in text)
        {
            if ((ch >= '぀' && ch <= 'ヿ')
                || (ch >= 'ｦ' && ch <= 'ﾝ')
                || (ch >= '一' && ch <= '鿿'))
            {
                return true;
            }
        }

        return false;
    }

    private static int KindRank(IndexedEntryKind kind) => kind switch
    {
        IndexedEntryKind.Executable => 0,
        IndexedEntryKind.SteamGame => 0,
        IndexedEntryKind.StoreApp => 0,
        IndexedEntryKind.Shortcut => 1,
        IndexedEntryKind.Folder => 2,
        _ => 3,
    };

    // 名前が同じ場合に統合してよい種別グループ。起動対象を表す種別はまとめて1グループとし、
    // 同名なら KindRank に従って代表（実行ファイル/ストアアプリ > ショートカット）を残す。
    // Folder は無関係なアプリと誤って統合しないよう独立したグループにする。
    private static string AppGroupKind(IndexedEntryKind kind) => kind switch
    {
        IndexedEntryKind.Executable or IndexedEntryKind.Shortcut or IndexedEntryKind.SteamGame or IndexedEntryKind.StoreApp => "App",
        _ => kind.ToString(),
    };

    private void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        LaunchSelected();
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void RebuildIndex()
    {
        _index = BuildFullIndex();
        QueryBox.Clear();
    }

    /// <summary>
    /// 設定画面での保存後に呼ばれる。インデックスを設定内容で作り直す。
    /// 検索ショートカットは照合のたびに _config.SearchShortcuts を直接見るため再登録は不要。
    /// ホットキーの再設定は App 側の責務。
    /// </summary>
    public void ReloadFromConfig()
    {
        ApplyResultFont();
        _index = BuildFullIndex();
    }

    // 検索結果一覧の表示名・種別タグは FontFamily を明示指定せず ResultsList から継承させているため、
    // ここで一箇所だけ設定すれば両方に反映される。
    private void ApplyResultFont()
    {
        ResultsList.FontFamily = new System.Windows.Media.FontFamily(_config.ResultFontFamily);
    }

    /// <summary>
    /// ファイル/フォルダのインデックスと Steam のインストール済みゲーム・Store(MSIX) アプリを
    /// 合わせて作り直し、index.json（ファイル/フォルダ側のみ）を更新する。
    /// </summary>
    private IReadOnlyList<IndexedEntry> BuildFullIndex()
    {
        var fileEntries = new FileIndexer(_config).BuildIndex();
        IndexStore.Save(IndexStore.DefaultPath, fileEntries);
        return MergeWithExternalScanners(fileEntries);
    }

    /// <summary>
    /// キャッシュ (index.json) があれば読み込むだけで済ませ、起動のたびに全ドライブを
    /// 再走査しない。キャッシュが無い（初回起動、または index.json を消した場合）ときだけ
    /// バックグラウンドで実際の走査を行い、その間だけ通知を出す。
    /// Steam のゲーム・Store アプリの検出はレジストリ/PackageManager 経由の軽い読み取りだけのため、
    /// ファイル側のキャッシュ有無に関わらず毎回その場で（バックグラウンドスレッドで）読み直す。
    /// </summary>
    private async Task LoadIndexAsync()
    {
        var cached = IndexStore.TryLoad(IndexStore.DefaultPath);
        if (cached is not null)
        {
            _index = await Task.Run(() => MergeWithExternalScanners(cached));
            return;
        }

        _showNotice?.Invoke("初回起動のためインデックスを作成しています…（完了までしばらくお待ちください）");
        _index = await Task.Run(() =>
        {
            var fileEntries = new FileIndexer(_config).BuildIndex();
            IndexStore.Save(IndexStore.DefaultPath, fileEntries);
            return MergeWithExternalScanners(fileEntries);
        });
    }

    private static IReadOnlyList<IndexedEntry> MergeWithExternalScanners(IReadOnlyList<IndexedEntry> fileEntries)
    {
        var steamEntries = SteamLibraryScanner.BuildIndex();
        var storeEntries = PackageAppScanner.BuildIndex();
        if (steamEntries.Count == 0 && storeEntries.Count == 0)
        {
            return fileEntries;
        }

        return [.. fileEntries, .. steamEntries, .. storeEntries];
    }

    private void OpenSearchShortcut(SearchShortcutConfig shortcut, string query)
    {
        var url = shortcut.UrlTemplate.Replace("{query}", Uri.EscapeDataString(query));
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            return;
        }

        HidePopup();
    }

    private void LaunchSelected()
    {
        if (ResultsList.SelectedItem is not ResultItem item)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.LaunchTarget) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            return;
        }

        _usage.RecordLaunch(item.FullPath);
        HidePopup();
    }

    private void OpenContainingFolderForSelected()
    {
        if (ResultsList.SelectedItem is not ResultItem item)
        {
            return;
        }

        // /select, は「対象を含むフォルダを開いて対象自体をハイライトする」動作になるため、
        // フォルダ・ファイルどちらの種別でも同じ呼び出しで意図通りに機能する。
        // explorer.exe は CommandLineToArgvW 準拠の引数解釈をしないため、ArgumentList 経由だと
        // "/select,パス" 全体が丸ごとクォートされてしまい正しく認識されない（既定のフォルダにフォールバックする）。
        // "/select," の直後だけをクォートする生の Arguments 文字列として渡す必要がある。
        var psi = new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"/select,\"{item.FullPath}\"",
        };
        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception)
        {
            return;
        }

        HidePopup();
    }

    // ListBox.ContextMenu は項目単位ではなくリスト全体に1つだけ設定しているため、
    // 開く直前にマウス位置から対象の ListBoxItem をヒットテストして選択状態にする。
    // （ItemContainerStyle + PreviewMouseRightButtonDown で項目ごとに ContextMenu を
    // 持たせる方式も試したが、この透過/最前面のボーダレスウィンドウ上ではマウスの
    // キャプチャ/ルーティングと干渉するのか、右クリックしてもメニューが一切開かない
    // 事象が発生したため、ListBox 標準の ContextMenuOpening を使う方式に変更した。）
    private void ResultsList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var point = Mouse.GetPosition(ResultsList);
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(ResultsList, point);
        var container = hit is null ? null : FindAncestor<System.Windows.Controls.ListBoxItem>(hit.VisualHit);
        if (container is null)
        {
            e.Handled = true;
            return;
        }

        container.IsSelected = true;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LaunchSelected();
    }

    private void OpenFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenContainingFolderForSelected();
    }
}
