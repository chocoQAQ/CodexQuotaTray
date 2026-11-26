using System.Text;
using Microsoft.Win32;

namespace CodexQuotaTray.Infrastructure;

/// <summary>由用户显式开启的登录自启；只操作当前用户、当前程序的一个 Run 值。</summary>
public sealed class StartupRegistration(string executable, IReadOnlyList<string> arguments,
    string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    private const string ValueName = "CodexQuotaTray";
    private readonly string command = BuildCommand(executable, arguments);

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            // Windows Run 启动命令的文档上限为 260 字符，超长时不能静默注册失败。
            if (command.Length > 260) throw new ArgumentException("自启命令过长，请缩短程序或登录目录的路径。");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }

    private static string BuildCommand(string executable, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\0'))
            throw new ArgumentException("自启程序必须使用有效的绝对路径。");
        return string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument));
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("自启参数包含无效字符。");
        // 按 Windows argv 规则引用每个参数：正确处理空格、引号及末尾反斜线。
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
