using System.IO;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace OriginalLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _appIcon;
    private GlobalHotkey? _hotkey;
    private MainWindow? _mainWindow;
    private MigemoService? _migemoService;
    private LauncherConfig? _config;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _migemoService = TryCreateMigemoService();

        _config = LauncherConfig.LoadOrCreateDefault(LauncherConfig.DefaultPath);

        // インデックス読み込み中の通知 (ShowNotice) がバルーンチップを使うため、
        // MainWindow より先にトレイアイコンを用意しておく。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _appIcon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "OriginalLauncher",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => TogglePopup();

        var usage = new UsageStore(UsageStore.DefaultPath);
        _mainWindow = new MainWindow(_migemoService, _config, usage, OpenSettings, ShowNotice);

        InstallHotkey();
    }

    private void ShowNotice(string message)
    {
        _notifyIcon?.ShowBalloonTip(4000, "OriginalLauncher", message, ToolTipIcon.Info);
    }

    private void InstallHotkey()
    {
        _hotkey = new GlobalHotkey(_config!.Hotkey);
        // フックのコールバック内で Show()/SetForegroundWindow 等の重い処理を同期実行すると、
        // Windows のメッセージポンプがネストしてしまい、遅れて届く KEYUP が再入的に配送されたり
        // Window の Activate/Deactivate が競合したりする不具合につながる。
        // Dispatcher に一度乗せ直し、フック呼び出しの外側で実行する。
        _hotkey.Triggered += () => Dispatcher.BeginInvoke(TogglePopup);
        _hotkey.Install();
    }

    private void OpenSettings()
    {
        if (_config is null)
        {
            return;
        }

        _mainWindow?.HidePopup();

        var settingsWindow = new SettingsWindow(_config, LauncherConfig.DefaultPath, () =>
        {
            _hotkey?.Dispose();
            InstallHotkey();
            _mainWindow?.ReloadFromConfig();
        });
        settingsWindow.ShowDialog();
    }

    private static MigemoService? TryCreateMigemoService()
    {
        var dictPath = Path.Combine(AppContext.BaseDirectory, "dict", "migemo", "migemo-dict");
        try
        {
            return new MigemoService(dictPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DllNotFoundException)
        {
            System.Windows.MessageBox.Show(
                $"migemo の初期化に失敗しました。あいまい検索は無効化されます。\n{ex.Message}",
                "OriginalLauncher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("設定...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        return menu;
    }

    private void TogglePopup()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible)
        {
            _mainWindow.HidePopup();
        }
        else
        {
            _mainWindow.ShowPopup();
        }
    }

    private void ExitApplication()
    {
        _hotkey?.Dispose();
        _migemoService?.Dispose();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _appIcon?.Dispose();

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        Shutdown();
    }
}
