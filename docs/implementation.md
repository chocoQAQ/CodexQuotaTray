# 架构与实现

CodexQuotaTray 使用 C#、.NET 10 和 Windows Forms。核心逻辑与 Windows 平台能力分离，由程序入口直接组装服务，不依赖外部容器或后台服务。

## 数据流程

```text
Program → SingleInstance → TrayApplication
                              ↓ 启动 / 每 60 秒 / 手动刷新
                         QuotaMonitor
                              ↓
          进程检测 → 自动选择实例 → 读取对应 auth.json
                              ↓
                         UsageClient
                              ↓
                 UsageParser → QuotaState
                              ↓
                   圆环 / 悬停提示 / 详情窗口
```

## 自动选择登录目录

`ProcessParametersReader` 只读当前用户进程的 PEB / RTL_USER_PROCESS_PARAMETERS，解析启动参数和环境块。环境信息只保留 `CODEX_HOME`，命令行用于判断运行模式和 `cli_auth_credentials_store` 覆盖项。

`ActiveCodexSessionSource` 枚举当前 Windows 会话中的 `codex.exe`，排除已知辅助进程，并结合进程祖先关系识别桌面 app-server。`ForegroundHistory` 监听前台切换事件，在内存中保存最近的 PID，确保打开托盘菜单后仍能识别刚才操作的 Codex。

选择顺序：

1. 最近操作的 Codex 窗口所属实例。
2. 仍在运行的上次选定实例。
3. 桌面应用的 app-server。
4. 其余可识别的用户会话。

同一优先级中，如果候选使用不同登录目录或不同凭据存储方式，则提示歧义，不猜测账号。同目录的候选按 PID 稳定选择。

目录优先取目标进程的 `CODEX_HOME`；未设置时使用目标用户的 Windows Profile 已知文件夹下的 `.codex`，再解析为真实路径。圆环自身的 `CODEX_HOME` 不参与自动选择。相对目录因无法可靠恢复进程启动时的工作目录而拒绝解析。

自动识别不注入进程、不修改进程内存、不扫描堆中的令牌，也不申请调试特权。内部参数布局依赖 Windows 实现，权限不足或结构变化时会报错。win-x64 程序实现了原生 64 位与 WOW64 参数布局的读取；不识别 WSL 和远程进程。

## 凭据与 HTTP

`FileCredentialSource` 读取选定目录的 `auth.json`，获取 `tokens.access_token` 和 `tokens.account_id`。当前支持文件登录，不支持 keyring / auto、API Key 订阅额度或宿主仅注入内存的凭据。

`UsageClient` 复用 `HttpClient`，向固定地址发送请求：

```http
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <access_token>
ChatGPT-Account-Id: <account_id>
```

禁止重定向，不保存 Cookie，解压后的响应体限制为 1 MiB。HTTP 客户端设置 20 秒超时，整个刷新操作设置 25 秒取消期限。只查询额度，不调用额度兑换接口，不刷新或写入登录凭据。

每次请求前后重新解析实例并读取凭据。进程标识、目录、账号 ID 和令牌参与内存中的指纹比较；期间发生切换时丢弃旧响应。

## 刷新与状态

`QuotaMonitor` 通过 `SemaphoreSlim` 保证最多一个查询在途。普通网络错误保留上次数据并标灰；身份错误或账号切换清除旧数据。超过 150 秒没有成功更新的数据判为过期。

`UsageParser` 解析主额度的 `primary_window`、`secondary_window` 和附加额度，统一转换为 `QuotaWindow`。剩余额度为 `100 - used_percent`，剩余时间比例为 `(重置时间 - 当前时间) / 窗口时长 × 100`，都限制在 0–100%。到时后不自行恢复额度。

## Windows 界面

`TrayApplication` 使用 WinForms Timer 每 60 秒触发查询。后台任务完成后回到 UI 线程，更新圆环、提示和可见的详情窗口。

`RingRenderer` 使用 System.Drawing 绘制透明位图，经 `GetHicon()` 转成系统图标。每次替换时释放旧图标和临时句柄。

`TrayIcon` 通过 `Shell_NotifyIconW` 和固定 GUID 管理图标，隐藏消息窗口接收双击、右键和单实例通知。Explorer 重启后根据 `TaskbarCreated` 消息重新注册。

`TrayVisibility` 首次尝试将本程序在 `HKCU\Control Panel\NotifyIconSettings` 下的 `IsPromoted` 设置为 `1`。这是兼容方式，不是保证常显的公共 API；仅修改本程序对应项，初始化后保留用户选择。

`SingleInstance` 使用含用户 SID 的 `Local\` 命名互斥锁。同一用户、同一 Windows 会话重复启动时，新进程向旧实例发送显示详情消息，然后退出。

`StartupRegistration` 提供托盘菜单中的“开机自启”开关，默认关闭。用户勾选后在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入 `CodexQuotaTray`，取消勾选只删除该值。命令使用当前 EXE 的绝对路径，并保留显式指定的 `--codex-home`，按照 Windows 参数引用规则处理空格和反斜线。开关操作无需管理员权限，只有写入成功后菜单才显示勾选。Windows 设置中另外禁用的启动项仍需在系统中启用。

这是用户登录 Windows 后启动托盘程序，不会启动 Codex。移动程序前先关闭开关，移动后再开启以更新路径。两个发布版本共用一个启动项，不能重复注册。退出时停止计时器、取消请求、删除托盘图标并释放窗口资源；自启设置保留至用户主动关闭。没有跟随启动或守护进程。

## 发布版本

`build.ps1 -Publish` 默认生成两个 win-x64 单文件版本及各自的 ZIP：`dist/CodexQuotaTray-self-contained` 包含运行时，`dist/CodexQuotaTray-no-runtime` 不包含运行时。无运行时版本需要目标系统安装 .NET 10 Desktop Runtime x64。使用 `-SelfContained` 或 `-FrameworkDependent` 可以单独发布其中一种。ZIP 只包含本次构建的 EXE 和 README，避免夹带本机诊断或其他遗留文件。

## 诊断

关闭已有圆环后，可以执行一次查询并保存摘要：

```powershell
Start-Process .\dist\CodexQuotaTray-self-contained\CodexQuotaTray.exe `
  -ArgumentList '--diagnose', 'diagnosis.json' -Wait
```

报告包含额度、状态及识别出的 PID/登录目录，不含账号 ID、令牌或原始响应。报告可能包含本机目录和额度信息，请勿直接提交到公开仓库。

## 验证

离线测试由独立控制台项目执行，覆盖进程参数读取、环境筛选、选择歧义、切换竞态、HTTP 行为和图标逐像素回归。进程读取测试仅访问测试自身创建的子进程；账号及接口数据均为测试样本。
