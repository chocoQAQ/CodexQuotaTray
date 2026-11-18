using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaTray.Core;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaTray.Infrastructure;

/// <summary>依据目标进程的真实环境识别目录，而不是继承圆环自己的 CODEX_HOME。</summary>
public sealed class ActiveCodexSessionSource : ICodexSessionSource, IDisposable
{
    private string? previousKey;
    private readonly ForegroundHistory foregroundHistory = new();
    public CodexSession? LastResolved { get; private set; }

    public CodexSession Resolve()
    {
        LastResolved = null;
        var tree = ProcessTree();
        var foreground = foregroundHistory.Snapshot();
        using var self = Process.GetCurrentProcess();
        var ranked = new List<(CodexSessionCandidate Candidate, int Rank)>();
        var processes = Process.GetProcessesByName("codex");
        try
        {
            foreach (var process in processes)
            {
                var rank = -1;
                var desktopServer = false;
                try
                {
                    if (process.SessionId != self.SessionId || process.HasExited) continue;
                    var ancestors = Ancestors(process.Id, tree);
                    var started = process.StartTime.ToUniversalTime();
                    rank = Array.FindIndex(foreground, item => item.ObservedUtc >= started && ancestors.Contains(item.ProcessId));
                    var context = ProcessParametersReader.Read(process.Id);
                    var options = CodexLaunchOptions.Parse(context.Arguments);
                    if (!options.IsUserSession) continue;
                    desktopServer = options.IsAppServer && ancestors.Skip(1).Any(IsDesktopProcess);
                    var home = ResolveHome(context);
                    var key = $"{process.Id}:{started.Ticks}:{home}:{options.StorageOverride}";
                    ranked.Add((new(new CodexSession(key, process.Id, home, options.StorageOverride), false, desktopServer), rank));
                }
                catch (QuotaException error)
                {
                    ranked.Add((new(null, false, desktopServer, error.Message), rank));
                }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or
                    InvalidOperationException or IOException or ArgumentException or NotSupportedException)
                {
                    // 不可读的候选必须参与歧义判断，不能静默改用其他账号。
                    ranked.Add((new(null, false, desktopServer), rank));
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        var bestRank = ranked.Where(item => item.Rank >= 0).Select(item => item.Rank).DefaultIfEmpty(-1).Min();
        var candidates = ranked.Select(item => item.Candidate with { IsForeground = bestRank >= 0 && item.Rank == bestRank }).ToArray();
        var session = CodexSessionSelection.Select(candidates, previousKey);
        previousKey = session.Key;
        return LastResolved = session;
    }

    public void Dispose() => foregroundHistory.Dispose();

    public static string ResolveHome(ProcessLaunchContext context)
    {
        context.HomeEnvironment.TryGetValue("CODEX_HOME", out var home);
        home = string.IsNullOrEmpty(home) ? Path.Combine(context.ProfileDirectory, ".codex") : home;
        // 相对 CODEX_HOME 依赖启动时 cwd；进程可能已经 chdir，不能拿当前 cwd 猜测。
        if (!Path.IsPathFullyQualified(home))
            throw new QuotaException("目标 Codex 使用相对 CODEX_HOME，无法可靠恢复启动目录；请为 Codex 设置绝对目录。", true);
        using var handle = CreateFile(home, 0, 1 | 2 | 4, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("登录目录不可访问。");
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("登录目录无法解析。");
        var canonical = buffer.ToString();
        if (canonical.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) canonical = @"\\" + canonical[8..];
        else if (canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) canonical = canonical[4..];
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    private static bool IsDesktopProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            var path = process.MainModule?.FileName;
            return process.ProcessName == "ChatGPT" && path is not null &&
                (path.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is ArgumentException or Win32Exception or InvalidOperationException) { return false; }
    }

    private static List<int> Ancestors(int id, Dictionary<int, (int ParentId, string Name)> tree)
    {
        var result = new List<int>();
        while (id > 0 && result.Count < 16 && !result.Contains(id))
        {
            if (!tree.TryGetValue(id, out var entry)) { result.Add(id); break; }
            // Explorer 等公共祖先不代表用户正在操作某个 Codex 窗口。
            if (entry.Name.ToLowerInvariant() is "explorer.exe" or "services.exe" or "svchost.exe" or "wininit.exe" or "winlogon.exe") break;
            result.Add(id);
            id = entry.ParentId;
        }
        return result;
    }

    private static Dictionary<int, (int ParentId, string Name)> ProcessTree()
    {
        var result = new Dictionary<int, (int ParentId, string Name)>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new QuotaException("无法读取进程列表，请稍后重试。", true);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32First(snapshot, ref entry)) return result;
        do { result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile); }
        while (Process32Next(snapshot, ref entry));
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll")] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
