using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OriginalLauncher;

/// <summary>
/// SHGetFileInfo でファイル/フォルダの関連付けアイコンを取得し、WPF 表示用の ImageSource に変換する。
/// フォルダは共通の汎用アイコンを、exe/lnk はファイルごとに異なるためフルパスをキーにキャッシュする。
/// SHGetFileInfo はシェル API で複数スレッドから同時に叩くと不安定になることがあるため、
/// 検索結果ごとにバックグラウンドスレッドから並行して呼ばれても実際の取得処理は 1 件ずつ直列化する。
/// 失敗（null）はキャッシュしない — 一時的な失敗がそのパスで永久にアイコンなしになるのを防ぐ。
/// </summary>
public static class IconProvider
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const string FolderCacheKey = "\0folder";

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static ImageSource? GetIcon(IndexedEntry entry)
    {
        var isFolder = entry.Kind == IndexedEntryKind.Folder;
        var cacheKey = isFolder ? FolderCacheKey : entry.FullPath;

        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        Gate.Wait();
        try
        {
            if (Cache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            var icon = LoadIcon(entry, isFolder);
            if (icon is not null)
            {
                Cache[cacheKey] = icon;
            }

            return icon;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ImageSource? LoadIcon(IndexedEntry entry, bool isFolder)
    {
        // Steam ゲームは FullPath がインストール先フォルダ（実行ファイルではない）なので、
        // SHGetFileInfo に頼るとただのフォルダアイコンになってしまう。Steam のライブラリキャッシュに
        // 実際のゲームアイコン画像が見つかっていれば、そちらを直接読み込んで使う。
        if (entry.IconOverridePath is { } overridePath && File.Exists(overridePath))
        {
            var image = LoadImageFile(overridePath);
            if (image is not null)
            {
                return image;
            }
        }

        return LoadShellIcon(entry.FullPath, isFolder);
    }

    private static ImageSource? LoadImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 32;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ImageSource? LoadShellIcon(string path, bool isFolder)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_SMALLICON;
        uint attributes = 0;

        if (isFolder)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attributes = FILE_ATTRIBUTE_DIRECTORY;
        }

        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();
            return bitmapSource;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
