using System.ComponentModel;
using System.Diagnostics;

namespace CodexQuotaTray.Infrastructure;

/// <summary>快照只保留一个最早启动的桌面进程；由调用者负责释放进程句柄。</summary>
public sealed record CodexDesktopSnapshot(Process? Process, bool Complete) : IDisposable
{
    public void Dispose() => Process?.Dispose();
}

/// <summary>复用进程退出通知，不增加周期定时器或监听进程。</summary>
public sealed class CodexExitMonitor(
    bool observedDesktop = false,
    Func<CodexDesktopSnapshot>? readDesktop = null,
    Func<CancellationToken, Task>? restartGrace = null)
{
    private bool observed = observedDesktop;
    private readonly Func<CodexDesktopSnapshot> read = readDesktop ?? ReadDesktop;
    private readonly Func<CancellationToken, Task> grace = restartGrace ?? (token => Task.Delay(TimeSpan.FromSeconds(2), token));

    public void Reset() => observed = false; // 只能在当前等待已取消并完成后调用。

    // 同一时间只允许一个调用；取消后可复用。false 时由已有的分钟刷新再次尝试。
    public async Task<bool> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var confirmedEmpty = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var snapshot = await Task.Run(read, cancellationToken).ConfigureAwait(false);
            if (snapshot.Process is { } process)
            {
                observed = true;
                confirmedEmpty = false;
                // Windows 进程句柄变为有信号状态时完成，不占用一个阻塞等待线程。
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                continue; // 重新枚举，覆盖多实例以及运行中启动的新实例。
            }
            if (!observed || !snapshot.Complete) return false;
            if (confirmedEmpty) return true;
            await grace(cancellationToken).ConfigureAwait(false);
            confirmedEmpty = true; // 留出退出清理和快速重启的时间，再确认一次。
        }
    }

    private static CodexDesktopSnapshot ReadDesktop()
    {
        Process? selected = null;
        var earliest = DateTime.MaxValue;
        var complete = true;
        var candidates = Process.GetProcessesByName("ChatGPT");
        try
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var matches = CodexStartupProcess.InspectCurrentUserDesktop(candidate.Id);
                    if (matches is null) { complete = false; continue; }
                    if (matches != true || candidate.HasExited) continue;
                    var started = candidate.StartTime;
                    if (started >= earliest) continue;
                    earliest = started;
                    selected = candidate;
                }
                catch (InvalidOperationException) { /* 枚举后进程可能刚好退出。 */ }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException)
                { complete = false; }
            }
            return new(selected, complete);
        }
        catch { selected?.Dispose(); throw; }
        finally
        {
            foreach (var candidate in candidates)
                if (!ReferenceEquals(candidate, selected)) candidate.Dispose();
        }
    }
}
