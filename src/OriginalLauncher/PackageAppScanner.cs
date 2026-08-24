using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;

namespace OriginalLauncher;

/// <summary>
/// インストール済み Store(MSIX) アプリを検出する。
///
/// Netflix・Prime Video のように Microsoft Store 経由でインストールされた PWA ラップアプリは、
/// Edge の「サイトをアプリとしてインストール」機能と違ってスタートメニューに実体の .lnk を作らない
/// （スタート画面の「すべてのアプリ」には仮想的に表示されるだけ）ため、ファイルシステムを走査する
/// FileIndexer では検出できない。そのため Steam 同様、専用のメタデータ経路（ここでは
/// PackageManager が返すパッケージ情報）から直接列挙する。
///
/// 起動は "shell:AppsFolder\<PackageFamilyName>!<AppId>" というシェル名前空間パスを
/// ShellExecute（Process.Start + UseShellExecute）に渡す、デスクトップアプリから UWP/MSIX アプリを
/// 起動する際の標準的な方法による。
/// </summary>
public static class PackageAppScanner
{
    private static readonly XNamespace UapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    public static IReadOnlyList<IndexedEntry> BuildIndex()
    {
        var entries = new List<IndexedEntry>();

        IEnumerable<Package> packages;
        try
        {
            packages = new PackageManager().FindPackagesForUser(string.Empty);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return entries;
        }

        foreach (var package in packages)
        {
            // フレームワーク（VCLibs 等のランタイム共有ライブラリ）・リソースパック・バンドルは
            // それ自体が起動可能なアプリではないため対象外。
            if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
            {
                continue;
            }

            entries.AddRange(BuildEntriesForPackage(package));
        }

        return entries;
    }

    private static IEnumerable<IndexedEntry> BuildEntriesForPackage(Package package)
    {
        string? installedPath;
        try
        {
            installedPath = package.InstalledLocation?.Path;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            installedPath = null;
        }

        if (string.IsNullOrEmpty(installedPath))
        {
            yield break;
        }

        IReadOnlyList<AppListEntry> appListEntries;
        try
        {
            appListEntries = package.GetAppListEntries();
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            yield break;
        }

        var iconPath = FindLogoPath(installedPath);

        foreach (var appListEntry in appListEntries)
        {
            var displayName = appListEntry.DisplayInfo?.DisplayName;
            var appUserModelId = appListEntry.AppUserModelId;
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(appUserModelId))
            {
                continue;
            }

            yield return new IndexedEntry(displayName, installedPath, IndexedEntryKind.StoreApp)
            {
                AppUserModelId = appUserModelId,
                IconOverridePath = iconPath,
            };
        }
    }

    // AppxManifest.xml の Square44x44Logo（アプリ一覧アイコン相当）を読み、実体のファイルを探す。
    // マニフェストに書かれたパスは拡張子前のベース名で、実ファイルは "*.scale-100.png" のように
    // スケール/ターゲットサイズのサフィックスが付いた形で存在するため、Steam のアイコン探索と同様
    // ベース名のワイルドカード一致で探す。scale-100 があれば優先し、無ければ最初に見つかったものを使う。
    private static string? FindLogoPath(string installedPath)
    {
        var manifestPath = Path.Combine(installedPath, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        string? logoRelative;
        try
        {
            var doc = XDocument.Load(manifestPath);
            logoRelative = (string?)doc.Descendants(UapNamespace + "VisualElements").FirstOrDefault()?.Attribute("Square44x44Logo");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(logoRelative))
        {
            return null;
        }

        var relativeDir = Path.GetDirectoryName(logoRelative);
        var dir = string.IsNullOrEmpty(relativeDir) ? installedPath : Path.Combine(installedPath, relativeDir);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(logoRelative);
        var extension = Path.GetExtension(logoRelative);

        try
        {
            return Directory.EnumerateFiles(dir, $"{baseName}*{extension}")
                .OrderByDescending(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
