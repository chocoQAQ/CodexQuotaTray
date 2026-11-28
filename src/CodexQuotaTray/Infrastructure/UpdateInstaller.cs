using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.Infrastructure;

public sealed record UpdatePlan(string Target, string Candidate, string Sha256, string Version,
    int OriginalProcessId, long OriginalStartTicks, string[] OriginalArguments,
    string OriginalSha256, string HandoffEvent, bool FollowDesktop);

/// <summary>把当前单文件 EXE 复制为临时更新进程，避免替换仍被自己占用的文件。</summary>
public sealed class UpdateInstaller(string executable, string[] originalArguments)
{
    private const string ReadyPrefix = @"Local\CodexQuotaTray.UpdateReady.";
    private static string GateName => @"Local\" + SingleInstance.WindowName + ".Update";
    private static string WorkRoot => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "Updates");

    public static string BuildVariant => typeof(UpdateInstaller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateVariant")?.Value ?? "no-runtime";
    public static bool IsSingleFileBuild => typeof(UpdateInstaller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "SingleFileBuild" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));

    public string CreateWorkDirectory()
    {
        if (!IsSingleFileBuild) throw new InvalidOperationException("自动更新需要使用发布的单文件版本。");
        // 在下载前验证安装目录可写，不申请管理员权限，也不触碰自启项。
        var probe = Path.Combine(Path.GetDirectoryName(executable)!, ".quota-update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        if ((File.GetAttributes(executable) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("当前程序文件只读。");
        Directory.CreateDirectory(WorkRoot);
        if ((File.GetAttributes(WorkRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新目录不能是重解析链接。");
        var directory = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public Task LaunchAsync(PreparedUpdate prepared, AvailableUpdate update, bool followDesktop, CancellationToken token)
        => Task.Run(() => Launch(prepared, update, followDesktop, token), token);

    private void Launch(PreparedUpdate prepared, AvailableUpdate update, bool followDesktop, CancellationToken token)
    {
        var version = FileVersionInfo.GetVersionInfo(prepared.ExecutablePath);
        var actual = new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
        if (actual != update.Version) throw new InvalidDataException("下载程序的版本与发行版不一致。");
        using var self = Process.GetCurrentProcess();
        var directory = Path.GetDirectoryName(prepared.ExecutablePath)!;
        ValidateWorkDirectory(directory);
        var handoffName = ReadyPrefix + Guid.NewGuid().ToString("N");
        using var handoff = new EventWaitHandle(false, EventResetMode.ManualReset, handoffName);
        var plan = new UpdatePlan(Path.GetFullPath(executable), prepared.ExecutablePath, prepared.Sha256,
            update.Version.ToString(), self.Id, self.StartTime.ToUniversalTime().Ticks, originalArguments,
            Hash(executable), handoffName, followDesktop);
        var manifest = Path.Combine(directory, "update.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(plan));
        var helper = Path.Combine(directory, "CodexQuotaTray.Updater.exe");
        File.Copy(executable, helper, false);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(manifest);
        using var process = Process.Start(info) ?? throw new IOException("无法启动更新进程。");
        var accepted = false;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!handoff.WaitOne(100))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(15))
                    throw new IOException("更新进程未能接管安装。");
            }
            token.ThrowIfCancellationRequested();
            accepted = true; // 只有更新器已取得锁并验证计划后，主程序才退出。
        }
        finally
        {
            if (!accepted && !process.HasExited) { process.Kill(); process.WaitForExit(5000); }
        }
    }

    public static bool IsApplying()
    {
        if (!Mutex.TryOpenExisting(GateName, out var gate)) return false;
        using (gate)
        {
            try { if (!gate.WaitOne(0)) return true; }
            catch (AbandonedMutexException) { }
            gate.ReleaseMutex();
            return false;
        }
    }

    public static void SignalReady(string name)
    {
        if (!name.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[ReadyPrefix.Length..], "N", out _))
            throw new ArgumentException("更新确认标识无效。");
        using var ready = EventWaitHandle.OpenExisting(name);
        ready.Set();
    }

    public static int Apply(string manifest, Action<string>? reportFailure = null)
    {
        reportFailure ??= message => MessageBox.Show(message, "CodexQuotaTray · 更新失败",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        UpdatePlan? plan = null;
        UpdateFileTransaction? files = null;
        Process? child = null;
        string? directory = null;
        FileStream? workLock = null;
        using var gate = new Mutex(false, GateName);
        var ownsGate = false;
        var handedOff = false;
        try
        {
            try { ownsGate = gate.WaitOne(0); }
            catch (AbandonedMutexException) { ownsGate = true; }
            if (!ownsGate) return 1;
            manifest = Path.GetFullPath(manifest);
            var workDirectory = Path.GetDirectoryName(manifest)!;
            ValidateWorkDirectory(workDirectory);
            directory = workDirectory;
            if (Path.GetFileName(manifest) != "update.json" || new FileInfo(manifest).Length > 32 * 1024)
                throw new InvalidDataException("更新计划无效。");
            workLock = new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var candidatePlan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException("更新计划为空。");
            ValidatePlan(candidatePlan, directory);
            plan = candidatePlan;
            if (!string.Equals(Environment.ProcessPath, Path.Combine(directory, "CodexQuotaTray.Updater.exe"), StringComparison.OrdinalIgnoreCase) ||
                !Hash(Environment.ProcessPath!).Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                !Hash(plan.Target).Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装源与正在运行的程序不一致。");
            var candidateVersion = FileVersionInfo.GetVersionInfo(plan.Candidate);
            if (new Version(candidateVersion.FileMajorPart, candidateVersion.FileMinorPart,
                    candidateVersion.FileBuildPart, candidateVersion.FilePrivatePart) != Version.Parse(plan.Version))
                throw new InvalidDataException("更新程序版本不匹配。");
            using (var original = Process.GetProcessById(plan.OriginalProcessId))
            {
                if (original.StartTime.ToUniversalTime().Ticks != plan.OriginalStartTicks ||
                    !string.Equals(original.MainModule?.FileName, plan.Target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("旧程序身份不匹配。");
            }
            SignalReady(plan.HandoffEvent);
            handedOff = true;
            WaitForOriginal(plan);
            if (!Hash(plan.Target).Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原程序文件已发生变化。");
            files = new UpdateFileTransaction(plan.Target, plan.Candidate, plan.Sha256);
            files.Install();

            var eventName = ReadyPrefix + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            child = Start(plan, eventName);
            var watch = Stopwatch.StartNew();
            while (!ready.WaitOne(200))
            {
                if (child.HasExited || watch.Elapsed > TimeSpan.FromSeconds(25))
                    throw new IOException("新版程序未能确认启动。");
            }
            files.Commit();
            WriteOutcome(directory, true, "更新完成，新版本已启动。");
            return 0;
        }
        catch (Exception)
        {
            if (!handedOff) return 1; // 主程序还在，交接失败由主界面报告，不留下孤立弹窗。
            try
            {
                if (child is not null && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                files?.Rollback();
                if (plan is not null && File.Exists(plan.Target))
                {
                    // 回滚也携带握手标识，允许它穿过仍由更新器持有的启动门锁。
                    var restoredEvent = ReadyPrefix + Guid.NewGuid().ToString("N");
                    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, restoredEvent);
                    using var restored = Start(plan, restoredEvent);
                    if (!ready.WaitOne(TimeSpan.FromSeconds(25)))
                        throw new IOException("旧版已恢复，但未能确认启动。");
                }
                if (directory is not null) WriteOutcome(directory, false, "更新失败，原版本已保留或恢复。");
                reportFailure("更新未完成，已保留或恢复原版本。请稍后重新启动程序再试。");
            }
            catch (Exception)
            {
                reportFailure("更新未完成，请重新下载发行版。若存在备份，请保留程序目录中的 CodexQuotaTray.exe.backup-* 文件。");
            }
            return 1;
        }
        finally
        {
            child?.Dispose();
            try { files?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            workLock?.Dispose();
            if (ownsGate) gate.ReleaseMutex();
            if (directory is not null && plan is not null)
            {
                // 当前 helper 本身仍被 Windows 占用，留待下一次正常启动清理。
                foreach (var name in new[] { "package.zip", "CodexQuotaTray.exe", "update.json" })
                    try { File.Delete(Path.Combine(directory, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static void CleanupOldWorkDirectories()
    {
        try
        {
            if (!Directory.Exists(WorkRoot)) return;
            foreach (var directory in Directory.GetDirectories(WorkRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddMinutes(-30)) continue;
                try
                {
                    ValidateWorkDirectory(directory);
                    using (new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate,
                               FileAccess.ReadWrite, FileShare.None)) { }
                    DeleteWorkDirectory(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void DeleteWorkDirectory(string directory)
    {
        ValidateWorkDirectory(directory);
        try
        {
            // 只删除已知文件，不递归遍历目录，避免异常链接或用户文件扩大清理范围。
            foreach (var name in new[] { "package.zip", "CodexQuotaTray.exe", "CodexQuotaTray.Updater.exe", "update.json", "active.lock", "result.json" })
                File.Delete(Path.Combine(directory, name));
            Directory.Delete(directory, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateWorkDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), WorkRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            (File.GetAttributes(WorkRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新目录无效。");
    }

    private static void ValidatePlan(UpdatePlan plan, string directory)
    {
        if (!Path.IsPathFullyQualified(plan.Target) || Path.GetFileName(plan.Target) != "CodexQuotaTray.exe" ||
            plan.Target.StartsWith(WorkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            plan.Candidate != Path.Combine(directory, "CodexQuotaTray.exe") ||
            !Regex.IsMatch(plan.Sha256, "^[A-Fa-f0-9]{64}$") ||
            !Regex.IsMatch(plan.OriginalSha256, "^[A-Fa-f0-9]{64}$") ||
            !plan.HandoffEvent.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(plan.HandoffEvent[ReadyPrefix.Length..], "N", out _) ||
            !UpdateClient.TryParseVersion(plan.Version, out _) || plan.OriginalProcessId <= 0 ||
            plan.OriginalStartTicks <= 0 || plan.OriginalArguments is null ||
            !(plan.OriginalArguments.Length == 0 || plan.OriginalArguments.Length == 2 &&
              plan.OriginalArguments[0] == "--codex-home" && Path.IsPathFullyQualified(plan.OriginalArguments[1])))
            throw new InvalidDataException("更新计划内容无效。");
    }

    private static void WaitForOriginal(UpdatePlan plan)
    {
        try
        {
            using var original = Process.GetProcessById(plan.OriginalProcessId);
            if (original.StartTime.ToUniversalTime().Ticks != plan.OriginalStartTicks) return;
            if (!original.WaitForExit(30000)) throw new IOException("旧版本尚未退出。");
        }
        catch (ArgumentException) { /* 旧进程已经结束。 */ }
    }

    private static Process Start(UpdatePlan plan, string readyEvent)
    {
        var info = new ProcessStartInfo(plan.Target) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
        foreach (var argument in plan.OriginalArguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--update-ready"); info.ArgumentList.Add(readyEvent);
        info.ArgumentList.Add("--skip-update-check");
        if (plan.FollowDesktop) info.ArgumentList.Add("--follow-session");
        return Process.Start(info) ?? throw new IOException("无法重新启动程序。");
    }

    private static void WriteOutcome(string directory, bool success, string message)
    {
        try { File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { success, message })); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
