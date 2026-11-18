using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.Infrastructure;

public sealed record UpdatePlan(string Target, string Candidate, string Sha256, string Version,
    int OriginalProcessId, long OriginalStartTicks, string[] OriginalArguments);

/// <summary>把当前单文件 EXE 复制为临时更新进程，避免替换仍被自己占用的文件。</summary>
public sealed class UpdateInstaller(string executable, string[] originalArguments)
{
    private const string ReadyPrefix = @"Local\CodexQuotaTray.UpdateReady.";
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
        var directory = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Launch(PreparedUpdate prepared, AvailableUpdate update)
    {
        var version = FileVersionInfo.GetVersionInfo(prepared.ExecutablePath);
        var actual = new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
        if (actual != update.Version) throw new InvalidDataException("下载程序的版本与发行版不一致。");
        using var self = Process.GetCurrentProcess();
        var directory = Path.GetDirectoryName(prepared.ExecutablePath)!;
        var plan = new UpdatePlan(Path.GetFullPath(executable), prepared.ExecutablePath, prepared.Sha256,
            update.Version.ToString(), self.Id, self.StartTime.ToUniversalTime().Ticks, originalArguments);
        var manifest = Path.Combine(directory, "update.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(plan));
        var helper = Path.Combine(directory, "CodexQuotaTray.Updater.exe");
        File.Copy(executable, helper, false);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(manifest);
        using var process = Process.Start(info) ?? throw new IOException("无法启动更新进程。");
    }

    public static void SignalReady(string name)
    {
        if (!name.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[ReadyPrefix.Length..], "N", out _))
            throw new ArgumentException("更新确认标识无效。");
        using var ready = EventWaitHandle.OpenExisting(name);
        ready.Set();
    }

    public static int Apply(string manifest)
    {
        UpdatePlan? plan = null;
        UpdateFileTransaction? files = null;
        Process? child = null;
        string? directory = null;
        FileStream? workLock = null;
        try
        {
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
            var candidateVersion = FileVersionInfo.GetVersionInfo(plan.Candidate);
            if (new Version(candidateVersion.FileMajorPart, candidateVersion.FileMinorPart,
                    candidateVersion.FileBuildPart, candidateVersion.FilePrivatePart) != Version.Parse(plan.Version))
                throw new InvalidDataException("更新程序版本不匹配。");
            WaitForOriginal(plan);
            files = new UpdateFileTransaction(plan.Target, plan.Candidate, plan.Sha256);
            files.Install();

            var eventName = ReadyPrefix + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            child = Start(plan, eventName, false);
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!ready.WaitOne(200))
            {
                if (child.HasExited || DateTime.UtcNow >= deadline)
                    throw new IOException("新版程序未能确认启动。");
            }
            files.Commit();
            WriteOutcome(directory, true, "更新完成，新版本已启动。");
            return 0;
        }
        catch (Exception)
        {
            try
            {
                if (child is not null && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                files?.Rollback();
                if (plan is not null && File.Exists(plan.Target))
                {
                    using var restored = Start(plan, null, true);
                }
                if (directory is not null) WriteOutcome(directory, false, "更新失败，原版本已保留或恢复。");
                MessageBox.Show("更新未完成，已保留或恢复原版本。请稍后重新启动程序再试。",
                    "Codex 额度圆环 · 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception)
            {
                MessageBox.Show("更新未完成。请重新下载发行版；原版本备份位于程序目录的 .backup 文件中。",
                    "Codex 额度圆环 · 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 1;
        }
        finally
        {
            child?.Dispose();
            try { files?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            workLock?.Dispose();
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
                    using (new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate,
                               FileAccess.ReadWrite, FileShare.None)) { }
                    Directory.Delete(directory, true);
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
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateWorkDirectory(string directory)
    {
        if (!string.Equals(Path.GetDirectoryName(directory), WorkRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("更新目录无效。");
    }

    private static void ValidatePlan(UpdatePlan plan, string directory)
    {
        if (!Path.IsPathFullyQualified(plan.Target) || Path.GetFileName(plan.Target) != "CodexQuotaTray.exe" ||
            plan.Target.StartsWith(WorkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            plan.Candidate != Path.Combine(directory, "CodexQuotaTray.exe") ||
            !Regex.IsMatch(plan.Sha256, "^[A-Fa-f0-9]{64}$") ||
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

    private static Process Start(UpdatePlan plan, string? readyEvent, bool skipCheck)
    {
        var info = new ProcessStartInfo(plan.Target) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
        foreach (var argument in plan.OriginalArguments) info.ArgumentList.Add(argument);
        if (readyEvent is not null) { info.ArgumentList.Add("--update-ready"); info.ArgumentList.Add(readyEvent); }
        if (skipCheck) info.ArgumentList.Add("--skip-update-check");
        return Process.Start(info) ?? throw new IOException("无法重新启动程序。");
    }

    private static void WriteOutcome(string directory, bool success, string message)
    {
        try { File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { success, message })); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
