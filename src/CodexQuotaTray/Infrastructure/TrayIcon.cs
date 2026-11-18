using System.Runtime.InteropServices;

namespace CodexQuotaTray.Infrastructure;

/// <summary>固定 GUID 的原生托盘图标，支持 Explorer 重启后重新注册。</summary>
internal sealed class TrayIcon : NativeWindow, IDisposable
{
    public static readonly Guid Id = new("10d7287d-21b2-4cfe-913b-7c7280f535d6");
    private const int CallbackMessage = 0x8001;
    private readonly int taskbarCreated = (int)RegisterWindowMessage("TaskbarCreated");
    private Icon? icon;
    private string tooltip = "Codex 额度圆环";
    public event Action? ShowRequested;
    public event Action? MenuRequested;
    public event Action? Registered;

    public TrayIcon() => CreateHandle(new CreateParams { Caption = SingleInstance.WindowName });

    public void Update(Icon next, string text)
    {
        var previous = icon;
        icon = next;
        tooltip = text.Length > 127 ? text[..127] : text;
        var data = Data();
        if (!ShellNotifyIcon(1, ref data)) Register();
        previous?.Dispose();
    }

    private void Register()
    {
        if (icon is null) return;
        var data = Data();
        if (!ShellNotifyIcon(0, ref data)) return;
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
        Registered?.Invoke();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == taskbarCreated) Register();
        else if (message.Msg == SingleInstance.ShowMessage) ShowRequested?.Invoke();
        else if (message.Msg == CallbackMessage)
        {
            var notification = (int)(message.LParam.ToInt64() & 0xffff);
            if (notification is 0x203 or 0x401) ShowRequested?.Invoke(); // 双击或键盘选中
            else if (notification == 0x7b)
            {
                SetForegroundWindow(Handle);
                MenuRequested?.Invoke();
                PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        base.WndProc(ref message);
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = Handle, Id = 1,
        Flags = 1 | 2 | 4 | 0x20 | 0x80, Callback = CallbackMessage,
        Icon = icon?.Handle ?? IntPtr.Zero, Tip = tooltip, Guid = Id, Info = "", InfoTitle = ""
    };

    public void Dispose()
    {
        var data = Data();
        ShellNotifyIcon(2, ref data);
        DestroyHandle();
        icon?.Dispose();
        icon = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
