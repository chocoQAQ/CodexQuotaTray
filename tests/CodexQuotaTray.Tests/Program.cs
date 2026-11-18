using System.Net;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using CodexQuotaTray.Core;
using CodexQuotaTray.Presentation;
using CodexQuotaTray.Infrastructure;
using Microsoft.Win32;

namespace CodexQuotaTray.Tests;

internal static class Program
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private const string Usage = """
        {"plan_type":"plus","rate_limit":{
          "primary_window":{"used_percent":22,"limit_window_seconds":18000,"reset_at":1800009000},
          "secondary_window":{"used_percent":68,"limit_window_seconds":604800,"reset_at":1800302400}},
         "additional_rate_limits":[{"metered_feature":"review","limit_name":"Code review","rate_limit":{
          "primary_window":{"used_percent":99,"limit_window_seconds":604800,"reset_at":1800302400}}}]}
        """;
    private static readonly CodexCredentials Auth = new("fake-test-token", "test-account");
    private static readonly QuotaSnapshot Snapshot = UsageParser.Parse(Usage, Now);
    private static int passed;

    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--process-fixture")
        {
            Console.WriteLine("ready");
            Console.ReadLine();
            return 0;
        }
        // 显式调用时才联网，默认测试全部离线且不读取真实账号。
        if (args.FirstOrDefault() == "--check-update-live")
        {
            using var http = UpdateClient.CreateHttpClient();
            var result = await new UpdateClient(http).CheckAsync(new Version(0, 0, 0, 0), default);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status == UpdateCheckStatus.Unavailable ? 1 : 0;
        }
        try
        {
            await Test("自动更新匹配当前运行时版本，拒绝缺失文件和外部下载地址", async () =>
            {
                using var noRuntime = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, ReleasePayload("v9.0.0", "no-runtime")))));
                var result = await new UpdateClient(noRuntime, "no-runtime").CheckAsync(new Version(1, 0, 0), default);
                Equal(UpdateCheckStatus.Available, result.Status);
                Check(result.Update!.ArchiveName.EndsWith("-no-runtime.zip", StringComparison.Ordinal));
                Equal(UpdateCheckStatus.Unavailable, (await new UpdateClient(noRuntime).CheckAsync(new Version(1, 0, 0), default)).Status);
                foreach (var body in new[] {
                    ReleasePayload("v9.0.0", archiveUrl: "https://example.com/program.zip"),
                    """{"tag_name":"v9.0.0","draft":false,"prerelease":false,"assets":[]}"""
                })
                {
                    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body))));
                    Equal(UpdateCheckStatus.Unavailable, (await new UpdateClient(http).CheckAsync(new Version(1, 0, 0), default)).Status);
                }
            });
            await Test("下载、校验 ZIP 并仅提取程序到指定目录", async () =>
            {
                var bytes = TestArchive();
                var update = FixtureUpdate(bytes.Length);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                using var http = new HttpClient(new Handler((request, _) =>
                {
                    Check(request.Headers.Authorization is null && !request.Headers.Contains("ChatGPT-Account-Id"));
                    return Task.FromResult(request.RequestUri == update.ChecksumsUrl
                        ? Response(HttpStatusCode.OK, hash + "  " + update.ArchiveName + "\n")
                        : BytesResponse(bytes));
                }));
                var directory = TemporaryDirectory();
                try
                {
                    var prepared = await new UpdateDownloader(http).PrepareAsync(update, directory, null, default);
                    Equal(Path.Combine(directory, "CodexQuotaTray.exe"), prepared.ExecutablePath);
                    Equal("test-executable", await File.ReadAllTextAsync(prepared.ExecutablePath));
                    Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test-executable"))), prepared.Sha256);
                    Check(!File.Exists(Path.Combine(directory, "README.md")));
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("校验错误和下载不完整时绝不提取更新程序", async () =>
            {
                var bytes = TestArchive();
                foreach (var wrongHash in new[] { true, false })
                {
                    var update = FixtureUpdate(bytes.Length + (wrongHash ? 0 : 1));
                    using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri == update.ChecksumsUrl
                        ? Response(HttpStatusCode.OK, new string('0', 64) + "  " + update.ArchiveName + "\n")
                        : BytesResponse(bytes))));
                    var directory = TemporaryDirectory();
                    try
                    {
                        await Throws<InvalidDataException>(() => new UpdateDownloader(http).PrepareAsync(update, directory, null, default));
                        Check(!File.Exists(Path.Combine(directory, "CodexQuotaTray.exe")));
                    }
                    finally { Directory.Delete(directory, true); }
                }
            });
            await Test("更新 ZIP 路径穿越被拒绝", async () =>
            {
                var bytes = TestArchive("../outside.exe");
                var update = FixtureUpdate(bytes.Length);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri == update.ChecksumsUrl
                    ? Response(HttpStatusCode.OK, hash + "  " + update.ArchiveName)
                    : BytesResponse(bytes))));
                var directory = TemporaryDirectory();
                try
                {
                    await Throws<InvalidDataException>(() => new UpdateDownloader(http).PrepareAsync(update, directory, null, default));
                    Check(!File.Exists(Path.Combine(directory, "CodexQuotaTray.exe")));
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("校验文件必须唯一匹配当前安装包", async () =>
            {
                var name = FixtureUpdate(1).ArchiveName;
                var hash = new string('a', 64);
                Equal(hash, UpdateDownloader.ParseChecksum("\uFEFF" + hash + "  " + name + "\r\n", name));
                Equal(hash, UpdateDownloader.ParseChecksum(hash + " *" + name, name));
                foreach (var text in new[] { hash + "  other.zip", "bad-hash  " + name, hash + "  " + name + "\n" + hash + "  " + name })
                    await Throws<InvalidDataException>(() => Task.FromResult(UpdateDownloader.ParseChecksum(text, name)));
            });
            await Test("下载重定向仅允许 GitHub 及其文件 CDN", async () =>
            {
                var calls = 0;
                using var http = new HttpClient(new Handler((_, _) =>
                {
                    calls++;
                    var response = Response(HttpStatusCode.Redirect, "");
                    response.Headers.Location = new Uri("https://example.com/update.zip");
                    return Task.FromResult(response);
                }));
                var directory = TemporaryDirectory();
                try
                {
                    await Throws<InvalidDataException>(() => new UpdateDownloader(http).PrepareAsync(FixtureUpdate(1), directory, null, default));
                    Equal(1, calls);
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("用户取消下载不生成安装程序", async () =>
            {
                using var http = new HttpClient(new Handler(async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return BytesResponse([]);
                }));
                var directory = TemporaryDirectory();
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                    await Throws<OperationCanceledException>(() => new UpdateDownloader(http).PrepareAsync(FixtureUpdate(1), directory, null, cancel.Token));
                    Check(!File.Exists(Path.Combine(directory, "CodexQuotaTray.exe")));
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("更新文件替换可回滚，确认提交后清理备份", async () =>
            {
                var directory = TemporaryDirectory();
                try
                {
                    var target = Path.Combine(directory, "CodexQuotaTray.exe");
                    var candidate = Path.Combine(directory, "new.exe");
                    await File.WriteAllTextAsync(target, "old-program");
                    await File.WriteAllTextAsync(candidate, "new-program");
                    var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidate)));
                    using (var transaction = new UpdateFileTransaction(target, candidate, hash))
                    {
                        transaction.Install();
                        Equal("new-program", await File.ReadAllTextAsync(target));
                        transaction.Rollback();
                        Equal("old-program", await File.ReadAllTextAsync(target));
                    }
                    using (var transaction = new UpdateFileTransaction(target, candidate, hash))
                    {
                        transaction.Install();
                        transaction.Commit();
                    }
                    Equal("new-program", await File.ReadAllTextAsync(target));
                    Check(!Directory.GetFiles(directory).Any(path => path.Contains(".backup-") || path.Contains(".incoming-")));
                    using var corrupt = new UpdateFileTransaction(target, candidate, new string('0', 64));
                    await Throws<InvalidDataException>(() => { corrupt.Install(); return Task.CompletedTask; });
                    Equal("new-program", await File.ReadAllTextAsync(target));
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("更新版本号兼容三/四段、数字顺序和无效标签", () =>
            {
                Check(UpdateClient.TryParseVersion("v1.2.0", out var three));
                Check(UpdateClient.TryParseVersion("1.2.0.0", out var four));
                Equal(three, four);
                Check(UpdateClient.TryParseVersion("V1.2.0.10", out var tenth));
                Check(UpdateClient.TryParseVersion("v1.2.0.9", out var ninth));
                Check(tenth > ninth);
                foreach (var tag in new[] { "latest", "1.2", "1.2.0-beta.1", "v1.2.0+build", "v1.2.0/../../evil",
                    "-1.2.0", "1.2.0.1.1", "999999999999.1.0", new string('1', 100), "" })
                    Check(!UpdateClient.TryParseVersion(tag, out _));
                return Task.CompletedTask;
            });
            await Test("匿名请求本仓库 Latest；下载页不使用响应提供的外部链接", async () =>
            {
                using var http = new HttpClient(new Handler((request, _) =>
                {
                    Equal(HttpMethod.Get, request.Method);
                    Equal(UpdateClient.Endpoint, request.RequestUri);
                    Check(request.Headers.Authorization is null);
                    Check(!request.Headers.Contains("ChatGPT-Account-Id"));
                    Equal("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
                    Check(request.Headers.UserAgent.ToString().StartsWith("CodexQuotaTray/", StringComparison.Ordinal));
                    return Task.FromResult(Response(HttpStatusCode.OK, ReleasePayload("v1.2.0.10")));
                }));
                var result = await new UpdateClient(http).CheckAsync(new Version(1, 2, 0, 9), default);
                Equal(UpdateCheckStatus.Available, result.Status);
                Equal(new Version(1, 2, 0, 10), result.Update!.Version);
                Equal(new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases/tag/v1.2.0.10"), result.Update.ReleasePage);
            });
            await Test("相同版本和旧版本不提示更新，较新版本才提示", async () =>
            {
                foreach (var (tag, current, expected) in new[]
                {
                    ("v1.2.0", new Version(1, 2, 0, 0), UpdateCheckStatus.Current),
                    ("v1.2.0.0", new Version(1, 2, 0), UpdateCheckStatus.Current),
                    ("v1.2.0.9", new Version(1, 2, 0, 10), UpdateCheckStatus.Current),
                    ("v1.2.0.10", new Version(1, 3, 0), UpdateCheckStatus.Current),
                    ("v1.3.0", new Version(1, 2, 99, 99), UpdateCheckStatus.Available)
                })
                {
                    var body = ReleasePayload(tag);
                    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body))));
                    var result = await new UpdateClient(http).CheckAsync(current, default);
                    Equal(expected, result.Status);
                    Equal(expected == UpdateCheckStatus.Available, result.Update is not null);
                }
            });
            await Test("草稿、预发布和异常响应不会触发更新提示", async () =>
            {
                foreach (var body in new[]
                {
                    """{"tag_name":"v99.0.0","draft":true,"prerelease":false}""",
                    """{"tag_name":"v99.0.0","draft":false,"prerelease":true}""",
                    """{"tag_name":"v99.0.0","draft":false}""",
                    """{"tag_name":"latest","draft":false,"prerelease":false}""",
                    """{"tag_name":"v99.0.0-beta.1","draft":false,"prerelease":false}""",
                    """{"tag_name":5,"draft":false,"prerelease":false}""",
                    "{}", "[]", "null", "<html>server-error</html>"
                })
                {
                    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body))));
                    var result = await new UpdateClient(http).CheckAsync(new Version(1, 0, 0), default);
                    Equal(UpdateCheckStatus.Unavailable, result.Status);
                    Check(result.Update is null);
                    Check(!result.Message!.Contains("server-error"));
                }
            });
            await Test("GitHub 404、限流和 HTTP 错误可恢复且不暴露响应", async () =>
            {
                foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden,
                    HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.Redirect })
                {
                    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(status, "private-body"))));
                    var result = await new UpdateClient(http).CheckAsync(new Version(1, 0, 0), default);
                    Equal(UpdateCheckStatus.Unavailable, result.Status);
                    Check(result.Update is null && !result.Message!.Contains("private-body"));
                }
            });
            await Test("更新检查限制响应体，网络失败和超时安静返回", async () =>
            {
                using var large = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new string('x', 256 * 1024 + 1)))));
                Equal(UpdateCheckStatus.Unavailable, (await new UpdateClient(large).CheckAsync(new Version(1, 0, 0), default)).Status);
                using var failed = new HttpClient(new Handler((_, _) => throw new HttpRequestException("private-data")));
                var error = await new UpdateClient(failed).CheckAsync(new Version(1, 0, 0), default);
                Equal(UpdateCheckStatus.Unavailable, error.Status);
                Check(!error.Message!.Contains("private-data"));
                using var timeout = new HttpClient(new Handler((_, _) => throw new TaskCanceledException()));
                Equal(UpdateCheckStatus.Unavailable, (await new UpdateClient(timeout).CheckAsync(new Version(1, 0, 0), default)).Status);
            });
            await Test("退出取消更新请求，下一次检查仍可成功", async () =>
            {
                var calls = 0;
                using var http = new HttpClient(new Handler(async (_, token) =>
                {
                    if (++calls == 1) await Task.Delay(Timeout.Infinite, token);
                    return Response(HttpStatusCode.OK, ReleasePayload("v99.0.0"));
                }));
                var updates = new UpdateClient(http);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Throws<OperationCanceledException>(() => updates.CheckAsync(new Version(1, 0, 0), cancel.Token));
                Equal(UpdateCheckStatus.Available, (await updates.CheckAsync(new Version(1, 0, 0), default)).Status);
            });
            await Test("自启开关默认关闭、保存参数并只删除自身启动项", () =>
            {
                // 使用独立临时注册表键，不修改实际的 Windows 登录启动项。
                var keyPath = @"Software\CodexQuotaTray.Tests\" + Guid.NewGuid();
                try
                {
                    const string executable = @"C:\My Apps\CodexQuotaTray.exe";
                    string[] arguments = ["--codex-home", @"D:\Codex 登录目录\"];
                    var startup = new StartupRegistration(executable, arguments, keyPath);
                    Check(!startup.IsEnabled());
                    startup.SetEnabled(false); // 未开启时关闭也不创建注册表键。
                    using (var missing = Registry.CurrentUser.OpenSubKey(keyPath)) Check(missing is null);
                    using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("OtherApp", "leave-this-value");
                    startup.SetEnabled(true);
                    Check(new StartupRegistration(executable, arguments, keyPath).IsEnabled());
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                    {
                        var command = (string)key.GetValue("CodexQuotaTray")!;
                        Equal(RegistryValueKind.String, key.GetValueKind("CodexQuotaTray"));
                        Check(ProcessParametersReader.SplitArguments(command).SequenceEqual(new[] { executable }.Concat(arguments)));
                    }
                    // 从另一个发布版本关闭时，也能移除共用启动项。
                    var otherBuild = new StartupRegistration(@"D:\no-runtime\CodexQuotaTray.exe", [], keyPath);
                    Check(otherBuild.IsEnabled());
                    otherBuild.SetEnabled(false);
                    Check(!startup.IsEnabled());
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                        Equal("leave-this-value", key.GetValue("OtherApp"));
                }
                finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
                return Task.CompletedTask;
            });
            await Test("自启参数引号正确往返、超长命令保留原设置", async () =>
            {
                var keyPath = @"Software\CodexQuotaTray.Tests\" + Guid.NewGuid();
                try
                {
                    const string executable = @"C:\CodexQuotaTray.exe";
                    string[] arguments = ["", "has spaces", "embedded\"quote", @"trailing slash\", "斜线\\\"引号"];
                    var startup = new StartupRegistration(executable, arguments, keyPath);
                    startup.SetEnabled(true);
                    string original;
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!)
                    {
                        original = (string)key.GetValue("CodexQuotaTray")!;
                        Check(ProcessParametersReader.SplitArguments(original).SequenceEqual(new[] { executable }.Concat(arguments)));
                    }
                    var tooLong = new StartupRegistration(executable, [new string('x', 260)], keyPath);
                    await Throws<ArgumentException>(() => { tooLong.SetEnabled(true); return Task.CompletedTask; });
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath)!) Equal(original, key.GetValue("CodexQuotaTray"));
                    await Throws<ArgumentException>(() => Task.FromResult(new StartupRegistration("relative.exe", [], keyPath)));
                }
                finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
            });
            await Test("目标进程参数读取及环境白名单", async () =>
            {
                // 云端全新检出没有 artifacts/verification，测试必须自行创建目录。
                var homeDirectory = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-process-" + Guid.NewGuid());
                Directory.CreateDirectory(homeDirectory);
                var marker = Guid.NewGuid().ToString();
                var markerName = "process-home-marker.txt";
                await File.WriteAllTextAsync(Path.Combine(homeDirectory, markerName), marker);
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                start.ArgumentList.Add("--process-fixture");
                start.ArgumentList.Add("value with spaces");
                start.Environment["CODEX_HOME"] = homeDirectory;
                start.Environment["QUOTA_TEST_SECRET"] = "must-not-be-retained";
                start.Environment["USERPROFILE"] = @"Z:\not-the-real-profile";
                using var child = Process.Start(start)!;
                try
                {
                    Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                    var context = ProcessParametersReader.Read(child.Id);
                    Equal(start.Environment["CODEX_HOME"], context.HomeEnvironment["CODEX_HOME"]);
                    Equal(1, context.HomeEnvironment.Count);
                    Equal("value with spaces", context.Arguments[2]);
                    Check(context.ProfileDirectory != start.Environment["USERPROFILE"]);
                    Equal(Environment.ProcessId, context.ParentId);
                    // Windows 的临时路径可能使用 8.3 短名，真实路径字符串不一定相同。
                    var resolvedHome = ActiveCodexSessionSource.ResolveHome(context);
                    Check(Path.IsPathFullyQualified(resolvedHome));
                    Equal(marker, await File.ReadAllTextAsync(Path.Combine(resolvedHome, markerName)));
                }
                finally
                {
                    await child.StandardInput.WriteLineAsync();
                    if (!child.WaitForExit(5000)) child.Kill();
                    File.Delete(Path.Combine(homeDirectory, markerName));
                    Directory.Delete(homeDirectory);
                }
            });
            await Test("辅助进程排除及命令行存储覆盖", () =>
            {
                Check(!CodexLaunchOptions.Parse(["codex", "exec-server"]).IsUserSession);
                Check(!CodexLaunchOptions.Parse(["codex", "--codex-run-as-exec-server"]).IsUserSession);
                var server = CodexLaunchOptions.Parse(["codex", "-c", "cli_auth_credentials_store=\"keyring\"", "app-server"]);
                Check(server.IsAppServer && server.IsUserSession);
                Equal("keyring", server.StorageOverride);
                Equal("file", CodexLaunchOptions.Parse(["codex", "app-server", "--config=cli_auth_credentials_store='file'"]).StorageOverride);
                Check(!CodexLaunchOptions.Parse(["codex", "exec", "app-server"]).IsAppServer);
                return Task.CompletedTask;
            });
            await Test("多实例选择：前台、最近实例、桌面服务与歧义", async () =>
            {
                var desktop = new CodexSession("desktop", 1, @"C:\desktop");
                var cli = new CodexSession("cli", 2, @"C:\cli");
                Equal(cli, CodexSessionSelection.Select([new(desktop, false, true), new(cli, true, false)], null));
                Equal(cli, CodexSessionSelection.Select([new(desktop, false, true), new(cli, false, false)], cli.Key));
                Equal(desktop, CodexSessionSelection.Select([new(desktop, false, true), new(cli, false, false)], null));
                await Throws<QuotaException>(() => Task.FromResult(CodexSessionSelection.Select([new(desktop, false, false), new(cli, false, false)], null)));
                await Throws<QuotaException>(() => Task.FromResult(CodexSessionSelection.Select([new(desktop, false, true), new(null, true, false)], null)));
                await Throws<QuotaException>(() => Task.FromResult(CodexSessionSelection.Select([], null)));
                var sameHome = cli with { Home = desktop.Home };
                Equal(desktop, CodexSessionSelection.Select([new(sameHome, false, false), new(desktop, false, false)], null));
            });
            await Test("自动凭据跟随目录切换并拒绝读取期间切换", async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-session-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(directory, "auth.json"), """{"tokens":{"access_token":"fixture","account_id":"fixture-account"}}""");
                    var first = new CodexSession("process-one", 1, directory);
                    var second = first with { Key = "process-two", ProcessId = 2 };
                    var sessions = new Sessions { ResolveNext = () => first };
                    var source = new ActiveCredentialSource(sessions);
                    var original = await source.ReadAsync(default);
                    Equal("fixture-account", original.AccountId);
                    sessions.ResolveNext = () => second;
                    Check(original.Fingerprint != (await source.ReadAsync(default)).Fingerprint);
                    var calls = 0;
                    sessions.ResolveNext = () => ++calls == 1 ? first : second;
                    Check((await Throws<QuotaException>(() => source.ReadAsync(default))).ClearSnapshot);
                    sessions.ResolveNext = () => first with { StorageOverride = "ephemeral" };
                    Check((await Throws<QuotaException>(() => source.ReadAsync(default))).ClearSnapshot);
                    await File.WriteAllTextAsync(Path.Combine(directory, "config.toml"), "cli_auth_credentials_store = 'keyring'");
                    sessions.ResolveNext = () => first with { StorageOverride = "file" };
                    Equal("fixture-account", (await source.ReadAsync(default)).AccountId);
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("解析官方 HTTP 字段及附加额度", () =>
            {
                Equal(3, Snapshot.Windows.Count);
                Equal("plus", Snapshot.Plan);
                Equal(32d, Snapshot.MainWindow!.RemainingPercent);
                Equal("codex", Snapshot.MainWindow.Bucket);
                Equal(50d, Snapshot.MainWindow.TimeRemainingPercent(Now));
                Equal("1 周额度", Snapshot.MainWindow.DurationLabel);
                return Task.CompletedTask;
            });
            await Test("缺失字段、越界百分比和重置时间", () =>
            {
                var data = UsageParser.Parse("""
                    {"rate_limit":{"primary_window":{"used_percent":-20,"reset_after_seconds":60},
                    "secondary_window":{"used_percent":140,"limit_window_seconds":-1,"reset_at":1e99}}}
                    """, Now);
                Equal(100d, data.Windows[0].RemainingPercent);
                Equal(Now.AddSeconds(60), data.Windows[0].ResetsAt);
                Equal(0d, data.Windows[1].RemainingPercent);
                Equal(null, data.Windows[1].ResetsAt);
                Equal(null, data.Windows[1].TimeRemainingPercent(Now));
                Equal(0d, Snapshot.MainWindow!.TimeRemainingPercent(Now.AddDays(20)));
                Equal(100d, Snapshot.MainWindow.TimeRemainingPercent(Now.AddDays(-20)));
                return Task.CompletedTask;
            });
            await Test("无额度、错误 JSON 和非数值不能伪装成功", async () =>
            {
                foreach (var input in new[] { "null", "[]", "<html>", "{}", "{\"rate_limit\":{\"primary_window\":{\"used_percent\":\"NaN\"}}}" })
                    await Throws<QuotaException>(() => Task.FromResult(UsageParser.Parse(input, Now)));
            });
            await Test("GET 地址、账号头和响应解析", async () =>
            {
                using var http = new HttpClient(new Handler((request, _) =>
                {
                    Equal(HttpMethod.Get, request.Method);
                    Equal(UsageClient.Endpoint, request.RequestUri);
                    Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Equal(Auth.AccessToken, request.Headers.Authorization?.Parameter);
                    Equal(Auth.AccountId, request.Headers.GetValues("ChatGPT-Account-Id").Single());
                    Check(!request.Headers.Contains("x-openai-codex-luna-reserve"));
                    Check(request.Content is null);
                    return Task.FromResult(Response(HttpStatusCode.OK, Usage));
                }));
                var result = await new UsageClient(http, new FixedClock()).ReadAsync(Auth, default);
                Equal(Now, result.UpdatedAt);
                Equal(3, result.Windows.Count);
            });
            await Test("HTTP 错误不泄漏响应内容", async () =>
            {
                foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
                    HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.Redirect })
                {
                    using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(code, "secret-response"))));
                    var error = await Throws<QuotaException>(() => new UsageClient(http).ReadAsync(Auth, default));
                    Check(!error.Message.Contains("secret-response"));
                    Equal(code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, error.ClearSnapshot);
                }
            });
            await Test("限制响应体并支持取消", async () =>
            {
                using var large = new HttpClient(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new string('x', 1024 * 1024 + 1)))));
                await Throws<QuotaException>(() => new UsageClient(large).ReadAsync(Auth, default));
                using var slow = new HttpClient(new Handler(async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return Response(HttpStatusCode.OK, Usage);
                }));
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Throws<OperationCanceledException>(() => new UsageClient(slow).ReadAsync(Auth, cancel.Token));
            });
            await Test("每次重读凭据、换号和无效登录", async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-test-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try
                {
                    var source = new FileCredentialSource(directory);
                    var path = Path.Combine(directory, "auth.json");
                    await Throws<QuotaException>(() => source.ReadAsync(default));
                    await File.WriteAllTextAsync(path, """{"tokens":{"access_token":"token-one","account_id":"one"}}""");
                    var first = await source.ReadAsync(default);
                    Equal("token-one", first.AccessToken);
                    Check(!first.ToString().Contains("token-one"));
                    await File.WriteAllTextAsync(path, """{"tokens":{"access_token":"token-two","account_id":"two"}}""");
                    var second = await source.ReadAsync(default);
                    Check(first.Fingerprint != second.Fingerprint);
                    foreach (var input in new[] { "{}", "{", "{\"auth_mode\":\"apikey\"}", "{\"tokens\":{\"access_token\":\"bad\\ntoken\"}}" })
                    {
                        await File.WriteAllTextAsync(path, input);
                        Check((await Throws<QuotaException>(() => source.ReadAsync(default))).ClearSnapshot);
                    }
                    await File.WriteAllTextAsync(path, """{"tokens":{"access_token":"old-account-token"}}""");
                    await File.WriteAllTextAsync(Path.Combine(directory, "config.toml"), "cli_auth_credentials_store = 'keyring'");
                    Check((await Throws<QuotaException>(() => source.ReadAsync(default))).ClearSnapshot);
                }
                finally { Directory.Delete(directory, true); }
            });
            await Test("Codex 未运行时不读取凭据或请求网络", async () =>
            {
                var activity = new Activity { Running = false };
                var source = new Credentials();
                var client = new Client();
                var state = await new QuotaMonitor(activity, source, client).RefreshAsync(default);
                Equal(null, state.Snapshot);
                Equal(0, source.Calls);
                Equal(0, client.Calls);
            });
            await Test("并发刷新只发送一次请求", async () =>
            {
                var pending = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var client = new Client { Read = _ => pending.Task };
                var monitor = new QuotaMonitor(new Activity(), new Credentials(), client);
                var first = monitor.RefreshAsync(default);
                await monitor.RefreshAsync(default);
                Equal(1, client.Calls);
                pending.SetResult(Snapshot);
                Equal(Snapshot, (await first).Snapshot);
                Equal(TimeSpan.FromSeconds(60), QuotaMonitor.RefreshInterval);
            });
            await Test("失败保留灰色旧数据、换号失败清除旧数据", async () =>
            {
                var source = new Credentials();
                var client = new Client();
                var monitor = new QuotaMonitor(new Activity(), source, client);
                await monitor.RefreshAsync(default);
                client.Read = _ => throw new HttpRequestException();
                var failed = await monitor.RefreshAsync(default);
                Check(failed.IsStale(Now));
                Equal(Snapshot, failed.Snapshot);
                source.Value = new CodexCredentials("another-token", "another-account");
                Equal(null, (await monitor.RefreshAsync(default)).Snapshot);
                client.Read = _ => Task.FromResult(Snapshot);
                Check(!(await monitor.RefreshAsync(default)).IsError);
                client.Read = _ => throw new QuotaException("登录过期", true);
                Equal(null, (await monitor.RefreshAsync(default)).Snapshot);
                client.Read = _ => throw new IOException("secret body");
                var ioFailure = await monitor.RefreshAsync(default);
                Check(ioFailure.IsError && !ioFailure.Message.Contains("secret body"));
            });
            await Test("请求途中换号或退出不接受旧响应", async () =>
            {
                var activity = new Activity();
                var source = new Credentials();
                var client = new Client { Read = _ => { source.Value = new CodexCredentials("new", "new"); return Task.FromResult(Snapshot); } };
                var monitor = new QuotaMonitor(activity, source, client);
                Equal(null, (await monitor.RefreshAsync(default)).Snapshot);
                client.Read = _ => { activity.Running = false; return Task.FromResult(Snapshot); };
                Equal(null, (await monitor.RefreshAsync(default)).Snapshot);
            });
            await Test("取消后释放刷新锁；150 秒后数据过期", async () =>
            {
                var client = new Client { Read = async token => { await Task.Delay(Timeout.Infinite, token); return Snapshot; } };
                var monitor = new QuotaMonitor(new Activity(), new Credentials(), client);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Throws<OperationCanceledException>(() => monitor.RefreshAsync(cancel.Token));
                client.Read = _ => Task.FromResult(Snapshot);
                var state = await monitor.RefreshAsync(default);
                Check(!state.IsStale(Now.AddSeconds(150)));
                Check(state.IsStale(Now.AddSeconds(151)));
            });
            await Test("圆环透明背景、颜色阈值和图标资源", () =>
            {
                var directory = Path.GetFullPath("artifacts/verification");
                Directory.CreateDirectory(directory);
                foreach (var (value, name, expected) in new[]
                {
                    (78d, "normal", Color.FromArgb(57, 209, 164)),
                    (25d, "warning", Color.FromArgb(240, 177, 65)),
                    (10d, "critical", Color.FromArgb(239, 92, 98))
                })
                {
                    using var bitmap = RingRenderer.Draw(value, 50, false, 128);
                    Equal(0, (int)bitmap.GetPixel(0, 0).A);
                    Check(Enumerable.Range(0, bitmap.Width).Any(x => Enumerable.Range(0, bitmap.Height)
                        .Any(y => bitmap.GetPixel(x, y).ToArgb() == expected.ToArgb())));
                    bitmap.Save(Path.Combine(directory, name + ".png"));
                }
                using var small = RingRenderer.Draw(45, 25, false, 32);
                small.Save(Path.Combine(directory, "tray-32px.png"));
                using var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "original-tray-32px.png"));
                for (var x = 0; x < 32; x++)
                    for (var y = 0; y < 32; y++)
                        Equal(original.GetPixel(x, y), small.GetPixel(x, y));
                using var stale = RingRenderer.Draw(78, 50, true, 128);
                stale.Save(Path.Combine(directory, "stale.png"));
                for (var i = 0; i < 100; i++)
                {
                    using var icon = RingRenderer.CreateIcon(45, 25, false);
                    Check(icon.Handle != IntPtr.Zero);
                }
                return Task.CompletedTask;
            });
            Console.WriteLine($"PASS: {passed} test groups");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task Test(string name, Func<Task> action)
    {
        await action();
        passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual));
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string text) => new(status)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static string ReleasePayload(string tag, string variant = "self-contained", string? archiveUrl = null)
    {
        var number = tag.TrimStart('v', 'V');
        var name = $"CodexQuotaTray-{number}-win-x64-{variant}.zip";
        var prefix = "https://github.com/chocoQAQ/CodexQuotaTray/releases/download/" + tag + "/";
        return JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false,
            html_url = "https://example.com/untrusted",
            assets = new[] {
                new { name, browser_download_url = archiveUrl ?? prefix + name, size = 500 },
                new { name = "SHA256SUMS.txt", browser_download_url = prefix + "SHA256SUMS.txt", size = 200 }
            } });
    }
    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-update-test-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
    private static AvailableUpdate FixtureUpdate(long size) => new(new Version(9, 0, 0, 1),
        new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases/tag/v9.0.0.1"),
        "CodexQuotaTray-9.0.0.1-win-x64-no-runtime.zip",
        new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases/download/v9.0.0.1/CodexQuotaTray-9.0.0.1-win-x64-no-runtime.zip"),
        new Uri("https://github.com/chocoQAQ/CodexQuotaTray/releases/download/v9.0.0.1/SHA256SUMS.txt"), size);
    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static byte[] TestArchive(string? extra = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var name in new[] { "CodexQuotaTray.exe", "README.md" }.Concat(extra is null ? [] : new[] { extra }))
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name == "CodexQuotaTray.exe" ? "test-executable" : "test-readme");
            }
        }
        return memory.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Activity : ICodexActivity { public bool Running = true; public bool IsRunning() => Running; }
    private sealed class Credentials : ICredentialSource
    {
        public CodexCredentials Value = Auth;
        public int Calls;
        public Task<CodexCredentials> ReadAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(Value); }
    }
    private sealed class Sessions : ICodexSessionSource
    {
        public required Func<CodexSession> ResolveNext;
        public CodexSession Resolve() => ResolveNext();
    }
    private sealed class Client : IUsageClient
    {
        public int Calls;
        public Func<CancellationToken, Task<QuotaSnapshot>> Read = _ => Task.FromResult(Snapshot);
        public Task<QuotaSnapshot> ReadAsync(CodexCredentials credentials, CancellationToken cancellationToken) { Calls++; return Read(cancellationToken); }
    }
}
