using System.Diagnostics;
using System.Security;
using CodexQuotaTray.Core;
using CodexQuotaTray.Infrastructure;

namespace CodexQuotaTray.Presentation;

internal sealed class TrayApplication : ApplicationContext
{
    private QuotaMonitor monitor;
    private readonly Func<QuotaSourceKind, QuotaMonitor> createMonitor;
    private readonly SettingsStore settings;
    private AppSettings preferences;
    private readonly StartupRegistration startup;
    private readonly CodexStartupRegistration codexStartup;
    private bool codexStartupEnabled;
    private bool codexStartupBusy;
    private Task? codexStartupTask;
    private readonly IUpdateClient updates;
    private readonly Version installedVersion;
    private readonly TrayIcon tray = new();
    private readonly DetailsForm details = new();
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem refreshItem = new("刷新");
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = (int)QuotaMonitor.RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer visibilityTimer = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource updateCancellation = new();
    private CancellationTokenSource? refreshCancellation;
    private bool refreshing;
    private bool closing;
    private bool changingSource;
    private int visibilityAttempts;
    private Task? refreshTask;
    private Task? updateTask;
    private Task? settingsTask;

    public TrayApplication(QuotaMonitor monitor, Func<QuotaSourceKind, QuotaMonitor> createMonitor,
        SettingsStore settings, AppSettings preferences, StartupRegistration startup,
        CodexStartupRegistration codexStartup, IUpdateClient updates,
        Version installedVersion, bool checkOnStartup)
    {
        this.monitor = monitor;
        this.createMonitor = createMonitor;
        this.settings = settings;
        this.preferences = preferences;
        this.startup = startup;
        this.codexStartup = codexStartup;
        this.updates = updates;
        this.installedVersion = installedVersion;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        menu.Items.Add("打开", null, (_, _) => ShowDetails());
        menu.Items.Add(refreshItem);
        refreshItem.Click += (_, _) => StartRefresh();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray.ShowRequested += ShowDetails;
        tray.MenuRequested += () => menu.Show(Cursor.Position);
        tray.Registered += () => { visibilityAttempts = 0; visibilityTimer.Start(); };
        details.RefreshRequested += StartRefresh;
        details.StartupRequested += ToggleStartup;
        details.UpdateCheckRequested += ToggleUpdateCheck;
        details.CodexStartupRequested += () =>
        {
            if (!closing && !codexStartupBusy) codexStartupTask = UpdateCodexStartupAsync(!codexStartupEnabled);
        };
        details.SourceRequested += kind => { if (!closing && !changingSource) settingsTask = ChangeSourceAsync(kind); };
        details.RepositoryRequested += () => OpenLink("https://github.com/chocoQAQ/CodexQuotaTray");
        details.TaskbarSettingsRequested += () => OpenLink("ms-settings:taskbar");
        refreshTimer.Tick += (_, _) => StartRefresh();
        visibilityTimer.Tick += (_, _) =>
        {
            if (TrayVisibility.TrySetDefaultVisible() || ++visibilityAttempts >= 10) visibilityTimer.Stop();
        };
        UpdateSettings();
        Render();
        refreshTimer.Start();
        StartRefresh();
        // 首次空闲即注销事件：每次真正启动仅检查一次，刷新额度和打开窗口都不触发检查。
        if (checkOnStartup && preferences.CheckForUpdatesOnStartup) Application.Idle += CheckUpdatesOnStartup;
    }

    private void CheckUpdatesOnStartup(object? sender, EventArgs args)
    {
        Application.Idle -= CheckUpdatesOnStartup;
        if (!closing && preferences.CheckForUpdatesOnStartup) updateTask = CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var result = await Task.Run(() => updates.CheckAsync(installedVersion, updateCancellation.Token));
            if (closing || updateCancellation.IsCancellationRequested || !preferences.CheckForUpdatesOnStartup ||
                result.Status != UpdateCheckStatus.Available || result.Update is not { } update) return;
            var answer = MessageBox.Show(
                $"发现新版本 v{update.Version}\n当前版本 v{installedVersion}\n\n是否打开 GitHub 发行页面？",
                "CodexQuotaTray · 发现更新", MessageBoxButtons.YesNo,
                MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes && !closing) OpenLink(update.ReleasePage.AbsoluteUri);
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
        catch (Exception) { /* 网络失败不打扰用户，下次启动再检查。 */ }
    }

