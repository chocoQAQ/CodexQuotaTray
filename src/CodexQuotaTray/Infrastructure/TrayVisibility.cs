using System.Security;
using Microsoft.Win32;

namespace CodexQuotaTray.Infrastructure;

internal static class TrayVisibility
{
    /// <summary>
    /// Windows 11 在注册图标后异步生成此项；只提升自己的 GUID/路径对应项。
    /// 这是尽力兼容机制，不是 Windows 的公共 API；旧系统提供设置入口。
    /// 初始化标记让用户后续手动折叠的选择得以保留。
    /// </summary>
    public static bool TrySetDefaultVisible()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (root is null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, true);
                if (key is null) continue;
                var guid = key.GetValue("IconGuid") as string;
                var path = key.GetValue("ExecutablePath") as string;
                var matches = Guid.TryParse(guid, out var value) && value == TrayIcon.Id;
                matches |= path is not null && string.Equals(Environment.ExpandEnvironmentVariables(path),
                    Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
                if (key.GetValue("CodexQuotaTrayInitialized") is not int initialized || initialized != 1)
                {
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    key.SetValue("CodexQuotaTrayInitialized", 1, RegistryValueKind.DWord);
                }
                return true;
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }
        return false;
    }
}
