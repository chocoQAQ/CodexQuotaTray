using System.Net;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using CodexQuotaTray.Core;
using CodexQuotaTray.Presentation;
using CodexQuotaTray.Infrastructure;
using Microsoft.Win32;
using System.Xml.Linq;
using System.Xml.XPath;

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
            await Test("app-server 多额度桶、分钟窗口和旧响应兼容", () =>
            {
                using var result = JsonDocument.Parse("""
                    {"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
                      "codex":{"primary":{"usedPercent":22,"windowDurationMins":300,"resetsAt":1800009000},
                        "secondary":{"usedPercent":68,"windowDurationMins":10080,"resetsAt":1800302400}},
                      "review":{"limitName":"Code review","primary":{"usedPercent":101,"resetsAt":-1}}}}
                    """);
                var snapshot = AppServerUsageParser.Parse(result.RootElement, Now);
                Equal(3, snapshot.Windows.Count);
                Equal(32d, snapshot.MainWindow!.RemainingPercent);
                Equal(18000d, snapshot.Windows[0].WindowSeconds);
                Equal(0d, snapshot.Windows[2].RemainingPercent);
                Equal(null, snapshot.Windows[2].ResetsAt);
                using var legacy = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":25}}}""");
                Equal(75d, AppServerUsageParser.Parse(legacy.RootElement, Now).MainWindow!.RemainingPercent);
                return Task.CompletedTask;
            });
            await Test("app-server 握手只查询账号和额度，忽略通知并拒绝宿主请求", async () =>
            {
                var input = AppServerFixture(extra: """
                    {"method":"account/updated","params":{}}
                    {"id":"host-request","method":"account/chatgptAuthTokens/refresh","params":{}}
                    """);
                using var output = new StringWriter();
                var result = await new AppServerProtocol(new StringReader(input), output).ReadQuotaAsync(default);
                Equal(75d, result.MainWindow!.RemainingPercent);
                var messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => JsonDocument.Parse(x)).ToArray();
                try
                {
                    var methods = messages.Where(x => x.RootElement.TryGetProperty("method", out _))
                        .Select(x => x.RootElement.GetProperty("method").GetString()).ToArray();
                    Equal("initialize,initialized,account/read,account/rateLimits/read,account/read", string.Join(',', methods));
                    Check(messages.Any(x => x.RootElement.TryGetProperty("error", out var error) && error.GetProperty("code").GetInt32() == -32601));
                    Check(!output.ToString().Contains("access_token"));
                }
                finally { foreach (var message in messages) message.Dispose(); }
            });
            await Test("app-server 换号、未登录及异常响应不展示旧账号数据", async () =>
            {
                foreach (var input in new[] { AppServerFixture(changed: true),
                    """{"id":1,"result":{}}\n{"id":2,"result":{"account":null}}\n""".Replace("\\n", "\n"),
                    """{"id":1,"error":{"message":"private-response"}}""" + "\n", "" })
                {
                    using var output = new StringWriter();
                    var error = await Throws<QuotaException>(() => new AppServerProtocol(new StringReader(input), output).ReadQuotaAsync(default));
                    Check(error.ClearSnapshot && !error.Message.Contains("private-response"));
                }
            });
            await Test("app-server 限制响应体并支持取消", async () =>
            {
                using var output = new StringWriter();
                await Throws<QuotaException>(() => new AppServerProtocol(new StringReader(new string('x', 1024 * 1024 + 1)), output).ReadQuotaAsync(default));
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => new AppServerProtocol(new StringReader(AppServerFixture()), output).ReadQuotaAsync(cancel.Token));
            });
            await Test("数据来源偏好持久化，损坏设置回退默认值", () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-settings-" + Guid.NewGuid());
                var path = Path.Combine(directory, "settings.json");
                var store = new SettingsStore(path);
                try
                {
                    Equal(QuotaSourceKind.AuthFile, store.Load().Source);
                    Check(store.Load().CheckForUpdatesOnStartup);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(path, """{"Source":"AppServer"}""");
                    Equal(QuotaSourceKind.AppServer, store.Load().Source);
                    Check(store.Load().CheckForUpdatesOnStartup); // 兼容缺少新字段的旧设置。
                    store.Save(store.Load() with { CheckForUpdatesOnStartup = false });
                    Check(!store.Load().CheckForUpdatesOnStartup);
                    Equal(QuotaSourceKind.AppServer, store.Load().Source);
                    store.Save(store.Load() with { Source = QuotaSourceKind.AuthFile });
                    Check(!store.Load().CheckForUpdatesOnStartup); // 切换来源不重置更新偏好。
                    store.Save(new AppSettings(QuotaSourceKind.AppServer));
                    Equal(QuotaSourceKind.AppServer, store.Load().Source);
                    Check(store.Load().CheckForUpdatesOnStartup);
                    File.WriteAllText(path, "{broken");
                    Equal(QuotaSourceKind.AuthFile, store.Load().Source);
                    File.WriteAllText(path, """{"Source":99}""");
                    Equal(QuotaSourceKind.AuthFile, store.Load().Source);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                return Task.CompletedTask;
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
            await Test("Codex 启动事件只匹配桌面启动，排除辅助进程和其他应用", () =>
            {
                bool Matches(string app, string message, int id = 201, string provider = "Microsoft-Windows-AppModel-Runtime")
                {
                    var eventXml = new XElement("Event",
                        new XElement("System", new XElement("Provider", new XAttribute("Name", provider)), new XElement("EventID", id)),
                        new XElement("EventData", new XElement("Data", new XAttribute("Name", "ApplicationName"), app),
                            new XElement("Data", new XAttribute("Name", "Message"), message)));
                    return new XDocument(eventXml).XPathSelectElement(CodexStartupRegistration.EventFilter) is not null;
                }
                Check(Matches(CodexStartupRegistration.ApplicationId, "[LaunchProcess]"));
                Check(!Matches("OpenAI.Codex_2p2nqsd0c76g0!CodexCoreCommandRunner", "[LaunchProcess]"));
                Check(!Matches(CodexStartupRegistration.ApplicationId, "[FinishPackageActivation]"));
                Check(!Matches("Other.App!App", "[LaunchProcess]"));
                Check(!Matches(CodexStartupRegistration.ApplicationId, "[LaunchProcess]", 202));
                Check(!Matches(CodexStartupRegistration.ApplicationId, "[LaunchProcess]", provider: "Other"));
                return Task.CompletedTask;
            });
            await Test("任务 XML 使用当前用户交互令牌，正确引用路径且没有周期触发", () =>
            {
                const string executable = @"C:\测试 & apps\CodexQuotaTray.exe";
                const string home = "C:\\测试 & home\\末尾\\";
                const string sid = "S-1-5-21-100-200-300-1001";
                var xml = XDocument.Parse(CodexStartupRegistration.BuildTaskXml(executable, ["--codex-home", home], sid));
                XNamespace ns = xml.Root!.Name.Namespace;
                string Value(string name) => xml.Descendants(ns + name).Single().Value;
                Equal(executable, Value("Command"));
                Equal(sid, Value("UserId"));
                Equal("InteractiveToken", Value("LogonType"));
                Equal("LeastPrivilege", Value("RunLevel"));
                Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
                Equal("PT0S", Value("ExecutionTimeLimit"));
                Equal("false", Value("WakeToRun"));
                Equal("false", Value("DisallowStartIfOnBatteries"));
                Equal("false", Value("StopIfGoingOnBatteries"));
                Equal("EventTrigger", xml.Descendants(ns + "Triggers").Single().Elements().Single().Name.LocalName);
                var query = XDocument.Parse(Value("Subscription")).Descendants("Select").Single();
                Equal(CodexStartupRegistration.EventLog, query.Attribute("Path")!.Value);
                Equal(CodexStartupRegistration.EventFilter, query.Value);
                Equal("Event/EventData/Data[@Name='ProcessID']", Value("Value"));
                var args = ProcessParametersReader.SplitArguments("app.exe " + Value("Arguments").Replace("$(CodexProcessId)", "1234"));
                Equal("--codex-home", args[1]);
                Equal(home, args[2]);
                Equal("--codex-started", args[3]);
                Equal("1234", args[4]);
                return Task.CompletedTask;
            });
            await Test("事件启动拒绝无效 PID 和普通进程", () =>
            {
                Check(!CodexStartupProcess.IsCurrentUserDesktop(0));
                Check(!CodexStartupProcess.IsCurrentUserDesktop(-1));
                Check(!CodexStartupProcess.IsCurrentUserDesktop(int.MaxValue));
                Check(!CodexStartupProcess.IsCurrentUserDesktop(Environment.ProcessId));
                return Task.CompletedTask;
            });
            await Test("跟随退出：未见桌面进程或查询不完整时保留圆环", async () =>
            {
                var reads = 0;
                var monitor = new CodexExitMonitor(readDesktop: () => { reads++; return new(null, true); });
                Check(!await monitor.WaitForExitAsync(default));
                Equal(1, reads);
                var uncertain = new CodexExitMonitor(true, () => new(null, false), _ => throw new Exception("不应进入退出宽限期"));
                Check(!await uncertain.WaitForExitAsync(default));
                var confirmed = new CodexExitMonitor(true, () => new(null, true), _ => Task.CompletedTask);
                Check(await confirmed.WaitForExitAsync(default));
                confirmed.Reset();
                Check(!await confirmed.WaitForExitAsync(default));
            });
            await Test("跟随退出：等待真实进程通知，最后一个实例退出才结束", async () =>
            {
                using var first = await StartExitFixtureAsync();
                using var second = await StartExitFixtureAsync();
                try
                {
                    var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var secondAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var reads = 0;
                    var monitor = new CodexExitMonitor(readDesktop: () =>
                    {
                        var count = Interlocked.Increment(ref reads);
                        if (count == 1)
                        {
                            var snapshot = new CodexDesktopSnapshot(Process.GetProcessById(first.Id), true);
                            attached.SetResult();
                            return snapshot;
                        }
                        if (count == 2)
                        {
                            var snapshot = new CodexDesktopSnapshot(Process.GetProcessById(second.Id), true);
                            secondAttached.SetResult();
                            return snapshot;
                        }
                        return new(null, true);
                    }, restartGrace: _ => Task.CompletedTask);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var waiting = monitor.WaitForExitAsync(timeout.Token);
                    await attached.Task.WaitAsync(timeout.Token);
                    Check(!waiting.IsCompleted);
                    Equal(1, Volatile.Read(ref reads));
                    await first.StandardInput.WriteLineAsync();
                    await secondAttached.Task.WaitAsync(timeout.Token);
                    Check(!waiting.IsCompleted);
                    await second.StandardInput.WriteLineAsync();
                    Check(await waiting);
                    Equal(4, reads);
                }
                finally { await StopExitFixtureAsync(first); await StopExitFixtureAsync(second); }
            });
            await Test("跟随退出：取消不结束宿主，宽限期重启会继续等待", async () =>
            {
                using var child = await StartExitFixtureAsync();
                try
                {
                    var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var resumedAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var reads = 0;
                    var monitor = new CodexExitMonitor(true, () =>
                    {
                        var count = Interlocked.Increment(ref reads);
                        if (count == 1) return new(null, true);
                        var snapshot = child.HasExited ? new CodexDesktopSnapshot(null, true)
                            : new CodexDesktopSnapshot(Process.GetProcessById(child.Id), true);
                        attached.TrySetResult();
                        if (count == 3) resumedAttached.SetResult();
                        return snapshot;
                    }, _ => Task.CompletedTask);
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var waiting = monitor.WaitForExitAsync(cancel.Token);
                    await attached.Task.WaitAsync(cancel.Token);
                    Check(!waiting.IsCompleted);
                    cancel.Cancel();
                    await Throws<OperationCanceledException>(() => waiting);
                    Check(!child.HasExited);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var resumed = monitor.WaitForExitAsync(timeout.Token);
                    await resumedAttached.Task.WaitAsync(timeout.Token);
                    await child.StandardInput.WriteLineAsync();
                    Check(await resumed);
                }
                finally { await StopExitFixtureAsync(child); }
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

    private static async Task<Process> StartExitFixtureAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--process-fixture");
        var process = Process.Start(start)!;
        try
        {
            Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            return process;
        }
        catch { await StopExitFixtureAsync(process); process.Dispose(); throw; }
    }

    private static async Task StopExitFixtureAsync(Process process)
    {
        if (process.HasExited) return;
        await process.StandardInput.WriteLineAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
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
    private static string AppServerFixture(bool changed = false, string extra = "") =>
        "{\"id\":1,\"result\":{}}\n" + extra + "\n" +
        "{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"test@example.invalid\"}}}\n" +
        "{\"id\":3,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":25}}}}\n" +
        "{\"id\":4,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"" +
        (changed ? "different@example.invalid" : "test@example.invalid") + "\"}}}\n";
    private static string ReleasePayload(string tag) => JsonSerializer.Serialize(new {
        tag_name = tag, draft = false, prerelease = false, html_url = "https://example.com/untrusted" });
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
