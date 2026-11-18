# CodexQuotaTray -- 轻量级 Codex 额度显示托盘程序

<p align="center">
  <img src="docs/assets/ring.png" width="96" height="96" alt="CodexQuotaTray 额度圆环">
</p>

在 Windows 系统托盘查看 Codex 剩余额度与重置进度，自动识别本机账号，每分钟刷新。

- **左半环**：剩余额度，随额度降低从绿变黄、变红。
- **右半环**：距离重置的剩余时间比例；灰色表示数据暂不可用。
- **双击**查看详情，**右键**刷新或退出。单实例，手动启动。

## 使用

需要 Windows 10/11 x64、本机 Codex 已登录 ChatGPT 账号，并使用 `auth.json` 保存凭据。

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，克隆项目后构建：

```powershell
git clone https://github.com/chocoQAQ/CodexQuotaTray.git
cd CodexQuotaTray
.\build.ps1 -Publish -SelfContained
```

运行 `dist/CodexQuotaTray/CodexQuotaTray.exe`。发布版自带运行时，目标电脑无需另装 .NET。

图标被折叠时可拖到常显区域。暂不支持 keyring / auto 安全存储、仅内存凭据及 WSL / 远程实例。

## 开发

C# / .NET 10 / WinForms，无第三方 NuGet 依赖。运行 `.\build.ps1` 编译并测试。

更多说明见 [架构与实现](docs/implementation.md)。本项目为第三方工具，与 OpenAI 无隶属关系。
