using System.Diagnostics;

namespace CheckGithub.Services;

public static class ShellHelper
{
    /// <summary>
    /// 打开系统窗口/浏览器。失败只返回 false，绝不向调用方的界面线程抛异常。
    /// </summary>
    public static bool OpenDirectory(string path)
    {
        if (!System.IO.Directory.Exists(path))
            return false;

        return TryStart(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { path },
            UseShellExecute = true,
        });
    }

    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return TryStart(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private static bool TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }
}
