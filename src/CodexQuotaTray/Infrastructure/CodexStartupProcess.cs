using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaTray.Infrastructure;

/// <summary>事件是全机日志：唤起后只接受当前用户、当前会话中的 Codex 桌面进程。</summary>
public static class CodexStartupProcess
{
    public static bool IsCurrentUserDesktop(int processId)
        => InspectCurrentUserDesktop(processId) == true;

    /// <summary>null 表示权限或系统查询失败，不能据此认定 Codex 已退出。</summary>
    public static bool? InspectCurrentUserDesktop(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            using var self = Process.GetCurrentProcess();
            if (process.SessionId != self.SessionId) return false;
            using var handle = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid) return Marshal.GetLastWin32Error() == 87 ? false : null;
            var length = 512u;
            var id = new StringBuilder((int)length);
            var result = GetApplicationUserModelId(handle, ref length, id);
            if (result is 15700 or 15703) return false; // 未打包进程或没有应用标识。
            if (result != 0) return null;
            if (id.ToString() != CodexStartupRegistration.ApplicationId) return false;
            if (!OpenProcessToken(handle, 0x0008, out var token)) return null;
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
                return owner.User == current.User && !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return false; } // 目标已退出。
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder id);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
