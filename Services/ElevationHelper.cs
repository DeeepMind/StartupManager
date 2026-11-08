namespace StartupManager.Services;

using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

/// <summary>
/// 需要管理员权限的操作，通过"提权重启自身"来完成。
///
/// 为什么不在程序启动时就要求提权：
///   绝大多数启动项（HKCU、用户启动文件夹、用户级计划任务）普通权限即可操作。
///   一上来就弹 UAC 会让读操作也变得麻烦，体验很差。
///
/// 策略：按需提权 —— 只有真正遇到"权限不足"时才重启自身，
/// 并携带"要执行什么操作"的参数，让新的提权实例自动完成它。
/// </summary>
public static class ElevationHelper
{
    /// <summary>命令行开关：表示本次是以提权方式被调用来执行单个操作的。</summary>
    public const string ElevateSwitch = "--elevated-op";

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重新启动本程序，并让它执行指定启动项的启用/禁用。
    /// 返回 null 表示已成功拉起提权进程；否则返回错误说明。
    /// </summary>
    public static string? RelaunchElevatedForOperation(StartupItemRef op)
    {
        try
        {
            string exe = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, "StartupManager.exe");

            string payload = JsonSerializer.Serialize(op);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                // 用参数传递，避免临时文件与转义问题
                Arguments = $"{ElevateSwitch} \"{payload.Replace("\"", "\\\"")}\"",
                UseShellExecute = true,      // 必须 true 才能触发 UAC
                Verb = "runas",              // 请求管理员权限
            };

            Process.Start(psi);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED，用户在 UAC 对话框中点了"否"
            return "已取消提权操作";
        }
        catch (Exception ex)
        {
            return "无法以管理员身份启动：" + ex.Message;
        }
    }

    /// <summary>判断某条错误信息是否属于"权限不足"。</summary>
    public static bool IsPermissionError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("权限不足", StringComparison.Ordinal)
            || message.Contains("管理员", StringComparison.Ordinal)
            || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("拒绝访问", StringComparison.Ordinal)
            || message.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase)
            || message.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
            // .NET 注册表权限被拒时抛 SecurityException，消息文本常为英文原文
            || message.Contains("SecurityException", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Requested registry access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("请求的注册表访问", StringComparison.Ordinal);
    }
}

/// <summary>提权时传给新进程的最小操作描述。</summary>
public sealed class StartupItemRef
{
    /// <summary>启动项来源枚举的整数值（避免跨进程的枚举序列化问题）。</summary>
    public int Source { get; set; }

    /// <summary>定位标识：注册表路径 / 文件路径 / 任务路径 / 服务名。</summary>
    public string Locator { get; set; } = "";

    /// <summary>备注（区分 WOW6432Node 视图等）。</summary>
    public string Note { get; set; } = "";

    /// <summary>目标值：true=启用，false=禁用。</summary>
    public bool Enable { get; set; }
}