    private async Task ChangeSourceAsync(QuotaSourceKind kind)
    {
        if (kind == preferences.Source) return;
        changingSource = true;
        UpdateSettings();
        Render();
        try
        {
            var next = preferences with { Source = kind };
            await Task.Run(() => settings.Save(next));
            refreshCancellation?.Cancel();
            if (refreshTask is not null) await refreshTask;
            if (closing) return;
            // 完全结束旧查询，再换用全新状态容器，旧账号/旧来源数据不会混入。
            preferences = next;
            monitor = createMonitor(kind);
        }
        catch (Exception)
        {
            if (!closing) MessageBox.Show("无法保存数据来源，请检查当前用户的设置目录权限。",
                "CodexQuotaTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            changingSource = false;
            if (!closing) { UpdateSettings(); Render(); StartRefresh(); }
        }
    }

    private void StartRefresh()
    {
        if (closing || refreshing || changingSource) return;
        refreshTask = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        refreshing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        refreshCancellation = cancellation;
        Render();
        try { await Task.Run(() => monitor.RefreshAsync(cancellation.Token)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { /* 原始异常不进入界面，避免泄漏账号、路径或响应。 */ }
        finally
        {
            refreshCancellation = null;
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
        refreshItem.Enabled = !refreshing && !changingSource;
        if (details.Visible) details.Render(state, refreshing);
    }

    private void ShowDetails()
    {
        if (closing) return;
        UpdateSettings();
        details.Render(monitor.State, refreshing);
        details.Show();
        details.WindowState = FormWindowState.Normal;
        details.Activate();
        // 仅打开设置时读取任务状态，正常额度轮询不连接任务计划服务。
        if (!codexStartupBusy) codexStartupTask = UpdateCodexStartupAsync(null);
    }

    private async Task UpdateCodexStartupAsync(bool? enabled)
    {
        codexStartupBusy = true;
        details.SetCodexStartup(codexStartupEnabled, true, true);
        var available = false;
        try
        {
            codexStartupEnabled = await Task.Run(() =>
            {
                if (enabled is { } value) codexStartup.SetEnabled(value);
                return codexStartup.IsEnabled();
            });
            available = true;
        }
        catch (Exception)
        {
            if (!closing && enabled is not null)
                MessageBox.Show("无法更改随 Codex 启动设置。请检查任务计划服务、事件日志和当前用户权限。\n" +
                    "此功能不会自动启用日志或请求管理员权限。重新打开窗口可重试。",
                    "CodexQuotaTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            codexStartupBusy = false;
            if (!closing) details.SetCodexStartup(codexStartupEnabled, false, available);
        }
    }

    private static void OpenLink(string link)
    {
        try { using var process = Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("无法打开链接，请检查默认浏览器或 Windows 设置。\n" + link,
                "CodexQuotaTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateSettings()
    {
        try { details.SetSettings(preferences.Source, startup.IsEnabled(), preferences.CheckForUpdatesOnStartup, changingSource); }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        { details.SetSettings(preferences.Source, false, preferences.CheckForUpdatesOnStartup, changingSource, false); }
    }

    private void ToggleUpdateCheck()
    {
        if (closing || changingSource) return;
        try
        {
            var next = preferences with { CheckForUpdatesOnStartup = !preferences.CheckForUpdatesOnStartup };
            // 写入成功后才改变开关；与来源切换互斥，避免两个偏好保存相互覆盖。
            settings.Save(next);
            preferences = next;
            if (!next.CheckForUpdatesOnStartup)
            {
                Application.Idle -= CheckUpdatesOnStartup;
                updateCancellation.Cancel();
            }
            // 开启不立即联网、不重新注册 Idle；仍然只在下次启动检查一次。
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            MessageBox.Show("无法保存更新检查设置，请检查当前用户的设置目录权限。",
                "CodexQuotaTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateSettings();
    }

    private void ToggleStartup()
    {
        try { startup.SetEnabled(!startup.IsEnabled()); }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            MessageBox.Show("无法更改开机自启设置，请检查权限或缩短程序路径后重试。",
                "CodexQuotaTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateSettings();
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        Application.Idle -= CheckUpdatesOnStartup;
        refreshTimer.Stop();
        visibilityTimer.Stop();
        lifetime.Cancel();
        updateCancellation.Cancel();
        tray.Dispose();
        details.Dispose();
        menu.Dispose();
        refreshTimer.Dispose();
        visibilityTimer.Dispose();
        // UI 退出不同步阻塞等待异步任务，避免死锁。
        var pending = Task.WhenAll(new[] { refreshTask, updateTask, settingsTask, codexStartupTask }.OfType<Task>());
        if (!pending.IsCompleted) _ = pending.ContinueWith(_ => { lifetime.Dispose(); updateCancellation.Dispose(); }, TaskScheduler.Default);
        else { lifetime.Dispose(); updateCancellation.Dispose(); }
        base.ExitThreadCore();
    }
}
