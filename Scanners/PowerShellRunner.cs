namespace StartupManager.Scanners;

using System.Diagnostics;
using System.Text;

/// <summary>
/// 通过 PowerShell 执行查询脚本的公共封装。
///
/// 重要：不要用 `powershell -Command -` 从标准输入喂脚本。
/// 实测在中文 Windows 上，stdin 传脚本会因控制台代码页与 BOM 问题
/// 导致脚本静默失败（输出为空、不报错），非常难排查。
/// 正确做法是写入临时 .ps1 文件（UTF-8 带 BOM）后以 -File 执行。
/// </summary>
internal static class PowerShellRunner
{
    /// <summary>最近一次执行的错误描述（供扫描器上报到 Warnings）。</summary>
    [ThreadStatic]
    public static string LastError = "";

    public static string Run(string script, int timeoutMs = 60000)
    {
        LastError = "";

        // 优先系统 Temp；若不可写（沙箱限制/权限异常），退回到应用目录旁的缓存目录。
        string[] candidates =
        {
            Path.GetTempPath(),
            Path.Combine(AppContext.BaseDirectory, ".smcache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "StartupManager", "cache"),
        };

        string? tmp = null;
        Exception? lastEx = null;

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                tmp = Path.Combine(dir, $"sm_{Guid.NewGuid():N}.ps1");

                // UTF-8 带 BOM：PowerShell 5.1 依赖 BOM 才能正确识别中文
                File.WriteAllText(tmp, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                tmp = null;
            }
        }

        if (tmp is null)
        {
            LastError = "无法创建临时脚本文件：" + (lastEx?.Message ?? "未知原因");
            return "";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmp}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                LastError = "无法启动 powershell.exe";
                return "";
            }

            // 先异步读 stderr，避免管道缓冲区写满导致死锁
            var errTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            string err = errTask.Result;

            if (!string.IsNullOrWhiteSpace(err) && string.IsNullOrWhiteSpace(output))
            {
                LastError = err.Trim();
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return "";
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // 临时文件删除失败不影响结果
            }
        }
    }
}
