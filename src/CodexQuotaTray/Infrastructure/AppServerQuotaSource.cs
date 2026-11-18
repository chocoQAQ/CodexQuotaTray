using System.ComponentModel;
using System.Diagnostics;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.Infrastructure;

/// <summary>新建短时 stdio 子进程，复用目标 Codex 的程序和登录目录，不接管宿主私有管道。</summary>
public sealed class AppServerQuotaSource(ICodexSessionSource sessions, string? explicitHome = null) : IQuotaSource
{
    public Task<QuotaConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.Resolve();
        if (session.StorageOverride == "ephemeral")
            throw new QuotaException("当前 Codex 仅使用内存登录，独立 app-server 无法共享此账号。", true);
        using var process = Process.GetProcessById(session.ProcessId);
        var executable = process.MainModule?.FileName ?? throw new QuotaException("无法定位 Codex 程序。", true);
        var home = explicitHome ?? session.Home;
        return Task.FromResult(new QuotaConnection("app-server:" + session.Key + ":" + home,
            token => ReadAsync(executable, home, session.StorageOverride, token)));
    }

    private static async Task<QuotaSnapshot> ReadAsync(string executable, string home, string? storage, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        if (storage is "file" or "keyring" or "auto")
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"cli_auth_credentials_store=\"{storage}\"");
        }
        start.Environment["CODEX_HOME"] = home;
        // 不把圆环宿主进程中的 API Key 或临时访问令牌误用为目标账号。
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" }) start.Environment.Remove(name);
        using var child = new Process { StartInfo = start };
        Task? stderr = null;
        try
        {
            child.Start();
            using var stop = token.Register(() =>
            {
                // 主程序退出时同步终止自己的查询子进程，避免消息循环先结束留下孤儿进程。
                try { if (!child.HasExited) child.Kill(true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            });
            stderr = DrainAsync(child.StandardError, token);
            return await new AppServerProtocol(child.StandardOutput, child.StandardInput).ReadQuotaAsync(token);
        }
        catch (Exception error) when (error is Win32Exception or IOException or System.Text.Json.JsonException)
        {
            throw new QuotaException("无法读取 app-server，请检查 Codex 版本、登录状态或切换到 auth.json。", true);
        }
        finally
        {
            // 只结束本次创建的查询进程，绝不操作正在运行的 Codex/ChatGPT 宿主。
            try
            {
                child.StandardInput.Close();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try { await child.WaitForExitAsync(shutdown.Token); }
                catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException) { }
            if (stderr is not null)
            {
                try { await stderr.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { }
            }
        }
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        // 持续排空错误管道但不保存其内容，既防死锁，也避免输出账号或原始响应。
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) > 0) { }
    }
}
