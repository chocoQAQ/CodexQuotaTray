<p align="center">
  <img src="docs/assets/ring.png" width="96" height="96" alt="CodexQuotaTray 额度圆环">
</p>

# CodexQuotaTray

**轻量 Codex 额度托盘显示程序。**

把 Codex 剩余额度和重置进度放进 Windows 系统托盘，抬眼就能看见。自动识别本机运行中的 Codex 登录目录，每分钟刷新，无需手动填写账号或令牌。

基于 C# / .NET 10 / WinForms，单进程运行，无第三方 NuGet 依赖。

## 功能

- **额度圆环**：左侧显示剩余额度，右侧显示距离重置的剩余时间比例。
- **自动识别账号**：根据活跃 Codex 进程定位登录目录，读取已有的文件登录凭据。
- **每分钟刷新**：启动时立即查询，之后每 60 秒更新，也可手动刷新。
- **额度详情**：查看各额度窗口的剩余百分比、重置时间和倒计时。
- **单实例**：重复打开程序会唤起已有窗口，不会生成多个托盘图标。
- **托盘常显**：Windows 11 首次运行尝试设置为常显，之后保留用户自己的折叠选择。

独立软件，手动启动。不安装插件，不注册开机启动项，不运行守护进程。

## 圆环怎么看

| 区域 | 含义 |
| --- | --- |
| 左侧绿色 | 剩余额度大于 25% |
| 左侧黄色 | 剩余额度大于 10%、不超过 25% |
| 左侧红色 | 剩余额度不超过 10% |
| 右侧蓝色 | 距离重置的剩余时间比例，越短越接近重置 |
| 灰色 | 等待读取、查询失败或数据过期 |

多个额度窗口并存时，优先显示 `codex` 分组中剩余最少的窗口。左右半环始终对应同一窗口，完整数据可在详情中查看。到达重置时间后，等待接口确认额度恢复。

## 快速开始

运行环境：**Windows 10/11 x64**，本机 Codex 已使用 ChatGPT 账号登录，并采用 `auth.json` 文件存储凭据。

目前可从源码构建。安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 后执行：

```powershell
git clone https://github.com/chocoQAQ/CodexQuotaTray.git
cd CodexQuotaTray

# 编译、测试并发布包含运行时的便携版
.\build.ps1 -Publish -SelfContained
```

双击 `dist/CodexQuotaTray/CodexQuotaTray.exe` 即可运行。此版本无需在目标电脑上另装 .NET；便携包包含运行时，因此体积会大于仅发布应用的版本。

- 鼠标悬停：查看额度与重置提示。
- 双击图标：打开详情窗口。
- 右键菜单：立即刷新、查看详情、打开托盘显示设置或退出。
- 关闭详情窗口：隐藏窗口，程序继续留在托盘。

如果图标被 Windows 折叠，可从任务栏的隐藏图标区域拖出，或通过“托盘显示设置”调整。不同系统版本及策略可能限制自动常显。

## 自动识别与账号

程序优先选择最近操作的 Codex 窗口所属实例；没有相关前台记录时保留上次实例，首次启动优先使用桌面应用的 app-server。多个不同登录目录无法区分时会提示，不会随意选择账号。

账号凭据来自**目标 Codex 进程对应目录中的 `auth.json`**，额度通过 HTTPS 向官方后端查询。程序不发起模型对话，不修改 Codex 配置，不使用 refresh token，不保存令牌或原始接口响应。

以下情况目前不支持自动识别：

- keyring / auto 安全存储，以及只存在于进程内存中的登录凭据。
- WSL 或远程 Codex 实例。
- 相对路径形式的 `CODEX_HOME`，或没有权限读取的进程。

多个终端标签共享同一个窗口时，也可能无法区分目标实例。必要时可显式指定登录目录：

```powershell
.\dist\CodexQuotaTray\CodexQuotaTray.exe --codex-home "D:\MyCodexHome"
```

## 开发

```powershell
# 编译并运行离线测试
.\build.ps1

# 发布不包含运行时的较小版本；目标电脑需要 .NET 10 Desktop Runtime
.\build.ps1 -Publish
```

```text
src/CodexQuotaTray.Core/   额度模型、HTTP 请求、凭据读取与实例选择
src/CodexQuotaTray/        Windows 托盘、圆环、详情窗口与进程识别
tests/                    离线测试与图标回归素材
docs/                     实现说明与预览素材
```

测试覆盖进程环境读取、多实例选择、账号切换、HTTP 错误、并发刷新、取消、数据过期和圆环绘制。测试使用模拟凭据，不读取真实账号或访问网络。

更多信息见 [架构与实现](docs/implementation.md)。

## 说明

这是第三方工具，与 OpenAI 无隶属关系。额度接口参考 [OpenAI Codex 源码](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs)，后端接口及 Windows 内部实现变化可能影响兼容性。
