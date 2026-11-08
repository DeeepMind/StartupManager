namespace StartupManager.Scanners;

using System.Diagnostics;
using System.Text;
using StartupManager.Models;

/// <summary>
/// 扫描"永久性 WMI 事件订阅"—— 一种非常隐蔽的持久化机制。
///
/// 原理：
///   WMI 支持"当某事件发生时执行某命令"的订阅，且订阅可以持久化到
///   root\subscription 命名空间，系统重启后依然生效。
///   因为不需要注册表 Run、不需要计划任务，绝大多数启动项管理工具都看不到它。
///
/// 为什么会存在：
///   · 合法的：部分企业软件、监控工具
///   · 恶意的：恶意软件常用它做持久化（这也是杀软重点监控的位置）
///
/// 读取要求：
///   root\subscription 命名空间默认只有管理员可读。
///   非管理员运行时会在 Warnings 里提示，而不是报错。
///
/// 安全设计：
///   完全只读。CanToggle = false。
///   删除 WMI 订阅需要精确操作，误删可能影响系统，因此只展示并提示。
/// </summary>
public sealed class WmiSubscriptionScanner : IStartupScanner
{
    public IEnumerable<StartupSource> Sources => new[] { StartupSource.Other };

    public bool RequiresElevation => true;

    public string LastError { get; private set; } = "";

    private const string QueryScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$out = New-Object System.Collections.ArrayList

# 1) 事件消费者：真正执行动作的部分
Get-CimInstance -Namespace root\subscription -ClassName CommandLineEventConsumer -ErrorAction SilentlyContinue |
  ForEach-Object {
    [void]$out.Add([pscustomobject]@{
        Kind    = 'CommandLineEventConsumer'
        Name    = $_.Name
        Detail  = $_.CommandLineTemplate
    })
}

Get-CimInstance -Namespace root\subscription -ClassName ActiveScriptEventConsumer -ErrorAction SilentlyContinue |
  ForEach-Object {
    [void]$out.Add([pscustomobject]@{
        Kind    = 'ActiveScriptEventConsumer'
        Name    = $_.Name
        Detail  = $_.ScriptText
    })
}

# 2) 过滤器：什么时候触发
Get-CimInstance -Namespace root\subscription -ClassName __EventFilter -ErrorAction SilentlyContinue |
  ForEach-Object {
    [void]$out.Add([pscustomobject]@{
        Kind    = 'EventFilter'
        Name    = $_.Name
        Detail  = $_.Query
    })
}

# 3) 绑定关系
Get-CimInstance -Namespace root\subscription -ClassName __FilterToConsumerBinding -ErrorAction SilentlyContinue |
  ForEach-Object {
    [void]$out.Add([pscustomobject]@{
        Kind    = 'Binding'
        Name    = [string]$_.Filter
        Detail  = [string]$_.Consumer
    })
}

$out | ConvertTo-Json -Compress -Depth 4
";

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        string json = PowerShellRunner.Run(QueryScript);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            // WMI 订阅本就可能为空（无此类订阅），仅在出错时记录
            if (!string.IsNullOrEmpty(PowerShellRunner.LastError))
            {
                LastError = PowerShellRunner.LastError;
            }
            return items;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var elements = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<System.Text.Json.JsonElement> { root };

            foreach (var el in elements)
            {
                string kind = GetStr(el, "Kind");
                string name = GetStr(el, "Name");
                string detail = GetStr(el, "Detail");

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // 只把"消费者"（真正执行动作的）当作启动项展示；
                // 过滤器与绑定属于配套结构，单独列出会干扰阅读。
                if (kind is "EventFilter" or "Binding")
                {
                    continue;
                }

                string? exe = ScanUtil.ExtractExecutablePath(detail);

                items.Add(new StartupItem
                {
                    Name = name,
                    Source = StartupSource.Other,
                    Command = detail,
                    ExecutablePath = exe,
                    Exists = string.IsNullOrEmpty(exe) || File.Exists(exe),
                    Enabled = true,
                    Publisher = ScanUtil.GetPublisher(exe),
                    IsSystem = false,
                    Locator = $@"root\subscription:{kind}:{name}",
                    CanToggle = false,
                    Note = "WMI 事件订阅（只读）",
                });
            }
        }
        catch
        {
            // 解析失败忽略
        }

        return items;
    }

    private static string GetStr(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";


    public string? SetEnabled(StartupItem item, bool enabled) =>
        "WMI 事件订阅为保证安全仅支持查看。\n" +
        "如需移除，请在管理员 PowerShell 中执行：\n" +
        $"  Get-CimInstance -Namespace root\\subscription -ClassName {item.Locator.Split(':')[1]} " +
        $"-Filter \"Name='{item.Locator.Split(':')[2]}'\" | Remove-CimInstance";
}
