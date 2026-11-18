using CodexQuotaTray.Core;

namespace CodexQuotaTray.Presentation;

internal sealed class DetailsForm : Form
{
    private readonly MaterialTheme theme = MaterialTheme.Current();
    private readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
    private readonly FlowLayoutPanel quotas = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Margin = new Padding(0, 12, 0, 8) };
    private readonly Font heading = new("Microsoft YaHei UI", 18, FontStyle.Bold);
    private readonly Font regular = new("Microsoft YaHei UI", 10);
    private readonly Font metric = new("Microsoft YaHei UI", 22, FontStyle.Bold);
    private readonly Label status;
    private readonly Label sourceHelp;
    private readonly ComboBox source = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        AccessibleName = "额度数据来源", Dock = DockStyle.Top };
    private readonly Panel sourceRow = new() { Height = 44, Padding = new Padding(0, 4, 0, 8), Margin = Padding.Empty };
    private readonly CheckBox startup = new() { Text = "开机自启", AutoSize = true,
        Margin = new Padding(0, 12, 0, 12), AutoCheck = false };
    private readonly MaterialButton refresh;
    private QuotaSnapshot? shownSnapshot;
    private bool shownStale;
    private bool changingSettings;
    private bool settingsBusy;

    public event Action? RefreshRequested;
    public event Action<QuotaSourceKind>? SourceRequested;
    public event Action? StartupRequested;
    public event Action? RepositoryRequested;
    public event Action? TaskbarSettingsRequested;

    public DetailsForm()
    {
        Text = "CodexQuotaTray";
        Font = regular;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 720);
        MinimumSize = new Size(470, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = theme.Surface;
        ForeColor = theme.OnSurface;
        using (var icon = RingRenderer.CreateIcon(null, null, true)) Icon = (Icon)icon.Clone();
        Controls.Add(body);
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(Label("Codex 额度", theme.OnSurface, heading), 0, 0);
        refresh = new(theme) { Text = "刷新", Size = new Size(104, 42), Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat, AccessibleName = "立即刷新额度" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        header.Controls.Add(refresh, 1, 0);
        body.Controls.Add(header);
        status = Label("正在读取…", theme.OnSurfaceVariant);
        body.Controls.Add(status);
        body.Controls.Add(quotas);
        body.Controls.Add(Label("数据来源", theme.OnSurface));
        source.Items.AddRange(["auth.json · 官方接口", "Codex app-server"]);
        source.BackColor = theme.Container;
        source.ForeColor = theme.OnSurface;
        source.SelectedIndexChanged += (_, _) =>
        {
            if (!changingSettings && source.SelectedIndex >= 0) SourceRequested?.Invoke((QuotaSourceKind)source.SelectedIndex);
        };
        // 给原生下拉框独立布局行，避免它的固定高度与 FlowLayout 的测量规则冲突。
        sourceRow.Controls.Add(source);
        body.Controls.Add(sourceRow);
        sourceHelp = Label("", theme.OnSurfaceVariant);
        body.Controls.Add(sourceHelp);
        startup.Click += (_, _) => StartupRequested?.Invoke();
        body.Controls.Add(startup);
        body.Controls.Add(Link("托盘显示设置", () => TaskbarSettingsRequested?.Invoke()));
        body.Controls.Add(Label($"v{typeof(DetailsForm).Assembly.GetName().Version}", theme.OnSurfaceVariant,
            margin: new Padding(0, 20, 0, 4)));
        body.Controls.Add(Link("github.com/chocoQAQ/CodexQuotaTray", () => RepositoryRequested?.Invoke()));
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        body.SizeChanged += (_, _) => ResizeRows();
        ResizeRows();
    }

    public void SetSettings(QuotaSourceKind kind, bool startupEnabled, bool busy, bool startupAvailable = true)
    {
        changingSettings = true;
        settingsBusy = busy;
        source.SelectedIndex = (int)kind;
        source.Enabled = !busy;
        startup.Checked = startupEnabled;
        startup.Enabled = !busy && startupAvailable;
        sourceHelp.Text = kind == QuotaSourceKind.AuthFile
            ? "读取本机 auth.json，直接查询官方接口。"
            : "由本机 Codex 查询额度，需可共享的登录状态。";
        changingSettings = false;
    }

    public void Render(QuotaState state, bool refreshing)
    {
        var stale = state.IsStale(DateTimeOffset.UtcNow);
        status.ForeColor = state.IsError ? theme.Error : theme.OnSurfaceVariant;
        status.Text = refreshing ? "正在刷新…" : state.IsError || state.Snapshot is null ? state.Message :
            $"{(stale ? "上次数据 · " : "")}{state.Snapshot.UpdatedAt.ToLocalTime():HH:mm:ss} 更新";
        refresh.Enabled = !refreshing && !settingsBusy;
        refresh.Text = refreshing ? "刷新中…" : "刷新";
        // 不重建设置控件，保留键盘焦点、下拉选择和滚动位置。
        if (ReferenceEquals(shownSnapshot, state.Snapshot) && shownStale == stale) return;
        shownSnapshot = state.Snapshot;
        shownStale = stale;
        quotas.SuspendLayout();
        foreach (var control in quotas.Controls.Cast<Control>().ToArray()) control.Dispose();
        if (state.Snapshot is { } snapshot)
            foreach (var window in snapshot.Windows) quotas.Controls.Add(QuotaRow(window, stale));
        ResizeRows();
        quotas.ResumeLayout(true);
    }

    private Control QuotaRow(QuotaWindow window, bool stale)
    {
        var panel = new TonalPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            SurfaceColor = theme.Container, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12) };
        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top,
            BackColor = theme.Container, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 88));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        var image = new PictureBox { Size = new Size(64, 64), Margin = new Padding(0, 14, 20, 0),
            SizeMode = PictureBoxSizeMode.Zoom, TabStop = false,
            Image = RingRenderer.Draw(window.RemainingPercent, window.TimeRemainingPercent(DateTimeOffset.UtcNow), stale, 96) };
        image.Disposed += (_, _) => image.Image?.Dispose();
        layout.Controls.Add(image, 0, 0);
        var text = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        text.ColumnStyles.Add(new(SizeType.Percent, 100));
        text.Controls.Add(Label(window.Bucket == "codex" ? window.DurationLabel : window.Label + " · " + window.DurationLabel, theme.OnSurfaceVariant));
        text.Controls.Add(Label(QuotaText.Percent(window) + " 剩余", theme.OnSurface, metric));
        text.Controls.Add(Label("重置 " + QuotaText.Reset(window), theme.OnSurfaceVariant));
        text.Controls.Add(Label(QuotaText.Countdown(window, DateTimeOffset.UtcNow), theme.OnSurfaceVariant));
        layout.Controls.Add(text, 1, 0);
        panel.Controls.Add(layout);
        text.SizeChanged += (_, _) =>
        {
            foreach (Control label in text.Controls) label.MaximumSize = new Size(Math.Max(100, text.ClientSize.Width - 6), 0);
        };
        return panel;
    }

    private Label Label(string text, Color color, Font? font = null, Padding? margin = null) => new() {
        Text = text, ForeColor = color, Font = font ?? regular, AutoSize = true, BackColor = Color.Transparent,
        Margin = margin ?? new Padding(0, 2, 0, 4), UseMnemonic = false
    };

    private LinkLabel Link(string text, Action click)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = theme.Primary,
            ActiveLinkColor = theme.Primary, VisitedLinkColor = theme.Primary, LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 4, 0, 8), AccessibleName = text };
        link.LinkClicked += (_, _) => click();
        return link;
    }

    private void ResizeRows()
    {
        var width = Math.Max(250, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (Control control in body.Controls)
        {
            if (control == sourceRow)
            {
                var size = new Size(width, source.PreferredHeight + sourceRow.Padding.Vertical);
                sourceRow.MinimumSize = size;
                sourceRow.MaximumSize = size;
                sourceRow.Size = size;
                continue;
            }
            control.MaximumSize = new Size(width, 0);
            if (control is TableLayoutPanel) control.MinimumSize = new Size(width, 0);
            if (control is Panel) control.Width = width;
        }
        quotas.MinimumSize = new Size(width, 0);
        foreach (Control row in quotas.Controls) { row.MinimumSize = new Size(width, 0); row.MaximumSize = new Size(width, 0); row.Width = width; }
    }

    protected override void Dispose(bool disposing)
    {
        var icon = disposing ? Icon : null;
        base.Dispose(disposing);
        if (disposing) { icon?.Dispose(); heading.Dispose(); regular.Dispose(); metric.Dispose(); }
    }
}
