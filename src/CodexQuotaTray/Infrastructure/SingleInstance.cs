using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CodexQuotaTray.Infrastructure;

/// <summary>同一用户会话只运行一个圆环；第二次启动只发送“显示详情”消息。</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    public bool IsFirst { get; }
    public static readonly string WindowName = "CodexQuotaTray.Standalone.1." + WindowsIdentity.GetCurrent().User?.Value;
    public static readonly int ShowMessage = (int)RegisterWindowMessage("CodexQuotaTray.Standalone.Show");

    public SingleInstance()
    {
        mutex = new Mutex(true, @"Local\" + WindowName, out var created);
        IsFirst = created;
    }

    public static void ShowExisting()
    {
        // 首个实例可能尚在初始化：短暂等候其消息窗口，不创建第二个托盘。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var window = FindWindow(null, WindowName);
            if (window != IntPtr.Zero)
            {
                GetWindowThreadProcessId(window, out var processId);
                AllowSetForegroundWindow(processId);
                PostMessage(window, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (IsFirst) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
