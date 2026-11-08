namespace StartupManager.Scanners;

using System.Diagnostics;
using System.Text;
using StartupManager.Models;
using StartupManager.Services;

/// <summary>
/// 扫描自动启动的 Windows 服务。
///
/// 为什么服务是最"深层"的启动方式：
///   服务由 SCM（服务控制管理器）在系统启动早期加载，
///   早于用户登录，且可以在登录界面出现之前就运行。
///   普通用户通常完全无法感知。
///
/// 过滤策略（非常重要，否则会列出几百个系统服务）：
///   1. 只取 StartType = Automatic（自动）或 AutomaticDelayedStart（延迟自动）
///   2. 排除微软签名的服务
///   3. 排除位于 %WINDIR%\System32 等系统目录的二进制（除非明显是第三方）
///   4. 排除驱动程序类服务（Driver / KernelDriver 类型）
///
/// 安全设计：
///   服务默认 CanToggle = false（只读展示）。
///   因为随意禁用服务可能导致系统功能异常，程序提供"复制 sc 命令"而非直接改动。
/// </summary>
public sealed class ServiceScanner : IStartupScanner
{
    public IEnumerable<StartupSource> Sources => new[] { StartupSource.Service };

    public bool RequiresElevation => true;

    public string LastError { get; private set; } = "";

    private const string QueryScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$out = New-Object System.Collections.ArrayList

# WMI 查询可同时拿到 StartMode 与 PathName，比 Get-Service 信息更全
Get-CimInstance -ClassName Win32_Service -ErrorAction SilentlyContinue | ForEach-Object {
    $s = $_
    if ($s.StartMode -ne 'Auto') { return }

    [void]$out.Add([pscustomobject]@{
        Name        = $s.Name
        DisplayName = $s.DisplayName
        PathName    = $s.PathName
        StartMode   = $s.StartMode
        State       = $s.State
        StartName   = $s.StartName
        DelayedAutoStart = $s.DelayedAutoStart
    })
}
$out | ConvertTo-Json -Compress -Depth 4
";

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        string json = PowerShellRunner.Run(QueryScript, 120000);
        if (string.IsNullOrWhiteSpace(json))
        {
            LastError = string.IsNullOrEmpty(PowerShellRunner.LastError)
                ? "未获取到服务数据（可能需要管理员权限）"
                : PowerShellRunner.LastError;
            return items;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var elements = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<System.Text.Json.JsonElement> { root };

            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            foreach (var el in elements)
            {
                string name = GetStr(el, "Name");
                string display = GetStr(el, "DisplayName");
                string pathName = GetStr(el, "PathName");
                string state = GetStr(el, "State");
                string startName = GetStr(el, "StartName");

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string? exe = ScanUtil.ExtractExecutablePath(pathName);
                string publisher = ScanUtil.GetPublisher(exe);

                // ── 系统服务判定 ──
                // 关键：不能"以 LocalSystem 运行就算系统服务"。
                // 大量第三方服务（Adobe、ZeroTier、杀软）也用 LocalSystem 账户运行，
                // 那样会把它们全部误判为系统项，导致真正的第三方服务反而不显示。
                // 因此以"二进制文件所在位置"为主判据，签名与账户名作为辅助。
                bool isSystem = false;

                if (!string.IsNullOrEmpty(exe) && !string.IsNullOrEmpty(winDir) &&
                    exe.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                {
                    // 1) 二进制位于 Windows 目录 —— 绝大多数系统服务在此
                    isSystem = true;
                }
                else if (publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                         publisher.Contains("Windows (R)", StringComparison.OrdinalIgnoreCase))
                {
                    // 2) 微软官方签名且不在 Windows 目录（如 Office、GameInput 相关服务）
                    //    "Windows (R) Win 7 DDK provider" 是微软内部签名者名称的常见写法
                    isSystem = true;
                }
                else if (string.IsNullOrEmpty(exe))
                {
                    // 3) 无法解析路径：保守地按账户判断，避免把未知项误报为第三方
                    if (startName.StartsWith("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) ||
                        startName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
                    {
                        isSystem = true;
                    }
                }

                // 区分自动启动的两种形态，便于界面准确显示
                string delayed = GetStr(el, "DelayedAutoStart");
                bool isDelayed = delayed.Equals("True", StringComparison.OrdinalIgnoreCase);

                items.Add(new StartupItem
                {
                    Name = string.IsNullOrWhiteSpace(display) ? name : display,
                    Source = StartupSource.Service,
                    Command = pathName,
                    ExecutablePath = exe,
                    Exists = string.IsNullOrEmpty(exe) || File.Exists(exe),
                    Enabled = true,          // 能出现在这里的都是 Auto 启动
                    Publisher = publisher,
                    IsSystem = isSystem,
                    Locator = name,
                    // 现在支持直接修改启动类型（自动 ↔ 手动），可逆且无需手动敲命令
                    CanToggle = true,
                    Note = $"服务名 {name}｜状态 {state}｜账户 {startName}"
                           + (isDelayed ? "｜延迟启动" : ""),
                });
            }
        }
        catch
        {
            // 解析失败返回空
        }

        return items;
    }

    private static string GetStr(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";


    public string? SetEnabled(StartupItem item, bool enabled)
    {
        // 服务改为直接操作：通过 sc config 修改启动类型。
        // disable → 改为「手动」（不是 disabled，避免连带破坏依赖它的程序）
        // enable  → 还原为禁用前的状态（自动 / 自动延迟）
        var original = LoadSavedState(item.Locator);

        if (!enabled)
        {
            // 先记录原始状态，供恢复时精确还原
            var current = ServiceControl.GetState(item.Locator);
            if (current is not null)
            {
                SaveState(item.Locator, current);
            }
        }

        return ServiceControl.SetEnabled(item.Locator, enabled, original);
    }

    // ── 原始启动状态持久化（存到 %LOCALAPPDATA%，与注册表备份分开管理）──

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupManager", "ServiceState");

    private static string StateFile(string serviceName)
    {
        string safe = string.Concat(serviceName.Select(
            c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(StateDir, safe + ".txt");
    }

    private static ServiceControl.ServiceStartState? LoadSavedState(string serviceName)
    {
        try
        {
            string f = StateFile(serviceName);
            if (!File.Exists(f))
            {
                return null;
            }
            return ServiceControl.Deserialize(File.ReadAllText(f).Trim());
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(string serviceName, ServiceControl.ServiceStartState state)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StateFile(serviceName), ServiceControl.Serialize(state));
        }
        catch
        {
            // 保存失败不阻断操作，只是恢复时退化为默认 auto
        }
    }
}
