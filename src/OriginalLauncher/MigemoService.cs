using System.IO;
using System.Runtime.InteropServices;

namespace OriginalLauncher;

/// <summary>
/// cmigemo (migemo.dll) を FFI で叩き、ローマ字/かな混じりのクエリを検索用の正規表現パターンに変換する。
/// クエリ・生成パターンは UTF-8 でやり取りする（dict\migemo は UTF-8 版辞書）。
/// </summary>
public sealed class MigemoService : IDisposable
{
    private const string NativeLibrary = "migemo";

    [DllImport(NativeLibrary, CharSet = CharSet.Ansi)]
    private static extern IntPtr migemo_open(string dict);

    [DllImport(NativeLibrary)]
    private static extern void migemo_close(IntPtr migemo);

    [DllImport(NativeLibrary)]
    private static extern IntPtr migemo_query(IntPtr migemo, IntPtr query);

    [DllImport(NativeLibrary)]
    private static extern void migemo_release(IntPtr migemo, IntPtr pattern);

    [DllImport(NativeLibrary)]
    private static extern int migemo_is_enable(IntPtr migemo);

    private readonly IntPtr _handle;
    private bool _disposed;

    public bool IsEnabled { get; }

    public MigemoService(string dictPath)
    {
        if (!File.Exists(dictPath))
        {
            throw new FileNotFoundException("migemo dictionary not found.", dictPath);
        }

        _handle = migemo_open(dictPath);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("migemo_open failed to initialize migemo.");
        }

        IsEnabled = migemo_is_enable(_handle) != 0;
    }

    /// <summary>
    /// クエリを migemo にかけて、あいまい一致用の正規表現パターン文字列を生成する。
    /// </summary>
    public string GetRegexPattern(string query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var queryPtr = Marshal.StringToCoTaskMemUTF8(query);
        try
        {
            var patternPtr = migemo_query(_handle, queryPtr);
            if (patternPtr == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUTF8(patternPtr) ?? string.Empty;
            }
            finally
            {
                migemo_release(_handle, patternPtr);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(queryPtr);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        migemo_close(_handle);
    }
}
