using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaTray.Infrastructure;

/// <summary>只读取同用户进程的启动参数区；不扫描堆、不写内存、不启用调试特权。</summary>
public sealed class ProcessLaunchContext
{
    public required int ParentId { get; init; }
    public required string CurrentDirectory { get; init; }
    public required string[] Arguments { get; init; }
    public required IReadOnlyDictionary<string, string> HomeEnvironment { get; init; }
    public required string ProfileDirectory { get; init; }
    public override string ToString() => "ProcessLaunchContext [filtered]";
}

public static class ProcessParametersReader
{
    private const int MaximumEnvironmentBytes = 1024 * 1024;

    public static ProcessLaunchContext Read(int processId)
    {
        if (!Environment.Is64BitProcess)
            throw new NotSupportedException("请使用 win-x64 发布版本。");
        using var process = OpenProcess(0x0400 | 0x0010, false, processId);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(process, 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        string profile;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var self = WindowsIdentity.GetCurrent())
        {
            if (owner.User != self.User) throw new UnauthorizedAccessException();
            var folder = new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173");
            if (SHGetKnownFolderPath(ref folder, 0, token, out var path) != 0)
                throw new Win32Exception("无法获取目标用户的主目录。");
            try { profile = Marshal.PtrToStringUni(path) ?? throw new Win32Exception("用户目录为空。"); }
            finally { Marshal.FreeCoTaskMem(path); }
        }

        if (NtQueryInformationProcess(process, 0, out var information,
                Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            throw new Win32Exception("无法读取进程参数。");
        if (NtQueryWow64Information(process, 26, out var wow64Peb, IntPtr.Size, out _) != 0)
            throw new Win32Exception("无法识别进程位数。");

        var is32 = wow64Peb != IntPtr.Zero;
        var peb = is32 ? wow64Peb : information.Peb;
        // PEB / RTL_USER_PROCESS_PARAMETERS 属于 Windows 内部布局。
        // x64 与 WOW64 分别处理；任何读取异常都向上报告，不能回退到圆环自己的环境。
        var parameters = Pointer(process, peb + (is32 ? 0x10 : 0x20), is32);
        var currentDirectory = UnicodeString(process, parameters + (is32 ? 0x24 : 0x38), is32);
        var command = UnicodeString(process, parameters + (is32 ? 0x40 : 0x70), is32);
        var environment = Pointer(process, parameters + (is32 ? 0x48 : 0x80), is32);
        var values = ReadHomeEnvironment(process, environment);
        return new ProcessLaunchContext
        {
            ParentId = checked((int)information.ParentId), CurrentDirectory = currentDirectory,
            Arguments = SplitArguments(command), HomeEnvironment = values, ProfileDirectory = profile
        };
    }

    public static string[] SplitArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "").ToArray();
        }
        finally { LocalFree(argv); }
    }

    private static Dictionary<string, string> ReadHomeEnvironment(SafeProcessHandle process, IntPtr address)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = new StringBuilder();
        var previousNull = false;
        for (var offset = 0; offset < MaximumEnvironmentBytes;)
        {
            var current = address + offset;
            if (VirtualQueryEx(process, current, out var region, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                throw new Win32Exception("无法读取进程环境。");
            var available = region.BaseAddress.ToInt64() + (long)region.RegionSize - current.ToInt64();
            var count = (int)Math.Min(Math.Min(available, 4096), MaximumEnvironmentBytes - offset);
            count &= ~1;
            if (count < 2) throw new Win32Exception("进程环境布局无效。");
            var bytes = Bytes(process, current, count);
            try
            {
                for (var i = 0; i < bytes.Length; i += 2)
                {
                    var character = (char)(bytes[i] | bytes[i + 1] << 8);
                    if (character == '\0')
                    {
                        if (previousNull) return result;
                        var text = entry.ToString();
                        var separator = text.IndexOf('=');
                        if (separator > 0)
                        {
                            var name = text[..separator];
                            if (name.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase))
                                result[name] = text[(separator + 1)..];
                        }
                        entry.Clear();
                        previousNull = true;
                    }
                    else { previousNull = false; entry.Append(character); }
                }
            }
            finally { Array.Clear(bytes); }
            offset += count;
        }
        throw new Win32Exception("进程环境超过读取上限。");
    }

    private static IntPtr Pointer(SafeProcessHandle process, IntPtr address, bool is32)
    {
        var data = Bytes(process, address, is32 ? 4 : 8);
        var result = is32 ? new IntPtr(BitConverter.ToUInt32(data)) : new IntPtr(BitConverter.ToInt64(data));
        if (result == IntPtr.Zero) throw new Win32Exception("进程参数指针无效。");
        return result;
    }

    private static string UnicodeString(SafeProcessHandle process, IntPtr address, bool is32)
    {
        var length = BitConverter.ToUInt16(Bytes(process, address, 2));
        if (length == 0) return "";
        if ((length & 1) != 0) throw new Win32Exception("进程字符串格式无效。");
        var buffer = Pointer(process, address + (is32 ? 4 : 8), is32);
        return Encoding.Unicode.GetString(Bytes(process, buffer, length));
    }

    private static byte[] Bytes(SafeProcessHandle process, IntPtr address, int count)
    {
        var result = new byte[count];
        if (!ReadProcessMemory(process, address, result, (nuint)count, out var read) || read != (nuint)count)
            throw new Win32Exception("进程参数不可读取或进程已退出。");
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, Peb, AffinityMask, BasePriority, ProcessId, ParentId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int type, out ProcessBasicInformation info, int length, out int returned);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")] private static extern int NtQueryWow64Information(SafeProcessHandle process, int type, out IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] private static extern nuint VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MemoryBasicInformation info, nuint length);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);
}
