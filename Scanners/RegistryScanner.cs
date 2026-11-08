namespace StartupManager.Scanners;

using Microsoft.Win32;
using StartupManager.Models;

/// <summary>
/// 扫描注册表中的启动项。
///
/// 覆盖位置：
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run
///   HKCU\...\RunOnce
///   HKLM\Software\Microsoft\Windows\CurrentVersion\Run
///   HKLM\...\RunOnce
///   HKLM\Software\WOW6432Node\...\Run（32 位程序在 64 位系统上的视图）
///
/// 禁用机制（可逆，不删除原值）：
///   把原值移动到一个专用的备份键下，并在原位置留标记，
///   恢复时把值搬回原键。这与 msconfig/taskmgr 的做法一致且更透明。
/// </summary>
public sealed class RegistryScanner : IStartupScanner
{
    private const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion";

    /// <summary>被禁用项的统一存放位置（按原键路径分子键保存）。</summary>
    private const string BackupRoot = @"Software\StartupManager\Disabled";

    public IEnumerable<StartupSource> Sources => new[]
    {
        StartupSource.RegistryRunHkcu,
        StartupSource.RegistryRunHklm,
        StartupSource.RegistryRunOnce,
    };

    public bool RequiresElevation { get; private set; }

    public string LastError { get; private set; } = "";

    private static readonly (RegistryHive Hive, RegistryView View, StartupSource Source)[] Targets =
    {
        (RegistryHive.CurrentUser, RegistryView.Default, StartupSource.RegistryRunHkcu),
        (RegistryHive.LocalMachine, RegistryView.Registry64, StartupSource.RegistryRunHklm),
        (RegistryHive.LocalMachine, RegistryView.Registry32, StartupSource.RegistryRunHklm),
    };

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();

        foreach (var (hive, view, source) in Targets)
        {
            ScanKey(items, hive, view, $@"{BaseKey}\Run", source, runOnce: false);
            ScanKey(items, hive, view, $@"{BaseKey}\RunOnce", source, runOnce: true);
        }

