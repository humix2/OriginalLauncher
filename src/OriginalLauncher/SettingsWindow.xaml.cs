using System.Collections.ObjectModel;
using System.Windows;

namespace OriginalLauncher;

/// <summary>
/// インデックス対象ルート・除外パターン用の DataGrid 編集ラッパー。
/// RootConfig.Excludes (List&lt;string&gt;) はそのままではセル編集できないため、
/// ";" 区切りのテキストとして編集し、保存時に変換する。
/// </summary>
public sealed class EditableRoot
{
    public string Path { get; set; } = "";
    public int MaxDepth { get; set; } = 4;
    public string ExcludesText { get; set; } = "";
}

/// <summary>
/// 起動キー・インデックス対象ルート・最大表示件数・検索ショートカットを編集する設定画面。
/// 保存すると同じ LauncherConfig インスタンスをその場で書き換えて config.json に保存し、
/// <paramref name="onSaved"/> で呼び出し元に変更適用（ホットキー再設定・インデックス再構築等）を委ねる。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly LauncherConfig _config;
    private readonly string _configPath;
    private readonly Action _onSaved;

    public ObservableCollection<EditableRoot> Roots { get; } = [];
    public ObservableCollection<SearchShortcutConfig> SearchShortcuts { get; } = [];

    public SettingsWindow(LauncherConfig config, string configPath, Action onSaved)
    {
        InitializeComponent();
        _config = config;
        _configPath = configPath;
        _onSaved = onSaved;
        DataContext = this;

        foreach (var root in _config.Roots)
        {
            Roots.Add(new EditableRoot
            {
                Path = root.Path,
                MaxDepth = root.MaxDepth,
                ExcludesText = string.Join(';', root.Excludes),
            });
        }

        foreach (var shortcut in _config.SearchShortcuts)
        {
            SearchShortcuts.Add(new SearchShortcutConfig
            {
                Prefix = shortcut.Prefix,
                UrlTemplate = shortcut.UrlTemplate,
                Description = shortcut.Description,
            });
        }

        HotkeyKeyBox.Text = _config.Hotkey.Key;
        ModAltCheck.IsChecked = _config.Hotkey.Modifiers.Contains("Alt", StringComparer.OrdinalIgnoreCase);
        ModControlCheck.IsChecked = _config.Hotkey.Modifiers.Contains("Control", StringComparer.OrdinalIgnoreCase);
        ModShiftCheck.IsChecked = _config.Hotkey.Modifiers.Contains("Shift", StringComparer.OrdinalIgnoreCase);
        ModWindowsCheck.IsChecked = _config.Hotkey.Modifiers.Contains("Windows", StringComparer.OrdinalIgnoreCase);
        MaxResultsBox.Text = _config.MaxResults.ToString();
    }

    private void AddRootButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            Roots.Add(new EditableRoot { Path = dialog.SelectedPath, MaxDepth = 4, ExcludesText = "" });
        }
    }

    private void RemoveRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootsGrid.SelectedItem is EditableRoot selected)
        {
            Roots.Remove(selected);
        }
    }

    private void AddShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        SearchShortcuts.Add(new SearchShortcutConfig
        {
            Prefix = "/",
            Description = "説明",
            UrlTemplate = "https://example.com/search?q={query}",
        });
    }

    private void RemoveShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutsGrid.SelectedItem is SearchShortcutConfig selected)
        {
            SearchShortcuts.Remove(selected);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        RootsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        RootsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        ShortcutsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        ShortcutsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        var hotkeyKey = HotkeyKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(hotkeyKey))
        {
            StatusText.Text = "起動キーを入力してください。";
            return;
        }

        if (!int.TryParse(MaxResultsBox.Text.Trim(), out var maxResults) || maxResults <= 0)
        {
            StatusText.Text = "最大表示件数は正の整数で入力してください。";
            return;
        }

        _config.Hotkey.Key = hotkeyKey;
        _config.Hotkey.Modifiers = BuildModifierList();
        _config.MaxResults = maxResults;

        _config.Roots = Roots
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new RootConfig
            {
                Path = r.Path.Trim(),
                MaxDepth = r.MaxDepth,
                Excludes = r.ExcludesText
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
            })
            .ToList();

        _config.SearchShortcuts = SearchShortcuts
            .Where(s => !string.IsNullOrWhiteSpace(s.Prefix) && !string.IsNullOrWhiteSpace(s.UrlTemplate))
            .ToList();

        _config.Save(_configPath);
        _onSaved();
        Close();
    }

    private List<string> BuildModifierList()
    {
        var modifiers = new List<string>();
        if (ModAltCheck.IsChecked == true) modifiers.Add("Alt");
        if (ModControlCheck.IsChecked == true) modifiers.Add("Control");
        if (ModShiftCheck.IsChecked == true) modifiers.Add("Shift");
        if (ModWindowsCheck.IsChecked == true) modifiers.Add("Windows");
        return modifiers;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
