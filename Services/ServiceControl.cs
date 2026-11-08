namespace StartupManager.Services;

using System.Diagnostics;

/// <summary>
/// 服务的启动类型读取与修改。
///
/// 为什么不再让用户手动复制命令：
///   服务启动类型是可以通过 API 安全修改的，而且完全可逆。
///   让用户去管理员命令行里手动敲 sc config 是不必要的负担。
///
/// 关键细节（很容易出错）：
///   服务的自动启动有「立即启动」和「延迟启动」两种：
///     Auto  + DelayedAutoStart=false
///     Auto  + DelayedAutoStart=true   ← 延迟启动
///   如果只记录 Auto/Demand 而不记录 Delayed 标志，
///   把延迟启动的服务"禁用再启用"后会变成普通自动启动，
///   属于静默改变用户配置。因此禁用时必须记住原始状态，
///   恢复时原样写回。
/// </summary>
public static class ServiceControl
{
    /// <summary>服务的启动类型快照，用于精确恢复。</summary>
    public sealed record ServiceStartState(bool IsAuto, bool IsDelayed)
    {
        public string Describe() => IsAuto
            ? (IsDelayed ? "自动（延迟启动）" : "自动")
            : "手动";
    }

    /// <summary>读取服务的当前启动类型。</summary>
    public static ServiceStartState? GetState(string serviceName)
    {
        string? raw = RunSc($"qc \"{serviceName}\"");
        if (raw is null)
        {
            return null;
        }

        // sc qc 输出形如：
        //   START_TYPE         : 2   AUTO_START  (DELAYED)
        // 数值映射：2=自动，3=手动，4=禁用
        bool isAuto = raw.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase);
        bool isDelayed = raw.Contains("DELAYED", StringComparison.OrdinalIgnoreCase);

        // 若没有 START_TYPE 行，说明查询失败
        if (!raw.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ServiceStartState(isAuto, isDelayed);
    }

    /// <summary>
    /// 修改服务启动类型。
    /// disable=true  → 改为手动启动（demand），并记录原状态
    /// disable=false → 还原为禁用前的状态（自动 / 自动延迟）
    /// </summary>
    public static string? SetEnabled(string serviceName, bool enable, ServiceStartState? savedOriginal)
    {
        // 不用 IsAdmin() 做前置拦截：
        // 在部分受限环境（沙箱、受限令牌）下 WindowsPrincipal 的判断
        // 可能与实际可用权限不一致，导致"明明能改却被自己挡住"。
        // 直接尝试执行，用真实结果判断更可靠。
        if (enable)
        {
            // 还原：优先用保存的原始状态，没有则默认 auto
            bool delayed = savedOriginal?.IsDelayed == true;
            string arg = delayed ? "delayed-auto" : "auto";
            return ExecSc($"config \"{serviceName}\" start= {arg}");
        }
        else
        {
            // 禁用：改为手动。注意用 demand 而非 disabled ——
            // disabled 会让依赖它的程序无法启动，风险过大。
            return ExecSc($"config \"{serviceName}\" start= demand");
        }
    }

    /// <summary>把服务状态存成可持久化的字符串（"auto"/"auto-delayed"）。</summary>
    public static string Serialize(ServiceStartState? s)
    {
        if (s is null)
        {
            return "auto";
        }

        return s.IsAuto ? (s.IsDelayed ? "auto-delayed" : "auto") : "demand";
    }

    public static ServiceStartState? Deserialize(string? text) => text switch
    {
        "auto-delayed" => new ServiceStartState(true, true),
        "auto" => new ServiceStartState(true, false),
        "demand" => new ServiceStartState(false, false),
        _ => null,
    };

    private static string? RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var errTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            _ = errTask;

            return output;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExecSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return "无法启动 sc.exe";
            }

            var errTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            string err = errTask.Result;
            p.WaitForExit(15000);

            if (p.ExitCode != 0)
            {
                string msg = (string.IsNullOrWhiteSpace(err) ? output : err).Trim();
                if (msg.Contains("拒绝访问", StringComparison.Ordinal) ||
                    msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    return "权限不足：修改服务需要管理员权限";
                }
                return "sc 返回错误：" + msg;
            }

            return null;
        }
        catch (Exception ex)
        {
            return "执行失败：" + ex.Message;
        }
    }
}