        return items;
    }

    private void ScanKey(List<StartupItem> items, RegistryHive hive, RegistryView view,
                         string subKey, StartupSource source, bool runOnce)
    {
        RegistryKey? root = null;
        try
        {
            root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            // 读取"启动审批"状态：被用户禁用的项会被挪到 Disabled 子键
            var disabled = ReadDisabledSet(hive, view, subKey);

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                object? raw = key.GetValue(name);
                string command = raw as string ?? FormatNonString(raw);
                string? exe = ScanUtil.ExtractExecutablePath(command);
                string publisher = ScanUtil.GetPublisher(exe);
                bool exists = string.IsNullOrEmpty(exe) || File.Exists(exe);

                // 三重来源判断"是否启用"：
                // ① 本程序自己禁用过的（值被挪到备份键）
                // ② 任务管理器/设置里禁用的（StartupApproved 里的 0x03 标记）
                // ③ 都没有 → 启用
                bool enabled = !disabled.Contains(name);
                if (enabled && !runOnce)
                {
                    StartupApproval.Kind kind = hive == RegistryHive.LocalMachine
                        ? StartupApproval.Kind.RunHklm
                        : StartupApproval.Kind.RunHkcu;

                    // 任务管理器的审批标记优先级最高：它才是真正的"生效状态"
                    bool? approved = StartupApproval.GetEnabled(kind, name);
                    if (approved.HasValue)
                    {
                        enabled = approved.Value;
                    }
                }

                var item = new StartupItem
                {
                    Name = name,
                    Source = runOnce ? StartupSource.RegistryRunOnce : source,
                    Command = command,
                    ExecutablePath = exe,
                    Exists = exists,
                    Enabled = enabled,
                    Publisher = publisher,
                    IsSystem = ScanUtil.IsSystemItem(exe, publisher, name),
                    Locator = $"{HiveName(hive)}\\{subKey}\\{name}",
                    CanToggle = true,
                };

                // WOW6432Node 视图的特殊说明
                if (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine)
                {
                    item.Note = "32 位视图（WOW6432Node）";
                }

                if (ScanUtil.LooksLikeMsiEntry(command))
                {
                    item.Note = string.IsNullOrEmpty(item.Note) ? "MSI 安装程序项" : item.Note + "；MSI 安装程序项";
                }

                items.Add(item);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // HKLM 在某些策略下可能不可读，跳过
        }
        catch (Exception)
        {
            // 单个键读取失败不影响其它来源
        }
        finally
        {
            root?.Dispose();
        }
    }

    private static string FormatNonString(object? raw) => raw switch
    {
        null => "",
        byte[] bytes => System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
        _ => raw.ToString() ?? "",
    };

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        _ => hive.ToString(),
    };

    /// <summary>读取备份键中被禁用项的名称集合。</summary>
    private static HashSet<string> ReadDisabledSet(RegistryHive hive, RegistryView view, string subKey)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var backup = root.OpenSubKey($@"{BackupRoot}\{subKey.Replace('\\', '|')}", writable: false);
            if (backup is not null)
            {
                foreach (var n in backup.GetValueNames())
                {
                    set.Add(n);
                }
            }
        }
        catch
        {
            // 备份键不存在或不可读，返回空集合
        }

        return set;
    }

    public string? SetEnabled(StartupItem item, bool enabled)
    {
        // 从 Locator 反解出 hive / 子键 / 值名
        // Locator 形如：HKCU\Software\...\Run\ValueName
        var parts = item.Locator.Split('\\');
        if (parts.Length < 2)
        {
            return "无法解析注册表位置：" + item.Locator;
        }

        RegistryHive hive = parts[0] switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => RegistryHive.CurrentUser,
        };
        RegistryView view = item.Note.Contains("WOW6432Node") ? RegistryView.Registry32 : RegistryView.Default;
        if (hive == RegistryHive.LocalMachine && view == RegistryView.Default)
        {
            view = RegistryView.Registry64;
        }

        string valueName = parts[^1];
        string subKey = string.Join('\\', parts.Skip(1).Take(parts.Length - 2));

        // ── 首选方案：使用 Windows 官方"启动审批"标记（仅适用于 Run 键）──
        // 这与任务管理器的行为完全一致：不删除原值，只写一个 0x03/0x02 标记。
        // 好处：① 与系统工具互操作，任务管理器里看到的状态同步变化；
        //       ② 不破坏原启动项的参数，恢复时不会丢东西。
        //
        // 注意：RunOnce **没有**对应的启动审批键。
        // Windows 从未提供 StartupApproved\RunOnce（任务管理器也不允许禁用 RunOnce，
        // 因为它是"运行一次即自删"的语义）。
        // 早期代码用 EndsWith("\Run") || EndsWith("\RunOnce") 统一判断，
        // 导致 RunOnce 项被写进了 Run 的审批键，会误改同名 Run 项的状态。
        // 现在只有真正的 Run 键走审批，RunOnce 走下面的"移动值"回退方案。
        bool isPlainRunKey = subKey.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase);
        if (isPlainRunKey)
        {
            StartupApproval.Kind kind = hive == RegistryHive.LocalMachine
                ? StartupApproval.Kind.RunHklm
                : StartupApproval.Kind.RunHkcu;

            string? approvalErr = StartupApproval.SetEnabled(kind, valueName, enabled);
            if (approvalErr is null)
            {
                return null;
            }

            // 审批写入失败（如权限不足）时，只有在确实没权限的情况下才放弃；
            // 否则退回到"移动值"的旧方案，保证功能可用。
            if (approvalErr.Contains("权限不足", StringComparison.Ordinal))
            {
                return approvalErr;
            }
        }

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            string backupPath = $@"{BackupRoot}\{subKey.Replace('\\', '|')}";

            if (!enabled)
            {
                // ── 禁用：把值搬到备份键，然后删除原值 ──
                using var src = root.OpenSubKey(subKey, writable: true);
                if (src is null)
                {
                    return "注册表键不存在：" + subKey;
                }

                object? val = src.GetValue(valueName);
                object? kind = src.GetValueKind(valueName);

                if (val is null)
                {
                    return "启动项已不存在，可能已被其它程序删除";
                }

                using (var dst = root.CreateSubKey(backupPath, writable: true))
                {
                    dst.SetValue(valueName, val, (RegistryValueKind)kind);
                    dst.SetValue("__source", subKey, RegistryValueKind.String);
                }

                src.DeleteValue(valueName, throwOnMissingValue: false);
                return null;
            }
            else
            {
                // ── 启用：把值从备份键搬回原键 ──
                using var backup = root.OpenSubKey(backupPath, writable: true);
                if (backup is null)
                {
                    return "找不到该启动项的备份记录，无法恢复";
                }

                object? val = backup.GetValue(valueName);
                if (val is null)
                {
                    return "备份中缺少该项：" + valueName;
                }

                object? kind = backup.GetValueKind(valueName);
                using (var dst = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey))
                {
                    if (dst is null)
                    {
                        return "无法打开原注册表键：" + subKey;
                    }

                    dst.SetValue(valueName, val, (RegistryValueKind)kind);
                }

                backup.DeleteValue(valueName, throwOnMissingValue: false);
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return "权限不足：修改该启动项需要管理员权限";
        }
        catch (System.Security.SecurityException)
        {
            // .NET 在注册表权限被拒绝时抛的是 SecurityException，
            // 它并不是 UnauthorizedAccessException 的子类。
            // 之前只捕获前者，导致这类错误落进通用分支，
            // 界面上只显示英文原文，且无法触发"自动提权"流程。
            return "权限不足：修改该启动项需要管理员权限";
        }
        catch (Exception ex)
        {
            return "操作失败：" + ex.Message;
        }
    }
}
