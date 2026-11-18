using System.ComponentModel;
using System.Diagnostics;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.Infrastructure;

public sealed class CodexActivity : ICodexActivity
{
    public bool IsRunning()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var name in new[] { "codex", "ChatGPT" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.SessionId != self.SessionId || process.HasExited) continue;
                        if (name == "codex") return true;
                        var path = process.MainModule?.FileName;
                        // 商店版 Codex 的进程名也叫 ChatGPT，必须排除普通 ChatGPT。
                        if (path is not null &&
                            (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                             path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                    catch (Win32Exception) { /* 进程不可访问时继续检查其他实例。 */ }
                    catch (InvalidOperationException) { /* 枚举后进程可能刚好退出。 */ }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return false;
    }
}
