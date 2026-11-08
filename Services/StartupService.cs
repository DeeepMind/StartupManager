namespace StartupManager.Services;

using System.Diagnostics;
using StartupManager.Models;
using StartupManager.Scanners;

/// <summary>
/// 启动项扫描的统一入口：协调所有扫描器，聚合结果。
/// </summary>
public sealed class StartupService
{
    private readonly List<IStartupScanner> _scanners;

    public StartupService()
    {
        _scanners = new List<IStartupScanner>
        {
            new RegistryScanner(),
            new StartupFolderScanner(),
            new ScheduledTaskScanner(),
            new ServiceScanner(),
            new WmiSubscriptionScanner(),
        };
    }

    /// <summary>扫描全部来源。各来源之间互不影响，单个失败不中断整体。</summary>
    public ScanResult ScanAll(IProgress<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();

        foreach (var scanner in _scanners)
        {
            foreach (var src in scanner.Sources)
            {
                progress?.Report($"正在扫描：{SourceName(src)} …");
            }

            try
            {
                var found = scanner.Scan();
                result.Items.AddRange(found);

                // 扫描器可能"成功但返回空"，需把内部错误上报，避免静默漏掉整类启动项
                if (!string.IsNullOrEmpty(scanner.LastError))
                {
                    string srcName = string.Join("/", scanner.Sources.Select(SourceName));
                    result.Warnings.Add($"{srcName}：{scanner.LastError}");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{scanner.GetType().Name} 扫描失败：{ex.Message}");
            }
        }

        progress?.Report("正在整理结果 …");

        // 按"是否启用 → 来源 → 名称"排序，让禁用的项沉到下面
        result.Items.Sort((a, b) =>
        {
            int c = b.Enabled.CompareTo(a.Enabled);
            if (c != 0) return c;
            c = a.Source.CompareTo(b.Source);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        sw.Stop();
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>为某个启动项找到能处理它的扫描器并执行启用/禁用。</summary>
    public string? SetEnabled(StartupItem item, bool enabled)
    {
        var scanner = _scanners.FirstOrDefault(s => s.Sources.Contains(item.Source));
        if (scanner is null)
        {
            return $"没有可处理 {item.SourceText} 的扫描器";
        }

        if (!item.CanToggle)
        {
            return "该项为只读，本程序不直接修改";
        }

        // 不再用 IsAdministrator 做前置拦截：
        // 部分受限环境下该判断与真实可用权限不一致。
        // 直接尝试执行，失败后由界面层识别"权限不足"并走自动提权（UAC）。
        // 服务扫描器本身仍标记 RequiresElevation=true，
        // 仅用于界面提示"该操作可能需要管理员权限"。
        return scanner.SetEnabled(item, enabled);
    }

    private static string SourceName(StartupSource s) => s switch
    {
        StartupSource.RegistryRunHkcu => "注册表（当前用户）",
        StartupSource.RegistryRunHklm => "注册表（所有用户）",
        StartupSource.RegistryRunOnce => "注册表 RunOnce",
        StartupSource.StartupFolderUser => "用户启动文件夹",
        StartupSource.StartupFolderCommon => "公共启动文件夹",
        StartupSource.ScheduledTask => "计划任务",
        StartupSource.Service => "服务",
        StartupSource.Other => "WMI 订阅",
        _ => s.ToString(),
    };
}
