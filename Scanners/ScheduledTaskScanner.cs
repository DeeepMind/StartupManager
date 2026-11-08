namespace StartupManager.Scanners;

using System.Diagnostics;
using System.Text;
using StartupManager.Models;

/// <summary>
/// 扫描 Windows 计划任务中"会在开机/登录时自动启动"的项。
///
/// 为什么计划任务要单独处理：
///   大量国产软件、更新器（Edge/Adobe/NVIDIA）都用计划任务实现开机自启，
///   它们不出现在注册表 Run 里，普通用户完全看不到。
///
/// 过滤策略：
///   1. 跳过 \Microsoft\Windows\... 下的全部系统任务（任务计划程序自带，几百个）
///   2. 只保留触发器为"开机"或"登录"的任务
///   3. 其它触发器（如每天定时、空闲时）不算开机启动项
///
/// 启用/禁用机制：
///   使用 schtasks /change /enable|/disable，等价于任务计划程序里的"禁用"。
///   这是微软官方支持的方式，可逆，不删除任务。
/// </summary>
public sealed class ScheduledTaskScanner : IStartupScanner
{
    public IEnumerable<StartupSource> Sources => new[] { StartupSource.ScheduledTask };

    public bool RequiresElevation => false;   // 用户级任务无需提权，系统级任务已被过滤

    public string LastError { get; private set; } = "";

    /// <summary>PowerShell 查询脚本：一次取出任务名、路径、状态、触发器、动作。</summary>
    private const string QueryScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$out = New-Object System.Collections.ArrayList
Get-ScheduledTask | ForEach-Object {
    $t = $_
    # 跳过微软系统任务目录
    if ($t.TaskPath -like '\Microsoft\*') { return }

    $triggers = ($t.Triggers | ForEach-Object { $_.CimClass.CimClassName }) -join ','
    $actions  = ($t.Actions | ForEach-Object {
        $e = $_.Execute
        if ($_.Arguments) { $e + ' ' + $_.Arguments } else { $e }
    }) -join ' | '

    [void]$out.Add([pscustomobject]@{
        TaskName  = $t.TaskName
        TaskPath  = $t.TaskPath
        State     = [string]$t.State
        Triggers  = $triggers
        Actions   = $actions
    })
}
$out | ConvertTo-Json -Compress -Depth 4
";

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        string json = PowerShellRunner.Run(QueryScript);
        if (string.IsNullOrWhiteSpace(json))
        {
            LastError = string.IsNullOrEmpty(PowerShellRunner.LastError)
                ? "未获取到计划任务数据（可能权限不足）"
                : PowerShellRunner.LastError;
            return items;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 单个结果时 ConvertTo-Json 返回对象而非数组
            var elements = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<System.Text.Json.JsonElement> { root };

            foreach (var el in elements)
            {
                string taskName = GetStr(el, "TaskName");
                string taskPath = GetStr(el, "TaskPath");
                string state = GetStr(el, "State");
                string triggers = GetStr(el, "Triggers");
                string actions = GetStr(el, "Actions");

                // 只保留开机/登录触发的任务
                if (!IsStartupTrigger(triggers))
                {
                    continue;
                }

                string fullPath = taskPath + taskName;
                string? exe = ScanUtil.ExtractExecutablePath(actions);
                string publisher = ScanUtil.GetPublisher(exe);
                // 服务/脚本类任务无法解析出 exe 时，用动作文本判断
                bool isSystem = ScanUtil.IsSystemItem(exe, publisher, taskName)
                                || actions.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                                || actions.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase);

                items.Add(new StartupItem
                {
                    Name = taskName,
                    Source = StartupSource.ScheduledTask,
                    Command = actions,
                    ExecutablePath = exe,
                    Exists = string.IsNullOrEmpty(exe) || File.Exists(exe),
                    Enabled = !state.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                    Publisher = publisher,
                    IsSystem = isSystem,
                    Locator = fullPath,
                    CanToggle = true,
                    Note = TriggerText(triggers),
                });
            }
        }
        catch (Exception ex)
        {
            // JSON 解析失败时返回空列表，由上层记录
            _ = ex;
        }

        return items;
    }

    /// <summary>判断触发器列表里是否包含开机或登录触发。</summary>
    private static bool IsStartupTrigger(string triggers)
    {
        if (string.IsNullOrEmpty(triggers))
        {
            return false;
        }

        return triggers.Contains("BootTrigger", StringComparison.OrdinalIgnoreCase)
            || triggers.Contains("LogonTrigger", StringComparison.OrdinalIgnoreCase);
    }

    private static string TriggerText(string triggers)
    {
        var parts = new List<string>();
        if (triggers.Contains("BootTrigger", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("开机");
        }
        if (triggers.Contains("LogonTrigger", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("登录");
        }
        return parts.Count > 0 ? "触发：" + string.Join("/", parts) : "";
    }

    private static string GetStr(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";


    public string? SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            string flag = enabled ? "/ENABLE" : "/DISABLE";
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Change /TN \"{item.Locator}\" {flag}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return "无法启动 schtasks";
            }

            string err = p.StandardError.ReadToEnd();
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);

            if (p.ExitCode != 0)
            {
                if (err.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                    outp.Contains("拒绝访问", StringComparison.Ordinal) ||
                    err.Contains("拒绝访问", StringComparison.Ordinal))
                {
                    return "权限不足：该任务需要管理员权限，请以管理员身份重新运行";
                }

                string msg = string.IsNullOrWhiteSpace(err) ? outp : err;
                return "操作失败：" + msg.Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            return "操作失败：" + ex.Message;
        }
    }
}
