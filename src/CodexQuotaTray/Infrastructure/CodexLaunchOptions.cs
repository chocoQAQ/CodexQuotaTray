namespace CodexQuotaTray.Infrastructure;

public sealed record CodexLaunchOptions(bool IsUserSession, bool IsAppServer, string? StorageOverride)
{
    /// <summary>只识别运行模式和存储覆盖项，不保留 prompt、token 或完整命令行。</summary>
    public static CodexLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        string? command = null;
        string? storage = null;
        for (var i = 1; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument == "--") break;
            if (argument.StartsWith("--codex-run-as-", StringComparison.Ordinal))
                return new(false, false, null);
            if (argument is "-c" or "--config")
            {
                if (++i >= arguments.Count) break;
                ReadConfig(arguments[i]);
                continue;
            }
            if (argument.StartsWith("--config=", StringComparison.Ordinal))
            { ReadConfig(argument[9..]); continue; }
            if (argument.StartsWith("-c", StringComparison.Ordinal) && argument.Length > 2)
            { ReadConfig(argument[2..]); continue; }
            if (argument is "-C" or "--cd" or "-m" or "--model" or "-p" or "--profile" or
                "-s" or "--sandbox" or "-a" or "--ask-for-approval" or "--enable" or "--disable" or
                "--listen" or "--add-dir" or "-i" or "--image")
            { i++; continue; }
            if (argument.StartsWith('-')) continue;
            if (command is not null) break; // 子命令后的第一个位置参数可能是用户 prompt。
            command = argument;
            if (command is not ("app-server" or "exec" or "e" or "review" or "resume" or "fork")) break;
        }

        var helper = command is "exec-server" or "mcp-server" or "stdio-to-uds" or "responses-api-proxy" or
            "sandbox" or "windows-sandbox" or "sandbox-setup" or "login" or "logout" or "debug" or
            "completion" or "mcp" or "app" or "update" or "tcp-tunnel";
        return new(!helper, command == "app-server", storage);

        void ReadConfig(string text)
        {
            var separator = text.IndexOf('=');
            if (separator < 0 || text[..separator].Trim().Trim('"', '\'') != "cli_auth_credentials_store") return;
            storage = text[(separator + 1)..].Trim().Trim('"', '\'');
        }
    }
}
