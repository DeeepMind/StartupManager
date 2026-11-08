namespace StartupManager.Scanners;

using Microsoft.Win32;
using StartupManager.Models;

/// <summary>
/// 读取与写入 Windows「启动审批」（StartupApproved）状态。
///
/// 背景（这是本工具最容易出错的地方）：
///   任务管理器/设置里"禁用"某个启动项时，**不会删除**原注册表值或快捷方式，
///   而是在下面这些键里写入一条二进制标记：
///
///     HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run
///     HKCU\...\StartupApproved\Run32
///     HKCU\...\StartupApproved\StartupFolder
///     HKLM\...\StartupApproved\Run
///     HKLM\...\StartupApproved\Run32
///     HKLM\...\StartupApproved\StartupFolder
///
///   值的名称 = 启动项名称，值的数据是 12 字节二进制：
///
///     [0] 状态字节：0x02 = 已启用，0x03 = 已禁用
///     [1..3]   00 00 00
///     [4..11]  FILETIME（禁用时间），启用时为全 0
///
///   只读原位置而不读审批标记，就会把所有"已被任务管理器禁用"的项
///   全部误报为"已启用"。
/// </summary>
internal static class StartupApproval
{
    private const string BasePath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>审批键的分区（对应启动项来源）。</summary>
    public enum Kind
    {
        /// <summary>HKCU 注册表 Run（含 Run32 的 32 位视图）。</summary>
        RunHkcu,

        /// <summary>HKLM 注册表 Run（含 Run32 的 32 位视图）。</summary>
        RunHklm,

        /// <summary>当前用户启动文件夹。</summary>
        StartupFolderUser,

        /// <summary>公共启动文件夹。</summary>
        StartupFolderCommon,
    }

    /// <summary>读取某项的审批状态。返回 null 表示"没有审批记录"（默认视为启用）。</summary>
    public static bool? GetEnabled(Kind kind, string itemName)
    {
        foreach (var (hive, view, subKey) in EnumerateLocations(kind))
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(subKey, writable: false);
                if (key is null)
                {
                    continue;
                }

                if (key.GetValue(itemName) is byte[] data && data.Length >= 4)
                {
                    // 0x03 = 已禁用；其它值（0x02 或 0x06 等）视为启用
                    return data[0] != 0x03;
                }
            }
            catch
            {
                // 单个位置读取失败不影响判断
            }
        }

        return null;
    }

    /// <summary>
    /// 写入审批状态，等价于任务管理器里的"启用/禁用"。
    /// 返回 null 表示成功。
    /// </summary>
    public static string? SetEnabled(Kind kind, string itemName, bool enabled)
    {
        var locations = EnumerateLocations(kind).ToList();
        if (locations.Count == 0)
        {
            return "内部错误：未知的审批位置";
        }

        // 优先写已有的记录所在位置；若都没有，写到第一个（规范位置）
        foreach (var (hive, view, subKey) in locations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);

                bool existsHere;
                try
                {
                    using var ro = root.OpenSubKey(subKey, writable: false);
                    existsHere = ro?.GetValue(itemName) is byte[];
                }
                catch
                {
                    existsHere = false;
                }

                if (!existsHere && locations.IndexOf((hive, view, subKey)) != 0)
                {
                    // 该位置没有记录且不是首选位置，跳过
                    continue;
                }

                using var key = root.CreateSubKey(subKey, writable: true);
                if (key is null)
                {
                    return "无法创建审批键：" + subKey;
                }

                byte[] data = BuildValue(existsHere ? ReadExisting(root, subKey, itemName) : null, enabled);
                key.SetValue(itemName, data, RegistryValueKind.Binary);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "权限不足：修改该项的启动状态需要管理员权限";
            }
            catch (System.Security.SecurityException)
            {
                return "权限不足：修改该项的启动状态需要管理员权限";
            }
            catch (Exception ex)
            {
                return "写入启动审批失败：" + ex.Message;
            }
        }

        return "未找到可写入的审批位置";
    }

    private static byte[]? ReadExisting(RegistryKey root, string subKey, string itemName)
    {
        try
        {
            using var k = root.OpenSubKey(subKey, false);
            return k?.GetValue(itemName) as byte[];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 构造 12 字节审批数据。
    /// 启用：首字节 0x02，时间部分全 0。
    /// 禁用：首字节 0x03，时间部分写当前 FILETIME（与 Explorer 行为一致）。
    /// </summary>
    private static byte[] BuildValue(byte[]? existing, bool enabled)
    {
        var data = new byte[12];

        // 保留原有的非状态字节（尽量不破坏 Explorer 写入的其它信息）
        if (existing is not null && existing.Length == data.Length)
        {
            Array.Copy(existing, data, data.Length);
        }

        data[0] = (byte)(enabled ? 0x02 : 0x03);
        data[1] = 0x00;
        data[2] = 0x00;
        data[3] = 0x00;

        if (enabled)
        {
            for (int i = 4; i < 12; i++)
            {
                data[i] = 0x00;
            }
        }
        else
        {
            long ft = DateTime.UtcNow.ToFileTimeUtc();
            BitConverter.GetBytes(ft).CopyTo(data, 4);
        }

        return data;
    }

    /// <summary>枚举某分区可能存在的审批位置（hive + 视图 + 子键）。</summary>
    private static IEnumerable<(RegistryHive Hive, RegistryView View, string SubKey)> EnumerateLocations(Kind kind)
    {
        switch (kind)
        {
            case Kind.RunHkcu:
                yield return (RegistryHive.CurrentUser, RegistryView.Registry64, $@"{BasePath}\Run");
                yield return (RegistryHive.CurrentUser, RegistryView.Registry32, $@"{BasePath}\Run32");
                break;

            case Kind.RunHklm:
                yield return (RegistryHive.LocalMachine, RegistryView.Registry64, $@"{BasePath}\Run");
                yield return (RegistryHive.LocalMachine, RegistryView.Registry32, $@"{BasePath}\Run32");
                break;

            case Kind.StartupFolderUser:
                yield return (RegistryHive.CurrentUser, RegistryView.Default, $@"{BasePath}\StartupFolder");
                break;

            case Kind.StartupFolderCommon:
                yield return (RegistryHive.LocalMachine, RegistryView.Default, $@"{BasePath}\StartupFolder");
                break;
        }
    }
}
