using CodexQuotaTray.Core;

namespace CodexQuotaTray.Presentation;

internal sealed class DetailsForm : Form
{
    private readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
    private readonly Font heading = new("Microsoft YaHei UI", 16, FontStyle.Bold);
    private readonly Font regular = new("Microsoft YaHei UI", 10);
    private readonly Font emphasis = new("Microsoft YaHei UI", 13, FontStyle.Bold);
    public event Action? RefreshRequested;

    public DetailsForm()
    {
        Text = "Codex 额度圆环";
        ClientSize = new Size(520, 580);
        MinimumSize = new Size(480, 400);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(20, 25, 34);
        ForeColor = Color.White;
        Font = regular;
        Controls.Add(body);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
        body.SizeChanged += (_, _) => ResizeRows();
    }

    public void Render(QuotaState state, bool refreshing)
    {
        var now = DateTimeOffset.UtcNow;
        body.SuspendLayout();
        foreach (Control control in body.Controls.Cast<Control>().ToArray()) control.Dispose();
        AddText("Codex 额度圆环", Color.White, heading);
        AddText(refreshing ? "正在更新…" : state.Message, Color.Silver);
        if (state.Snapshot is { } snapshot)
        {
            if (state.IsStale(now)) AddText("以下为上次数据，当前额度尚未确认。", Color.FromArgb(240, 177, 65));
            foreach (var window in snapshot.Windows)
            {
                var card = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown,
                    WrapContents = false, Padding = new Padding(14), Margin = new Padding(0, 4, 0, 10),
                    BackColor = Color.FromArgb(29, 36, 48) };
                var bitmap = RingRenderer.Draw(window.RemainingPercent, window.TimeRemainingPercent(now), state.IsStale(now), 64);
                var image = new PictureBox { Image = bitmap, Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom };
                image.Disposed += (_, _) => bitmap.Dispose();
                card.Controls.Add(image);
                card.Controls.Add(Label(window.Label + " · " + window.DurationLabel, Color.Silver));
                card.Controls.Add(Label("剩余 " + QuotaText.Percent(window), Color.White, emphasis));
                card.Controls.Add(Label("重置 " + QuotaText.Reset(window), Color.FromArgb(255, 211, 122), emphasis));
                card.Controls.Add(Label(QuotaText.Countdown(window, now), Color.White));
                body.Controls.Add(card);
            }
            AddText($"上次更新：{snapshot.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}", Color.Silver);
        }
        AddText("左半环：剩余额度 · 右半环：距离重置的剩余时间\n多个窗口时优先显示 Codex 剩余最少的窗口。", Color.Silver);
        var refresh = new Button { Text = refreshing ? "正在刷新…" : "立即刷新", Enabled = !refreshing,
            AutoSize = true, Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        body.Controls.Add(refresh);
        ResizeRows();
        body.ResumeLayout();
    }

    private void AddText(string text, Color color, Font? font = null) => body.Controls.Add(Label(text, color, font));
    private Label Label(string text, Color color, Font? font = null) => new()
    {
        Text = text, ForeColor = color, Font = font ?? regular, AutoSize = true,
        Margin = new Padding(0, 3, 0, 8), UseMnemonic = false
    };
    private void ResizeRows()
    {
        var width = Math.Max(250, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Control control in body.Controls)
        {
            if (control is Label) control.MaximumSize = new Size(width, 0);
            if (control is not FlowLayoutPanel card) continue;
            card.Width = width;
            card.MinimumSize = new Size(width, 0);
            card.MaximumSize = new Size(width, 0);
            foreach (Control child in card.Controls)
                if (child is Label) child.MaximumSize = new Size(width - card.Padding.Horizontal, 0);
        }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { heading.Dispose(); regular.Dispose(); emphasis.Dispose(); }
    }
}
