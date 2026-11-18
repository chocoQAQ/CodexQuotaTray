using System.Runtime.InteropServices;

namespace CodexQuotaTray.Infrastructure;

/// <summary>保留最近的前台 PID，让用户打开托盘菜单后仍能选中刚才操作的 Codex。</summary>
internal sealed class ForegroundHistory : IDisposable
{
    private readonly object sync = new();
    private readonly List<(int ProcessId, DateTime ObservedUtc)> history = [];
    private readonly WinEventCallback callback;
    private readonly IntPtr hook;

    public ForegroundHistory()
    {
        callback = (_, _, window, _, _, _, _) => Observe(window);
        hook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
        Observe(GetForegroundWindow());
    }

    public (int ProcessId, DateTime ObservedUtc)[] Snapshot()
    {
        Observe(GetForegroundWindow());
        lock (sync) return history.ToArray();
    }

    private void Observe(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return;
        lock (sync)
        {
            history.RemoveAll(item => item.ProcessId == processId);
            history.Insert(0, ((int)processId, DateTime.UtcNow));
            if (history.Count > 32) history.RemoveAt(history.Count - 1);
        }
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        GC.KeepAlive(callback);
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
