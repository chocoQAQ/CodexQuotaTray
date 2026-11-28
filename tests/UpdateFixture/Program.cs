using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTray.Core;
using CodexQuotaTray.Infrastructure;

// 专用发布夹具：不创建托盘、不访问账号或网络，只验证真实跨进程更新协议。
if (args.Length == 2 && args[0] == "--apply-update")
    return UpdateInstaller.Apply(args[1], message =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1])!, "failure.txt"), message));

var readyIndex = Array.IndexOf(args, "--update-ready");
if (readyIndex >= 0)
{
    var version = Assembly.GetExecutingAssembly().GetName().Version!;
    if (version.Revision == 3) return 72; // 故意模拟新版启动失败。
    var home = args[Array.IndexOf(args, "--codex-home") + 1];
    File.WriteAllText(Path.Combine(home, "restarted.json"), JsonSerializer.Serialize(new
    {
        version = version.ToString(), variant = UpdateInstaller.BuildVariant,
        skipCheck = args.Contains("--skip-update-check"), follow = args.Contains("--follow-session")
    }));
    UpdateInstaller.SignalReady(args[readyIndex + 1]);
    return 0;
}

if (args.Length != 2 || args[0] != "--stage") return 2;
var executable = Environment.ProcessPath!;
var targetDirectory = Path.GetDirectoryName(executable)!;
var installer = new UpdateInstaller(executable, ["--codex-home", targetDirectory]);
var directory = installer.CreateWorkDirectory();
File.WriteAllText(Path.Combine(targetDirectory, "work.txt"), directory);
var candidate = Path.Combine(directory, "CodexQuotaTray.exe");
File.Copy(args[1], candidate);
var info = FileVersionInfo.GetVersionInfo(candidate);
var candidateVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
using var stream = File.OpenRead(candidate);
var prepared = new PreparedUpdate(candidate, Convert.ToHexString(SHA256.HashData(stream)));
var unused = new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases");
var update = new AvailableUpdate(candidateVersion, unused, "fixture.zip", unused, unused, 1);
await installer.LaunchAsync(prepared, update, true, default);
if (!UpdateInstaller.IsApplying()) throw new InvalidOperationException("更新器未持有启动门锁。");
return 0; // 更新器必须等此进程退出后才能替换目标 EXE。
