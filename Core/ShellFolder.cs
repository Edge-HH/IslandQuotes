using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IslandQuotes.Core;

/// <summary>
/// 通过 Win32 ShellExecuteW 在系统文件管理器中打开目录。
/// 动词固定为 open、所有参数显式传递，不经过 shell 命令行拼接，也不启动中间 shell 进程；
/// 返回值大于 32 视为成功，否则按 Win32 错误抛出。
/// </summary>
public static class ShellFolder
{
    private const int SwShownormal = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string? verb,
        string file,
        string? parameters,
        string? directory,
        int nShowCmd);

    /// <summary>在系统文件管理器中打开指定目录；失败时抛出 <see cref="Win32Exception"/>。</summary>
    public static void OpenInFileManager(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        IntPtr result = ShellExecuteW(IntPtr.Zero, "open", directory, null, null, SwShownormal);
        if (result.ToInt64() <= 32)
        {
            throw new Win32Exception($"无法打开目录（ShellExecuteW 返回 {result.ToInt64()}）。");
        }
    }
}
