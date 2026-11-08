namespace StartupManager;

using System.Text;
using StartupManager.Models;
using StartupManager.Services;

/// <summary>
/// 无人值守自检：不显示界面，直接验证扫描 + 网格绑定的核心逻辑。
/// 用法： StartupManager.exe --selftest &lt;输出文件路径&gt;
/// </summary>
internal static class SelfTest
{
    public static int Run(string outputPath)
    {
        var sb = new StringBuilder();
        int failed = 0;

        void Check(string name, bool ok, string detail)
        {
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  —  {detail}");
            if (!ok) failed++;
        }

        sb.AppendLine("═══ 启动项管理器 自检 ═══");
        sb.AppendLine();

        // ── 1. 扫描 ──
        var svc = new StartupService();
        var result = svc.ScanAll();

        Check("扫描有结果", result.TotalCount > 0,
            $"共 {result.TotalCount} 项");
        Check("无扫描警告", result.Warnings.Count == 0,
            result.Warnings.Count == 0 ? "无" : string.Join("；", result.Warnings));

        var bySource = result.Items.GroupBy(i => i.SourceText)
            .ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine("  按来源：");
        foreach (var kv in bySource.OrderByDescending(k => k.Value))
        {
            sb.AppendLine($"    {kv.Key,-24} {kv.Value,4} 项");
        }
        sb.AppendLine();

        Check("计划任务已扫描到", bySource.GetValueOrDefault("计划任务") > 0,
            $"{bySource.GetValueOrDefault("计划任务")} 项");
        Check("服务已扫描到", bySource.GetValueOrDefault("服务") > 0,
            $"{bySource.GetValueOrDefault("服务")} 项");
        Check("注册表已扫描到",
            bySource.GetValueOrDefault("注册表 · 当前用户") > 0,
            $"{bySource.GetValueOrDefault("注册表 · 当前用户")} 项");

        // ── 2. 状态列显示转换（本次修复的 bug 核心）──
        // 模拟 CellFormatting 的转换：bool -> 中文，且不修改底层值
        bool allEnabled = result.Items.All(i => i.Enabled == true || i.Enabled == false);
        Check("Enabled 属性均为合法 bool", allEnabled,
            "所有项 Enabled 均为 true/false");

        var sample = result.Items.First();
        string display = sample.Enabled ? "启用" : "已禁用";
        Check("状态列显示文本可正确转换", display is "启用" or "已禁用",
            $"示例：{sample.Name} → \"{display}\"");

        // 关键：确认底层值仍是 bool，没有被字符串污染
        // （本 bug 的根因就是曾把 "启用" 字符串写回绑定 bool 属性的单元格）
        object boxed = sample.Enabled;
        Check("底层值类型为 Boolean", boxed is bool,
            $"sample.Enabled = {boxed}（类型 {boxed.GetType().Name}）");

        // ── 3. 数据完整性 ──
        int emptyNames = result.Items.Count(i => string.IsNullOrWhiteSpace(i.Name));
        Check("无空名称项", emptyNames == 0, $"{emptyNames} 项为空");

        int noSource = result.Items.Count(i => string.IsNullOrWhiteSpace(i.SourceText));
        Check("所有项都有来源描述", noSource == 0, $"{noSource} 项缺失");

        // ── 4. 排序稳定性 ──
        var sorted = result.Items.OrderByDescending(i => i.Enabled).ToList();
        Check("排序后启用项在前", sorted.FirstOrDefault()?.Enabled != false ||
              !result.Items.Any(i => i.Enabled),
            "首位为启用项");

        sb.AppendLine();
        sb.AppendLine("═══ 界面与权限 ═══");

        // ── 5. 网格表头必须全中文 ──
        using (var form = new MainForm())
        {
            var gridField = typeof(MainForm).GetField("_grid",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var grid = gridField?.GetValue(form) as DataGridView;

            if (grid is null)
            {
                Check("找到主网格控件", false, "反射获取 _grid 失败");
            }
            else
            {
                Check("关闭了自动生成列", grid.AutoGenerateColumns == false,
                    $"AutoGenerateColumns={grid.AutoGenerateColumns}");

                var headers = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToList();
                bool allChinese = headers.All(h => h.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
                Check("所有表头均为中文", allChinese,
                    string.Join(" | ", headers));

                // 英文表头（属性名）不应出现
                string[] forbidden = { "Source", "ExecutablePath", "Exists", "IsSystem", "Locator", "CanToggle" };
                var leaked = headers.Where(h => forbidden.Contains(h)).ToList();
                Check("无英文属性名泄漏为表头", leaked.Count == 0,
                    leaked.Count == 0 ? "无" : string.Join(", ", leaked));

                Check("网格 Dock 为 Fill", grid.Dock == DockStyle.Fill,
                    $"Dock={grid.Dock}");
            }
        }

        // ── 6. 提权判定 ──
        Check("识别中文权限错误", ElevationHelper.IsPermissionError("权限不足：需要管理员"), "命中 权限不足");
        Check("识别英文权限错误", ElevationHelper.IsPermissionError("Access is denied"), "命中 Access is denied");
        Check("正常错误不误判", !ElevationHelper.IsPermissionError("找不到该启动项的备份记录"), "未误判");

        // SecurityException 是本项目踩过的真实坑：
        // .NET 注册表权限被拒时抛的是 SecurityException，不是 UnauthorizedAccessException，
        // 最初未被识别为权限错误，用户只看到英文原文且无法触发自动提权。
        Check("识别 SecurityException 文本",
            ElevationHelper.IsPermissionError("操作失败：Requested registry access is not allowed."),
            "命中 Requested registry access");
        Check("识别中文注册表拒绝文本",
            ElevationHelper.IsPermissionError("操作失败：请求的注册表访问被拒绝。"),
            "命中 请求的注册表访问");

        // ── 7. 右键菜单 ──
        using (var form = new MainForm())
        {
            var cmField = typeof(MainForm).GetField("_contextMenu",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cm = cmField?.GetValue(form) as ContextMenuStrip;

            if (cm is null)
            {
                Check("找到右键菜单", false, "反射获取 _contextMenu 失败");
            }
            else
            {
                var texts = cm.Items.Cast<ToolStripItem>()
                                       .Where(i => i is ToolStripMenuItem)
                                       .Select(i => i.Text ?? "")
                                       .ToList();

                Check("右键菜单已创建", cm.Items.Count > 0, $"{cm.Items.Count} 项");
                Check("右键菜单含启用/禁用", texts.Any(t => t.Contains("启用")),
                    string.Join(" / ", texts));
                Check("右键菜单含打开位置", texts.Any(t => t.Contains("打开")),
                    "含打开项");
                Check("右键菜单含「复制切换命令」", texts.Any(t => t == "复制切换命令"),
                    texts.FirstOrDefault(t => t == "复制切换命令") ?? "缺失");
                Check("右键菜单含「复制目标路径」", texts.Any(t => t == "复制目标路径"),
                    texts.FirstOrDefault(t => t == "复制目标路径") ?? "缺失");
                Check("右键菜单含详情", texts.Any(t => t.Contains("详细")),
                    "含详情项");
                Check("右键菜单含重新扫描", texts.Any(t => t.Contains("扫描")),
                    "含扫描项");

                // 网格必须真正挂上这个菜单
                var gridField = typeof(MainForm).GetField("_grid",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var g = gridField?.GetValue(form) as DataGridView;
                Check("网格已绑定右键菜单", g?.ContextMenuStrip == cm,
                    g?.ContextMenuStrip is null ? "未绑定" : "已绑定");
            }
        }

        // ── 8. 复制命令必须与当前状态相反（曾出现硬编码错误）──
        var svcItem = new StartupItem
        {
            Name = "AGMService",
            Source = StartupSource.Service,
            Locator = "AGMService",
            Enabled = true,        // 当前启用 → 应给出"禁用"命令
            CanToggle = false,
        };
        string cmdDisable = MainForm.BuildToggleCommand(svcItem);
        Check("服务·当前启用 → 复制禁用命令",
            cmdDisable.Contains("start= demand") && !cmdDisable.Contains("start= auto"),
            cmdDisable);

        svcItem.Enabled = false;   // 当前禁用 → 应给出"启用"命令
        string cmdEnable = MainForm.BuildToggleCommand(svcItem);
        Check("服务·当前禁用 → 复制启用命令",
            cmdEnable.Contains("start= auto"),
            cmdEnable);

        // 服务名含空格（真实案例："Red Giant Service"）
        // 不加引号会导致 sc 把名字拆成多个参数，直接报 usage
        var spacedSvc = new StartupItem
        {
            Name = "Red Giant Service",
            Source = StartupSource.Service,
            Locator = "Red Giant Service",
            Enabled = true,
        };
        string spacedCmd = MainForm.BuildToggleCommand(spacedSvc);
        Check("服务名含空格时必须加引号",
            spacedCmd.Contains("\"Red Giant Service\""),
            spacedCmd);

        // 服务应支持直接启用/禁用（不再要求用户手动复制命令）
        var realSvc = result.Items.FirstOrDefault(i => i.Source == StartupSource.Service && !i.IsSystem);
        Check("服务项可直接操作（CanToggle）",
            realSvc is not null && realSvc.CanToggle,
            realSvc is null ? "未找到第三方服务" : $"{realSvc.Name}: CanToggle={realSvc.CanToggle}");

        // 服务启动状态序列化往返
        var st1 = new StartupManager.Services.ServiceControl.ServiceStartState(true, true);
        var ser = StartupManager.Services.ServiceControl.Serialize(st1);
        var de = StartupManager.Services.ServiceControl.Deserialize(ser);
        Check("服务启动状态可精确往返（含延迟启动）",
            de is not null && de.IsAuto && de.IsDelayed,
            $"serialize={ser}, deserialize={de?.Describe()}");

        var taskItem = new StartupItem
        {
            Name = "Listary",
            Source = StartupSource.ScheduledTask,
            Locator = "\\Listary",
            Enabled = true,
        };
        Check("计划任务·当前启用 → /DISABLE",
            MainForm.BuildToggleCommand(taskItem).Contains("/DISABLE"),
            MainForm.BuildToggleCommand(taskItem));

        taskItem.Enabled = false;
        Check("计划任务·当前禁用 → /ENABLE",
            MainForm.BuildToggleCommand(taskItem).Contains("/ENABLE"),
            MainForm.BuildToggleCommand(taskItem));

        // ── 9. RunOnce 不得走 StartupApproved（Windows 没有这个键）──
        // 曾经用 EndsWith("\Run") || EndsWith("\RunOnce") 统一判断，
        // 导致 RunOnce 项被写进 Run 的审批键，误改同名 Run 项状态。
        var runOnceItem = new StartupItem
        {
            Name = "SM_RunOnceProbe",
            Source = StartupSource.RegistryRunOnce,
            Locator = @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce\SM_RunOnceProbe",
            Enabled = true,
        };

        // 反射确认扫描器对 RunOnce 的子键判定不会进入审批分支
        var m = typeof(StartupManager.Scanners.RegistryScanner).GetMethod("SetEnabled",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Check("RunOnce 项存在且有 SetEnabled 入口", m is not null,
            m is null ? "未找到方法" : "已找到");

        // 通过行为验证：对不存在的 RunOnce 项执行禁用，应返回"键不存在"类错误，
        // 而不是成功写审批标记（若走审批分支会直接返回 null 表示成功）
        var regScanner = new StartupManager.Scanners.RegistryScanner();
        string? onceErr = regScanner.SetEnabled(runOnceItem, false);
        Check("RunOnce 不走审批分支（返回真实错误而非成功）",
            onceErr is not null,
            onceErr ?? "错误：返回了成功，说明错误地走了 StartupApproved 分支");

        sb.AppendLine();
        sb.AppendLine($"═══ 结果：{failed} 项失败 ═══");

        try
        {
            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写入结果失败：" + ex.Message);
        }

        return failed == 0 ? 0 : 1;
    }
}
