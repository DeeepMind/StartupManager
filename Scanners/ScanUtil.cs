namespace StartupManager.Scanners;

using System.Diagnostics;
using System.Security.Principal;
using System.Text;

/// <summary>扫描器共用的工具方法。</summary>
public static class ScanUtil
{
    /// <summary>
    /// 从命令行中提取可执行文件路径。
    /// 需处理："C:\a b\x.exe" -arg、C:\a\x.exe /arg、rundll32.exe "x.dll",Entry 等形式。
    /// </summary>
    public static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();

        // 展开环境变量，便于后续 File.Exists 判断
        try
        {
            command = Environment.ExpandEnvironmentVariables(command);
        }
        catch
        {
            // 展开失败就按原样处理
        }

        // 情况 1：以引号开头 —— 取第一个引号内的内容
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : null;
        }

        // 情况 2：未加引号但含空格，可能是 "C:\Program Files\a\b.exe -x"
        // 从右往左找 ".exe"，再回头找空格，这样能正确处理路径含空格的情况。
        int exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            string candidate = command.Substring(0, exeIdx + 4);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 情况 3：第一个空格之前的部分即路径
        int firstSpace = command.IndexOf(' ');
        string first = firstSpace > 0 ? command.Substring(0, firstSpace) : command;

        // 去掉前缀参数（如 rundll32 之外的偶见 / 开头）
        return first.Trim('"');
    }

    /// <summary>
    /// 判断一个启动项是否属于 Windows 系统自带。
    /// 判据组合：路径在 System32/SysWOW64/Windows 目录下、或发布者为 Microsoft。
    /// </summary>
    public static bool IsSystemItem(string? exePath, string publisher, string name)
    {
        // 明确是微软签名 → 系统项
        if (!string.IsNullOrEmpty(publisher) &&
            (publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
             publisher.Contains("微软", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(exePath))
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            try
            {
                string full = Path.GetFullPath(exePath);

                // Windows 目录下的可执行文件基本可判定为系统组件
                if (!string.IsNullOrEmpty(win) && full.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(sys32) && full.StartsWith(sys32, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(sysWow) && full.StartsWith(sysWow, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 路径非法则忽略
            }
        }

        return false;
    }

    /// <summary>读取文件版本信息中的公司名，作为"发布者"。失败返回空串。</summary>
    public static string GetPublisher(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.CompanyName ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>判断当前进程是否以管理员身份运行。</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>MSI 安装的启动项通常是进 0/1 组成的路径，对其做特殊说明。</summary>
    public static bool LooksLikeMsiEntry(string command) =>
        command.Contains("Windows Installer", StringComparison.OrdinalIgnoreCase) ||
        command.Contains(".msi", StringComparison.OrdinalIgnoreCase);
}
