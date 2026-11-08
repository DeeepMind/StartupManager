namespace StartupManager.Models;

/// <summary>启动项的来源类型。</summary>
public enum StartupSource
{
    /// <summary>HKCU\...\Run —— 当前用户注册表启动项。</summary>
    RegistryRunHkcu,

    /// <summary>HKLM\...\Run —— 本机所有用户注册表启动项（32/64 位视图）。</summary>
    RegistryRunHklm,

    /// <summary>RunOnce —— 只执行一次，执行后自动删除。</summary>
    RegistryRunOnce,

    /// <summary>当前用户的启动文件夹。</summary>
    StartupFolderUser,

    /// <summary>所有用户的公共启动文件夹。</summary>
    StartupFolderCommon,

    /// <summary>Windows 计划任务。</summary>
    ScheduledTask,

    /// <summary>Windows 服务（自动启动）。</summary>
    Service,

    /// <summary>其他深层位置（WMI 事件订阅等）。</summary>
    Other,
}

/// <summary>
/// 一个启动项的统一抽象。
/// 不同来源的启用/禁用机制完全不同，因此具体操作由各自的 IScanner 实现。
/// </summary>
public sealed class StartupItem
{
    /// <summary>显示名称（注册表值名 / 任务名 / 服务名 / 快捷方式文件名）。</summary>
    public string Name { get; set; } = "";

    /// <summary>来源类型。</summary>
    public StartupSource Source { get; set; }

    /// <summary>人类可读的来源描述，例如 "注册表 (当前用户)"。</summary>
    public string SourceText => Source switch
    {
        StartupSource.RegistryRunHkcu => "注册表 · 当前用户",
        StartupSource.RegistryRunHklm => "注册表 · 所有用户",
        StartupSource.RegistryRunOnce => "注册表 · 仅一次",
        StartupSource.StartupFolderUser => "启动文件夹 · 当前用户",
        StartupSource.StartupFolderCommon => "启动文件夹 · 公共",
        StartupSource.ScheduledTask => "计划任务",
        StartupSource.Service => "服务",
        StartupSource.Other => "其他",
        _ => Source.ToString(),
    };

    /// <summary>启动项指向的目标：exe 路径、命令行、任务动作或服务二进制路径。</summary>
    public string Command { get; set; } = "";

    /// <summary>目标文件的实际路径（若可解析），用于判断文件是否存在。</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>目标文件是否真实存在。false 通常代表残留项。</summary>
    public bool Exists { get; set; } = true;

    /// <summary>是否处于启用状态。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>发布者/签名信息（若能获取）。</summary>
    public string Publisher { get; set; } = "";

    /// <summary>是否为 Windows/系统自带组件。系统项默认隐藏。</summary>
    public bool IsSystem { get; set; }

    /// <summary>定位用的原始标识：注册表完整键路径、任务完整路径、服务名或文件路径。</summary>
    public string Locator { get; set; } = "";

    /// <summary>补充说明（扫描时发现的问题、WOW64 视图等）。</summary>
    public string Note { get; set; } = "";

    /// <summary>是否允许在本程序中启用/禁用。部分来源（如深层 WMI）只读。</summary>
    public bool CanToggle { get; set; } = true;
}

/// <summary>扫描结果汇总。</summary>
public sealed class ScanResult
{
    public List<StartupItem> Items { get; } = new();

    /// <summary>扫描过程中的错误信息，用于界面提示。</summary>
    public List<string> Warnings { get; } = new();

    public TimeSpan Elapsed { get; set; }

    public int TotalCount => Items.Count;
    public int EnabledCount => Items.Count(i => i.Enabled);
    public int SystemCount => Items.Count(i => i.IsSystem);
    public int MissingCount => Items.Count(i => !i.Exists);
}
