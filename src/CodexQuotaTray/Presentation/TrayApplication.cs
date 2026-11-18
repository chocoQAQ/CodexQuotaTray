using System.Diagnostics;
using System.Security;
using CodexQuotaTray.Core;
using CodexQuotaTray.Infrastructure;

namespace CodexQuotaTray.Presentation;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly QuotaMonitor monitor;
    private readonly StartupRegistration startup;
    private readonly IUpdateClient updates;
    private readonly Version installedVersion;
    private readonly UpdateInstaller installer;
    private readonly TrayIcon tray = new();
    private readonly DetailsForm details = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem statusItem = new("正在读取额度…");
    private readonly ToolStripMenuItem refreshItem = new("立即刷新");
    private readonly ToolStripMenuItem startupItem = new("开机自启");
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = (int)QuotaMonitor.RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer visibilityTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private bool refreshing;
    private bool closing;
    private int visibilityAttempts;
    private Task? refreshTask;
    private Task? updateTask;
    private UpdateProgressForm? updateProgress;

    public TrayApplication(QuotaMonitor monitor, StartupRegistration startup, IUpdateClient updates,
        Version installedVersion, UpdateInstaller installer, bool checkOnStartup)
    {
        this.monitor = monitor;
        this.startup = startup;
        this.updates = updates;
        this.installedVersion = installedVersion;
        this.installer = installer;
        // 明确安装 WinForms 同步上下文，await 返回后只在 UI 线程更新窗口和图标。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        menu.Items.Add(statusItem);
        statusItem.Click += (_, _) => ShowDetails();
        menu.Items.Add("查看额度和重置时间", null, (_, _) => ShowDetails());
        menu.Items.Add(refreshItem);
        refreshItem.Click += (_, _) => StartRefresh();
        menu.Items.Add("托盘显示设置", null, (_, _) => OpenTaskbarSettings());
        menu.Items.Add(startupItem);
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Opening += (_, _) => UpdateStartupMenu();
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
        // 等消息循环启动后检查一次，网络等待不会阻塞托盘初始化。
        if (checkOnStartup) Application.Idle += CheckUpdatesOnStartup;
    }

    private void CheckUpdatesOnStartup(object? sender, EventArgs args)
    {
        Application.Idle -= CheckUpdatesOnStartup;
        if (!closing) updateTask = CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var result = await Task.Run(() => updates.CheckAsync(installedVersion, lifetime.Token));
            if (closing) return;
            if (result.Update is { } update && result.Status == UpdateCheckStatus.Available)
            {
                var answer = MessageBox.Show(
                    $"发现新版本 v{update.Version}\n当前版本 v{installedVersion}\n\n是否立即下载并安装更新？\n安装完成后程序将自动重启。",
                    "Codex 额度圆环 · 发现更新", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                if (answer == DialogResult.Yes && !closing) await InstallUpdateAsync(update);
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception) { /* 启动检查失败不打扰用户，下次启动再检查。 */ }
    }

    private async Task InstallUpdateAsync(AvailableUpdate update)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var window = new UpdateProgressForm();
        updateProgress = window;
        window.CancelRequested += () => cancellation.Cancel();
        string? directory = null;
        var launched = false;
        try
        {
            directory = installer.CreateWorkDirectory();
            window.Show();
            using var http = UpdateDownloader.CreateHttpClient();
            var progress = new Progress<UpdateProgress>(window.Report);
            var prepared = await Task.Run(() => new UpdateDownloader(http).PrepareAsync(update, directory, progress, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            installer.Launch(prepared, update);
            launched = true;
            ExitThread();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closing) MessageBox.Show("自动更新未完成，当前版本仍可继续使用。请检查网络、磁盘空间及程序目录的写入权限，稍后重新启动程序再试。",
                "Codex 额度圆环 · 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updateProgress = null;
            window.AllowClose = true;
            if (!window.IsDisposed) window.Close();
            if (!launched && directory is not null) UpdateInstaller.DeleteWorkDirectory(directory);
        }
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

    private void UpdateStartupMenu()
    {
        try
        {
            startupItem.Checked = startup.IsEnabled();
            startupItem.Enabled = true;
            startupItem.ToolTipText = "登录 Windows 后在托盘启动；取消勾选可关闭。";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            startupItem.Enabled = false;
            startupItem.ToolTipText = "当前用户无法读取开机自启设置。";
        }
    }

    private void ToggleStartup()
    {
        try
        {
            // 不使用 CheckOnClick：仅在写入成功后显示勾选，避免失败时界面与实际状态不符。
            startup.SetEnabled(!startup.IsEnabled());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            MessageBox.Show("无法更改开机自启设置，请检查当前用户权限，或缩短程序路径后重试。",
                "Codex 额度圆环", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStartupMenu();
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        Application.Idle -= CheckUpdatesOnStartup;
        refreshTimer.Stop();
        visibilityTimer.Stop();
        lifetime.Cancel();
        if (updateProgress is not null) { updateProgress.AllowClose = true; updateProgress.Dispose(); }
        tray.Dispose();
        details.Dispose();
        menu.Dispose();
        refreshTimer.Dispose();
        visibilityTimer.Dispose();
        // 此处不能同步等待捕获 UI 上下文的任务，否则退出时会死锁。
        var pending = Task.WhenAll(new[] { refreshTask, updateTask }.OfType<Task>());
        if (!pending.IsCompleted)
            _ = pending.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        else lifetime.Dispose();
        base.ExitThreadCore();
    }
}
