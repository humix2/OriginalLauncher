using System.ComponentModel;
using System.Windows.Media;

namespace OriginalLauncher;

/// <summary>
/// 検索結果リストの表示用ラッパー。アイコン取得（SHGetFileInfo）は同期だと毎回のタイプ入力を
/// 引っかからせる原因になるため、バックグラウンドで取得して非同期に反映する。
/// </summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private readonly IndexedEntry _entry;
    private ImageSource? _icon;

    public string DisplayName => _entry.DisplayName;
    public string Tag => _entry.Tag;
    public string FullPath => _entry.FullPath;

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ResultItem(IndexedEntry entry)
    {
        _entry = entry;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        Icon = await Task.Run(() => IconProvider.GetIcon(_entry));
    }
}
