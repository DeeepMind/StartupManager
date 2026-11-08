namespace StartupManager.Scanners;

using StartupManager.Models;

/// <summary>
/// 某一类启动来源的扫描器。
/// 每个实现负责：枚举该来源的启动项、判断是否系统项、以及启用/禁用。
/// </summary>
public interface IStartupScanner
{
    /// <summary>该扫描器覆盖的启动来源。</summary>
    IEnumerable<StartupSource> Sources { get; }

    /// <summary>扫描并返回启动项。实现应吞掉单个条目的异常，避免整批中断。</summary>
    List<StartupItem> Scan();

    /// <summary>启用或禁用某项。返回 null 表示成功，否则返回错误信息。</summary>
    string? SetEnabled(StartupItem item, bool enabled);

    /// <summary>该扫描器是否需要管理员权限才能执行修改操作。</summary>
    bool RequiresElevation { get; }

    /// <summary>最近一次 Scan() 的错误信息（为空表示正常）。</summary>
    string LastError { get; }
}
