using System.Net;
using System.Text;
using System.Diagnostics;
using CodexQuotaTray.Core;
using CodexQuotaTray.Presentation;
using CodexQuotaTray.Infrastructure;

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
        try
        {
            await Test("目标进程参数读取及环境白名单", async () =>
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                start.ArgumentList.Add("--process-fixture");
                start.ArgumentList.Add("value with spaces");
                start.Environment["CODEX_HOME"] = Path.GetFullPath("artifacts/verification");
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
                    Equal(Path.GetFullPath("artifacts/verification"), ActiveCodexSessionSource.ResolveHome(context));
                }
                finally
                {
                    await child.StandardInput.WriteLineAsync();
                    if (!child.WaitForExit(5000)) child.Kill();
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
