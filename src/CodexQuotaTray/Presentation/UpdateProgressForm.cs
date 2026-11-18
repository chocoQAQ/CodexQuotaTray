using CodexQuotaTray.Core;

namespace CodexQuotaTray.Presentation;

internal sealed class UpdateProgressForm : Form
{
    private readonly Label status = new() { AutoSize = false, Location = new Point(20, 20), Size = new Size(380, 45) };
    private readonly ProgressBar progress = new() { Location = new Point(20, 70), Size = new Size(380, 22), Style = ProgressBarStyle.Marquee };
    private readonly Button cancel = new() { Text = "取消", Location = new Point(300, 112), Size = new Size(100, 32) };
    public event Action? CancelRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public UpdateProgressForm()
    {
        Text = "Codex 额度圆环 · 正在更新";
        ClientSize = new Size(420, 164);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Controls.AddRange([status, progress, cancel]);
        cancel.Click += (_, _) => RequestCancel();
        FormClosing += (_, args) => { if (!AllowClose) { args.Cancel = true; RequestCancel(); } };
    }

    public void Report(UpdateProgress update)
    {
        if (IsDisposed) return;
        status.Text = update.Message + (update.Total > 0
            ? $"\n{update.Downloaded / 1048576d:0.0} / {update.Total / 1048576d:0.0} MB" : "");
        progress.Style = update.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        if (update.Total > 0) progress.Value = (int)Math.Clamp(update.Downloaded * 100d / update.Total, 0, 100);
    }

    private void RequestCancel()
    {
        cancel.Enabled = false;
        status.Text = "正在取消更新…";
        CancelRequested?.Invoke();
    }
}
