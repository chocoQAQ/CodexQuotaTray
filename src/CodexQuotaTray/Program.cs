using System.Text.Json;
using CodexQuotaTray.Core;
using CodexQuotaTray.Infrastructure;
using CodexQuotaTray.Presentation;

namespace CodexQuotaTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            string? home = null;
            string? diagnosis = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--codex-home" when i + 1 < args.Length: home = args[++i]; break;
                    case "--diagnose" when i + 1 < args.Length: diagnosis = args[++i]; break;
                    default: throw new ArgumentException("支持的参数：--codex-home <目录>、--diagnose <输出文件>。");
                }
            }
            if (home is not null) home = Path.GetFullPath(home);
            using var singleInstance = new SingleInstance();
            if (!singleInstance.IsFirst)
            {
                SingleInstance.ShowExisting();
                return 0;
            }
            using var http = UsageClient.CreateHttpClient();
            using var sessions = new ActiveCodexSessionSource();
            ICredentialSource credentials = home is null ? new ActiveCredentialSource(sessions) : new FileCredentialSource(home);
            var monitor = new QuotaMonitor(new CodexActivity(), credentials, new UsageClient(http));
            if (diagnosis is not null)
            {
                var state = monitor.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
                // 诊断文件仅含额度和经过筛选的状态，不包含账号、token 或原始响应。
                var source = home is null ? sessions.LastResolved : null;
                File.WriteAllText(Path.GetFullPath(diagnosis), JsonSerializer.Serialize(new
                {
                    state.Snapshot, state.Message, state.IsError,
                    LoginSource = new { Automatic = home is null, ProcessId = source?.ProcessId, Home = home ?? source?.Home }
                },
                    new JsonSerializerOptions { WriteIndented = true }));
                return state.Snapshot is not null && !state.IsError ? 0 : 1;
            }
            using var context = new TrayApplication(monitor);
            Application.Run(context);
            return 0;
        }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "Codex 额度圆环", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }
        catch (Exception)
        {
            MessageBox.Show("程序无法启动，请检查运行环境和文件权限。", "Codex 额度圆环", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
