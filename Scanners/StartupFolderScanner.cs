namespace StartupManager.Scanners;

using StartupManager.Models;

/// <summary>
/// 扫描启动文件夹中的快捷方式与可执行文件。
///
/// 位置：
///   %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup          （当前用户）
///   %ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp       （所有用户）
///
/// 禁用机制：把文件移动到本程序管理的备份目录，恢复时移回。
/// 这样做可逆，且不会丢失快捷方式内的参数。
/// </summary>
public sealed class StartupFolderScanner : IStartupScanner
{
    public IEnumerable<StartupSource> Sources => new[]
    {
        StartupSource.StartupFolderUser,
        StartupSource.StartupFolderCommon,
    };

    public bool RequiresElevation => false;

    public string LastError { get; private set; } = "";

    /// <summary>备份目录：放在 %LOCALAPPDATA%\StartupManager\DisabledStartupFiles。</summary>
    private static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupManager", "DisabledStartupFiles");

    /// <summary>每个子系统记录原位置，恢复时据此还原。</summary>
    private static string IndexFile => Path.Combine(BackupDir, "_index.txt");

    private static IEnumerable<(string Path, StartupSource Source)> GetFolders()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrEmpty(user))
        {
            yield return (user, StartupSource.StartupFolderUser);
        }

        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        if (!string.IsNullOrEmpty(common))
        {
            yield return (common, StartupSource.StartupFolderCommon);
        }
    }

    public List<StartupItem> Scan()
    {
        var items = new List<StartupItem>();
        var disabled = LoadIndex();   // 文件名 -> 原目录

        foreach (var (folder, source) in GetFolders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(file);

                    // desktop.ini 是资源管理器用来标记文件夹的，不是启动项
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? target = ResolveShortcutTarget(file);
                    string publisher = ScanUtil.GetPublisher(target);
                    bool isSystem = ScanUtil.IsSystemItem(target, publisher, fileName);

                    // 任务管理器/设置里"禁用"启动文件夹项时不会删文件，
                    // 而是在 StartupApproved\StartupFolder 下写 0x03 标记。
                    StartupApproval.Kind kind = source == StartupSource.StartupFolderUser
                        ? StartupApproval.Kind.StartupFolderUser
                        : StartupApproval.Kind.StartupFolderCommon;
                    bool enabled = StartupApproval.GetEnabled(kind, fileName) ?? true;

                    items.Add(new StartupItem
                    {
                        Name = fileName,
                        Source = source,
                        Command = target ?? file,
                        ExecutablePath = target ?? file,
                        Exists = target is null ? File.Exists(file) : File.Exists(target),
                        Enabled = enabled,
                        Publisher = publisher,
                        IsSystem = isSystem,
                        Locator = file,
                        CanToggle = true,
                        Note = target is null && !file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                            ? "非快捷方式文件"
                            : "",
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 公共启动文件夹可能需要管理员权限
            }
            catch
            {
                // 忽略单个文件夹的枚举失败
            }
        }

        // 已禁用（被移到备份目录）的项也列出来，状态为"已禁用"
        try
        {
            if (Directory.Exists(BackupDir))
            {
                foreach (var file in Directory.EnumerateFiles(BackupDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith('_'))
                    {
                        continue;   // 跳过索引等元文件
                    }

                    if (!disabled.TryGetValue(fileName, out string? originalDir))
                    {
                        continue;
                    }

                    StartupSource source = originalDir.Equals(
                        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                        StringComparison.OrdinalIgnoreCase)
                        ? StartupSource.StartupFolderUser
                        : StartupSource.StartupFolderCommon;

                    items.Add(new StartupItem
                    {
                        Name = fileName,
                        Source = source,
                        Command = Path.Combine(originalDir, fileName),
                        ExecutablePath = null,
                        Exists = true,
                        Enabled = false,
                        IsSystem = false,
                        Locator = Path.Combine(originalDir, fileName),
                        CanToggle = true,
                        Note = "已禁用（文件在备份目录）",
                    });
                }
            }
        }
        catch
        {
            // 备份目录读取失败忽略
        }

        return items;
    }

    /// <summary>解析 .lnk 快捷方式指向的目标路径。非 .lnk 直接返回 null。</summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // 用 Shell COM 解析快捷方式，避免引入额外依赖
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            try
            {
                object? shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });

                if (shortcut is null)
                {
                    return null;
                }

                object? target = shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);

                return target as string;
            }
            finally
            {
                if (shell is IDisposable d)
                {
                    d.Dispose();
                }
                else
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> LoadIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(IndexFile))
            {
                return map;
            }

            foreach (var line in File.ReadAllLines(IndexFile))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0)
                {
                    map[line[..tab]] = line[(tab + 1)..];
                }
            }
        }
        catch
        {
            // 索引损坏则视为空
        }

        return map;
    }

    private static void SaveIndex(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllLines(IndexFile, map.Select(kv => $"{kv.Key}\t{kv.Value}"));
        }
        catch
        {
            // 写索引失败不影响文件移动本身
        }
    }

    public string? SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            string originalPath = item.Locator;
            string fileName = Path.GetFileName(originalPath);
            string originalDir = Path.GetDirectoryName(originalPath) ?? "";
            string backupPath = Path.Combine(BackupDir, fileName);

            // ── 首选：写 Windows 官方"启动审批"标记 ──
            // 与任务管理器行为一致：文件留在原地，只标记为禁用。
            // 这样不会丢失快捷方式的参数，也能与系统工具互操作。
            // 但注意：如果之前是本程序把文件移到了备份目录，则需要用文件移动恢复。
            bool isInBackup = !File.Exists(originalPath) && File.Exists(backupPath);

            if (!isInBackup)
            {
                StartupApproval.Kind kind = item.Source == StartupSource.StartupFolderUser
                    ? StartupApproval.Kind.StartupFolderUser
                    : StartupApproval.Kind.StartupFolderCommon;

                string? approvalErr = StartupApproval.SetEnabled(kind, fileName, enabled);
                if (approvalErr is null)
                {
                    return null;
                }

                if (approvalErr.Contains("权限不足", StringComparison.Ordinal))
                {
                    return approvalErr;
                }
                // 其它错误则回退到文件移动方案
            }

            if (!enabled)
            {
                if (!File.Exists(originalPath))
                {
                    return "文件已不存在：" + originalPath;
                }

                Directory.CreateDirectory(BackupDir);
                File.Move(originalPath, backupPath, overwrite: true);

                var idx = LoadIndex();
                idx[fileName] = originalDir;
                SaveIndex(idx);
                return null;
            }
            else
            {
                if (!File.Exists(backupPath))
                {
                    return "备份中找不到该文件：" + fileName;
                }

                Directory.CreateDirectory(originalDir);
                File.Move(backupPath, originalPath, overwrite: true);

                var idx = LoadIndex();
                idx.Remove(fileName);
                SaveIndex(idx);
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return "权限不足：公共启动文件夹需要管理员权限";
        }
        catch (System.Security.SecurityException)
        {
            return "权限不足：公共启动文件夹需要管理员权限";
        }
        catch (Exception ex)
        {
            return "操作失败：" + ex.Message;
        }
    }
}
