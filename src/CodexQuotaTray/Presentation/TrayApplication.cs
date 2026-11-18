using System.Diagnostics;
using CodexQuotaTray.Core;
using CodexQuotaTray.Infrastructure;

namespace CodexQuotaTray.Presentation;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly QuotaMonitor monitor;
    private readonly TrayIcon tray = new();
    private readonly DetailsForm details = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem statusItem = new("正在读取额度…");
    private readonly ToolStripMenuItem refreshItem = new("立即刷新");
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = (int)QuotaMonitor.RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer visibilityTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool refreshing;
    private bool closing;
    private int visibilityAttempts;
    private Task? refreshTask;

    public TrayApplication(QuotaMonitor monitor)
    {
        this.monitor = monitor;
        // 明确安装 WinForms 同步上下文，await 返回后只在 UI 线程更新窗口和图标。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        menu.Items.Add(statusItem);
        statusItem.Click += (_, _) => ShowDetails();
        menu.Items.Add("查看额度和重置时间", null, (_, _) => ShowDetails());
        menu.Items.Add(refreshItem);
        refreshItem.Click += (_, _) => StartRefresh();
        menu.Items.Add("托盘显示设置", null, (_, _) => OpenTaskbarSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray.ShowRequested += ShowDetails;
        tray.MenuRequested += () => menu.Show(Cursor.Position);
        tray.Registered += () => { visibilityAttempts = 0; visibilityTimer.Start(); };
        details.RefreshRequested += StartRefresh;
        refreshTimer.Tick += (_, _) => StartRefresh();
        visibilityTimer.Tick += (_, _) =>
        {
            if (TrayVisibility.TrySetDefaultVisible() || ++visibilityAttempts >= 10)
                visibilityTimer.Stop();
        };
        Render();
        refreshTimer.Start();
        StartRefresh();
    }

    private void StartRefresh()
    {
        if (closing || refreshing) return;
        refreshTask = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        refreshing = true;
        Render();
        try
        {
            // 进程枚举和文件 I/O 也放到后台，保持右键菜单和退出操作响应。
            await Task.Run(() => monitor.RefreshAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception)
        {
            // 未预期异常也不输出响应体、凭据或系统路径。
            if (!closing) statusItem.Text = "刷新失败，请重试或重新启动圆环。";
        }
        finally
        {
            refreshing = false;
            if (!closing) Render();
        }
    }

    private void Render()
    {
        var state = monitor.State;
        var now = DateTimeOffset.UtcNow;
        var main = state.Snapshot?.MainWindow;
        var stale = state.IsStale(now);
        var suffix = stale ? "（上次数据）" : "";
        var tooltip = main is null ? state.Message :
            $"Codex 剩余 {QuotaText.Percent(main)}{suffix}\n重置 {QuotaText.Reset(main)}\n{QuotaText.Countdown(main, now)}";
        tray.Update(RingRenderer.CreateIcon(main?.RemainingPercent, main?.TimeRemainingPercent(now), stale), tooltip);
        statusItem.Text = main is null ? state.Message : $"剩余 {QuotaText.Percent(main)}{suffix} · 重置 {QuotaText.Reset(main)}";
        refreshItem.Enabled = !refreshing;
        if (details.Visible) details.Render(state, refreshing);
    }

    private void ShowDetails()
    {
        if (closing) return;
        details.Render(monitor.State, refreshing);
        details.Show();
        details.WindowState = FormWindowState.Normal;
        details.Activate();
    }

    private static void OpenTaskbarSettings()
    {
        try { using var process = Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("请打开 Windows 设置 → 个性化 → 任务栏，设置圆环为始终显示。", "Codex 额度圆环");
        }
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        refreshTimer.Stop();
        visibilityTimer.Stop();
        lifetime.Cancel();
        tray.Dispose();
        details.Dispose();
        menu.Dispose();
        refreshTimer.Dispose();
        visibilityTimer.Dispose();
        // 此处不能同步等待捕获 UI 上下文的任务，否则退出时会死锁。
        if (refreshTask is { IsCompleted: false })
            _ = refreshTask.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        else lifetime.Dispose();
        base.ExitThreadCore();
    }
}
