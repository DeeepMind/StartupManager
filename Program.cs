namespace StartupManager;

using System.Text;
using System.Text.Json;
using StartupManager.Models;
using StartupManager.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // ── 提权实例：执行单个启用/禁用操作后退出 ──
        if (args.Length >= 2 && args[0].Equals(ElevationHelper.ElevateSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return RunElevatedOperation(args[1]);
        }

        // ── 无界面自检模式 ──
        if (args.Length >= 1 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            string outPath = args.Length >= 2
                ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "selftest.txt");
            return SelfTest.Run(outPath);
        }

        // ── 状态导出模式：把扫描结果写成文本，便于与任务管理器逐项对照 ──
        if (args.Length >= 1 && args[0].Equals("--dump", StringComparison.OrdinalIgnoreCase))
        {
            string outPath = args.Length >= 2
                ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "dump.txt");

            var svc = new StartupService();
            var res = svc.ScanAll();
            var sb = new StringBuilder();
            sb.AppendLine("状态\t来源\t名称\t命令行");
            foreach (var it in res.Items.Where(i => !i.IsSystem)
                                        .OrderBy(i => i.SourceText).ThenBy(i => i.Name))
            {
                sb.AppendLine($"{(it.Enabled ? "启用" : "已禁用")}\t{it.SourceText}\t{it.Name}\t{it.Command}");
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// 以管理员身份运行时执行的单个操作。
    /// 由于是独立进程，只打印结果并短暂停留，便于用户看到成功/失败原因。
    /// </summary>
    private static int RunElevatedOperation(string json)
    {
        ApplicationConfiguration.Initialize();

        StartupItemRef? op = null;
        try
        {
            op = JsonSerializer.Deserialize<StartupItemRef>(json);
        }
        catch
        {
            // 解析失败下面统一处理
        }

        if (op is null)
        {
            MessageBox.Show("提权参数无效，操作已取消。", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        // 还原成一个 StartupItem，供扫描器的 SetEnabled 使用
        var item = new StartupItem
        {
            Source = (StartupSource)op.Source,
            Locator = op.Locator,
            Note = op.Note,
            Name = Path.GetFileName(op.Locator.TrimEnd('\\')),
            CanToggle = true,
        };

        var svc = new StartupService();
        string? err = svc.SetEnabled(item, op.Enable);

        string action = op.Enable ? "启用" : "禁用";
        if (err is null)
        {
            MessageBox.Show($"已{action}成功。\n\n{op.Locator}", "操作完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        MessageBox.Show($"{action}失败：\n\n{err}", "操作失败",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 1;
    }
}
