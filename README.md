# CodexQuotaTray -- 轻量级 Codex 额度显示托盘程序

<p align="center">
  <img src="docs/assets/ring.png" width="96" height="96" alt="CodexQuotaTray 额度圆环">
</p>

在 Windows 系统托盘查看 Codex 剩余额度与重置进度，自动识别本机账号，每分钟刷新。

- **左半环**：剩余额度，随额度降低从绿变黄、变红。
- **右半环**：距离重置的剩余时间比例；灰色表示数据暂不可用。

本项目为第三方工具，与 OpenAI 无隶属关系。
## 使用

适用于 windows 10/11 x64

Codex 登录后启动程序，自动识别登录目录。窗口内可选择 `auth.json · 官方接口` 或 `Codex app-server` 查询方式，并记住选择。

从 [Release](https://github.com/chocoQAQ/CodexQuotaTray/releases/latest) 获取最新版

`main` 分支更新后，GitHub Actions 自动测试、编译并发布最新 Release（包含以下两个版本）。

- **内置运行时版**：解压即可运行。
- **无运行时版**：体积更小，需要安装 [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)。

## 构建
需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
git clone https://github.com/chocoQAQ/CodexQuotaTray.git
cd CodexQuotaTray

.\build.ps1 -Publish  # 同时生成两个版本和 ZIP 包
```

仅构建某一版本可添加 `-SelfContained` 或 `-FrameworkDependent`。

输出位于 `dist/CodexQuotaTray-self-contained` 和 `dist/CodexQuotaTray-no-runtime`。

## 开发

C# / .NET 10 / WinForms，无第三方 NuGet 依赖。

更多说明见 [架构与实现](docs/implementation.md)。

## 注
我没想好一些功能细节该怎么做，所以选了这种简单的实现方案，如果有更好的实现方式，欢迎提交 Pull Request。

和GPT大战了好久艹，我也不知为什么他一直在用各种奇怪的实现方式，实在没办法了，太折腾了，先这样吧

所以可能代码里会有很多奇怪的东西，但我做出来主要是给自己用和朋友用的，所以先不考虑那么多了，能用就行

如果我还记着这个项目，我会接着完善它的功能和性能的，前提是在我这里用出问题了，或者好心人提了issues

对，我没测试，准备边用边测