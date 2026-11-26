using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace CodexQuotaTray.Infrastructure;

/// <summary>让系统已有的事件日志和任务计划服务负责触发；本程序不常驻监听。</summary>
public sealed class CodexStartupRegistration(string executable, IReadOnlyList<string> arguments)
{
    public const string ApplicationId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    public const string EventLog = "Microsoft-Windows-AppModel-Runtime/Admin";
    public const string EventFilter = "*[System[Provider[@Name='Microsoft-Windows-AppModel-Runtime'] and EventID=201] and " +
        "EventData[Data[@Name='ApplicationName']='" + ApplicationId + "' and Data[@Name='Message']='[LaunchProcess]']]";
    private static string UserSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    private static string TaskName => "CodexQuotaTray.FollowCodex." + UserSid;

    public bool IsEnabled()
    {
        using var com = new ComObjects();
        dynamic folder = OpenFolder(com);
        try
        {
            dynamic task = com.Keep((object)folder.GetTask(TaskName));
            return (bool)task.Enabled;
        }
        catch (Exception error) when (IsMissingTask(error)) { return false; }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // 不开启全局进程审计，也不擅自启用被系统/组织关闭的日志。
            using var log = new EventLogConfiguration(EventLog);
            if (!log.IsEnabled) throw new InvalidOperationException("Codex 启动事件日志未启用。");
        }
        using var com = new ComObjects();
        dynamic folder = OpenFolder(com);
        if (enabled)
        {
            // 6 = 创建或更新；3 = 当前用户已登录的交互令牌，不存密码、不请求提权。
            com.Keep((object)folder.RegisterTask(TaskName, BuildTaskXml(executable, arguments, UserSid),
                6, UserSid, null, 3, null));
        }
        else
        {
            try { folder.DeleteTask(TaskName, 0); }
            catch (Exception error) when (IsMissingTask(error)) { }
        }
    }

    public static string BuildTaskXml(string executable, IReadOnlyList<string> arguments, string userSid)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\0'))
            throw new ArgumentException("任务程序必须使用有效的绝对路径。");
        _ = new SecurityIdentifier(userSid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] content) => new(ns + name, content);
        var subscription = new XElement("QueryList", new XElement("Query", new XAttribute("Id", "0"),
            new XElement("Select", new XAttribute("Path", EventLog), EventFilter)));
        // PID 取 EventData 而非 System/Execution，后者是记录日志的系统进程。
        var actionArguments = string.Join(" ", arguments.Select(StartupRegistration.QuoteArgument)
            .Append("--codex-started $(CodexProcessId)"));
        return E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "CodexQuotaTray：随 Codex 桌面端启动（测试）")),
            E("Triggers", E("EventTrigger", E("Enabled", true), E("Subscription", subscription.ToString()),
                E("ValueQueries", E("Value", new XAttribute("name", "CodexProcessId"),
                    "Event/EventData/Data[@Name='ProcessID']")))),
            E("Principals", E("Principal", new XAttribute("id", "CurrentUser"), E("UserId", userSid),
                E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("AllowHardTerminate", false), E("StartWhenAvailable", false),
                E("RunOnlyIfNetworkAvailable", false), E("IdleSettings", E("StopOnIdleEnd", false), E("RestartOnIdle", false)),
                E("AllowStartOnDemand", false), E("Enabled", true), E("Hidden", false),
                E("RunOnlyIfIdle", false), E("WakeToRun", false), E("ExecutionTimeLimit", "PT0S"), E("Priority", 7)),
            E("Actions", new XAttribute("Context", "CurrentUser"), E("Exec", E("Command", executable),
                E("Arguments", actionArguments), E("WorkingDirectory", Path.GetDirectoryName(executable)!))))
            .ToString();
    }

    private static object OpenFolder(ComObjects com)
    {
        dynamic service = com.Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!);
        service.Connect();
        return com.Keep((object)service.GetFolder(@"\"));
    }

    // .NET 可能把 COM 的 ERROR_FILE_NOT_FOUND 映射为 FileNotFoundException。
    private static bool IsMissingTask(Exception error) =>
        error is COMException or FileNotFoundException && error.HResult == unchecked((int)0x80070002);

    // COM 对象在同一个工作线程创建、使用和释放，不让 RCW 长期占用任务服务连接。
    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> objects = [];
        public object Keep(object value) { objects.Add(value); return value; }
        public void Dispose()
        {
            for (var i = objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(objects[i])) Marshal.ReleaseComObject(objects[i]);
        }
    }
}
