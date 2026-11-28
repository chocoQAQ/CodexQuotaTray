using CodexQuotaTray.Core;

namespace CodexQuotaTray.Presentation;

internal sealed class UpdateProgressForm : Form
{
    private readonly Label status = new() { AutoSize = false, Location = new Point(20, 20), Size = new Size(380, 45) };
    private readonly ProgressBar progress = new() { Location = new Point(20, 70), Size = new Size(380, 22), Style = ProgressBarStyle.Marquee };
    private readonly Button cancel;
    public event Action? CancelRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public UpdateProgressForm()
    {
        var theme = MaterialTheme.Current();
        cancel = new MaterialButton(theme) { Text = "取消", Location = new Point(300, 112), Size = new Size(100, 36) };
        Font = new Font("Microsoft YaHei UI", 10);
        var font = Font;
        Disposed += (_, _) => font.Dispose();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = theme.Surface;
        ForeColor = theme.OnSurface;
        Text = "Codex 额度圆环 · 正在更新";
        ClientSize = new Size(420, 164);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Controls.AddRange([status, progress, cancel]);
        cancel.Click += (_, _) => RequestCancel();
        FormClosing += (_, args) => { if (!AllowClose) { args.Cancel = true; RequestCancel(); } };
    }

    private bool installing;
    public void BeginInstall()
    {
        installing = true;
        cancel.Enabled = false;
        status.Text = "正在准备替换并重启…";
        progress.Style = ProgressBarStyle.Marquee;
    }

    public void Report(UpdateProgress update)
    {
        if (IsDisposed || installing || !cancel.Enabled) return;
        status.Text = update.Message + (update.Total > 0
            ? $"\n{update.Downloaded / 1048576d:0.0} / {update.Total / 1048576d:0.0} MB" : "");
        progress.Style = update.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (update.Total > 0) progress.Value = (int)Math.Clamp(update.Downloaded * 100d / update.Total, 0, 100);
    }

    private void RequestCancel()
    {
        if (installing) return;
        cancel.Enabled = false;
        status.Text = "正在取消更新…";
        CancelRequested?.Invoke();
    }
}
