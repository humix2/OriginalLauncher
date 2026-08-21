using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
    private IReadOnlyList<IndexedEntry> _index;
    private DateTime _shownAtUtc;

    // "//" や "/a" のようなプレフィックスちょうどを入力すると、この待機状態に入る。
    // 入力欄はクリアされ、以降の入力はこのショートカットへの検索語として扱われる。
    private SearchShortcutConfig? _armedShortcut;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public bool AllowClose { get; set; }

    public MainWindow() : this(null, new LauncherConfig(), new UsageStore(UsageStore.DefaultPath), null)
    {
    }

    public MainWindow(MigemoService? migemo, LauncherConfig config, UsageStore usage, Action? openSettings)
    {
        InitializeComponent();
        _migemo = migemo;
        _config = config;
        _usage = usage;
        _openSettings = openSettings;
        _index = new FileIndexer(_config).BuildIndex();

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
        _foregroundWatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _foregroundWatcher.Tick += (_, _) =>
        {
            if (IsVisible
                && DateTime.UtcNow - _shownAtUtc > ForegroundGracePeriod
                && GetForegroundWindow() != new WindowInteropHelper(this).Handle)
            {
                HidePopup();
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
        if (string.IsNullOrWhiteSpace(query) || _migemo is null)
        {
            return;
        }

        var patternText = _migemo.GetRegexPattern(query);
        if (string.IsNullOrEmpty(patternText))
        {
            return;
        }

        var regex = new Regex(patternText, RegexOptions.IgnoreCase);
        var matches = new List<IndexedEntry>();
        foreach (var entry in _index)
        {
            if (regex.IsMatch(entry.DisplayName))
            {
                matches.Add(entry);
            }
        }

        // 表示名+種別が同じもの（別ルートに同名のショートカット/フォルダがあるケース）は
        // グループ内で最も使われている/最近使ったものを代表として1件にまとめる。
        // 使用回数が多いもの、同数なら最近使ったものを優先する。未使用のものは既存の走査順のまま後ろに残る。
        var ranked = matches
            .GroupBy(entry => (Name: entry.DisplayName.ToLowerInvariant(), entry.Kind))
            .Select(group => group
                .OrderByDescending(entry => _usage.GetCount(entry.FullPath))
                .ThenByDescending(entry => _usage.GetLastUsedUtc(entry.FullPath))
                .First())
            .OrderByDescending(entry => _usage.GetCount(entry.FullPath))
            .ThenByDescending(entry => _usage.GetLastUsedUtc(entry.FullPath))
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
        _index = new FileIndexer(_config).BuildIndex();
        QueryBox.Clear();
    }

    /// <summary>
    /// 設定画面での保存後に呼ばれる。インデックスを設定内容で作り直す。
    /// 検索ショートカットは照合のたびに _config.SearchShortcuts を直接見るため再登録は不要。
    /// ホットキーの再設定は App 側の責務。
    /// </summary>
    public void ReloadFromConfig()
    {
        _index = new FileIndexer(_config).BuildIndex();
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
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            return;
        }

        _usage.RecordLaunch(item.FullPath);
        HidePopup();
    }
}
