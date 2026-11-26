# 架构与实现

C# / .NET 10 / WinForms，无第三方 NuGet 依赖。Core 负责额度协议和查询状态，Windows 项目负责进程识别、设置及托盘界面。

## 数据流程

```text
Program → SingleInstance → TrayApplication
                              ↓ 启动 / 每 60 秒 / 手动刷新
                         QuotaMonitor
                              ↓
                       IQuotaSource
                    ↙               ↘
        AuthFileQuotaSource     AppServerQuotaSource
                ↓                     ↓
           官方 HTTP 接口       Codex stdio JSON-RPC
                    ↘               ↙
                        QuotaSnapshot
                              ↓
                       托盘圆环 / 主窗口
```

## 自动识别实例

`ProcessParametersReader` 只读同一用户进程的 PEB / RTL_USER_PROCESS_PARAMETERS，环境信息只保留 `CODEX_HOME`，命令行用于判断运行模式和 `cli_auth_credentials_store` 覆盖项。不会注入进程、扫描堆或启用调试特权。

`ActiveCodexSessionSource` 枚举当前 Windows 会话中的 `codex.exe`，排除已知辅助进程。`ForegroundHistory` 记录最近操作的窗口，选择顺序为：最近操作的 Codex → 仍存活的上次实例 → 桌面 app-server → 其他用户会话。同一优先级存在不同登录目录时提示歧义，不猜测账号。

目录取目标进程的绝对 `CODEX_HOME`，未设置时使用目标用户目录下的 `.codex`，再解析为真实路径。读取权限不足会报错。Windows 内部结构可能随系统变化；目前支持 win-x64 和 WOW64 参数读取，不支持 WSL、远程进程或相对 CODEX_HOME。

## auth.json · 官方接口

`AuthFileQuotaSource` 通过 `FileCredentialSource` 读取对应目录的 `auth.json`，取得 `tokens.access_token` 和 `tokens.account_id`。`UsageClient` 只向固定官方地址发送 GET：

```http
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <access_token>
ChatGPT-Account-Id: <account_id>
```

禁止重定向、不保存 Cookie，解压后响应限制为 1 MiB。此模式不刷新或写入登录凭据，不调用兑换额度接口。查询前后重新定位实例并读取凭据，用内存指纹检查切换；不支持 keyring / auto、API Key 或仅内存登录。

## Codex app-server

`AppServerQuotaSource` 定位选定进程使用的原生 Codex 可执行文件，每次刷新创建短时 `app-server --listen stdio://` 子进程，指定相同的登录目录及已识别的存储覆盖项。不会接管桌面应用已有的私有管道。

`AppServerProtocol` 经标准输入输出交换逐行 JSON：`initialize` → `initialized` → `account/read` → `account/rateLimits/read` → `account/read`。不创建聊天、运行模型或执行工具。前后账号信息只用于内存中的一致性比较，不记录或展示账号。依据 [OpenAI 官方 app-server 协议](https://developers.openai.com/codex/app-server) 实现。

由 Codex 处理文件或安全存储登录及它自身的令牌刷新，托盘程序不直接读取此模式的令牌。独立子进程无法共享宿主仅内存的登录；不自动回退到其他账号。原进程的其他临时配置不复制，WSL 和远程实例不支持。

RPC 响应总量限制为 1 Mi 字符，stderr 只排空不保存，宿主请求明确拒绝。查询完成后关闭 stdin，等待 500 毫秒后只终止自己创建的进程。取消或退出时立即终止查询子进程，无常驻 app-server。

## 刷新、偏好与界面

`QuotaMonitor` 对两种来源统一实施 25 秒总超时、并发保护和查询前后身份检查。普通 HTTP 网络错误保留并标灰上次数据；身份不明或 app-server 错误清空数据。超过 150 秒未更新视为过期，不按本地时间推定额度已恢复。

`UsageParser` 和 `AppServerUsageParser` 将各自字段转换成统一的 `QuotaWindow`。剩余额度为 `100 - used_percent`；时间比例为剩余时间除以窗口时长，二者限制在 0–100%。主圆环优先展示 Codex 桶中剩余最少的窗口，左右半环始终对应同一个窗口。

数据来源保存在 `%LOCALAPPDATA%\CodexQuotaTray\settings.json`，只含偏好、不含凭据。切换时保存选择，取消并等待旧请求结束，再建立全新监控器，防止旧响应覆盖新选择。

界面参考 Material 3 的语义配色、圆角表面、字号层级和按钮状态，继续使用轻量 WinForms。启动时读取系统明暗主题，兼容系统高对比度配色。主窗口可缩放、纵向滚动；设置控件不会随额度刷新重建。原生下拉框、复选框、链接保留键盘和辅助功能行为。

右键菜单仅有打开、刷新、退出。主窗口提供数据来源、开机自启、托盘显示设置与 GitHub 链接。`RingRenderer` 保留原有图标外观；`TrayIcon` 使用固定 GUID 注册图标，Explorer 重启后重建。`TrayVisibility` 仅尝试提升本程序图标可见性，Windows 最终显示状态由系统和用户设置决定。

`SingleInstance` 使用当前用户 SID 的命名互斥锁。重复启动只唤起已有窗口，不重复查询更新。

## 开机自启

主窗口“开机自启”默认关闭。`StartupRegistration` 只操作 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 中本程序的值，保留显式 `--codex-home`，正确引用带空格或引号的参数。写入成功后才勾选，不申请管理员权限。移动程序前先关闭，移动后再开启。

## 随 Codex 桌面端启动（测试）

默认关闭，与开机自启独立。`CodexStartupRegistration` 使用 Windows 任务计划 COM 接口，在当前用户 SID 对应的任务中订阅 `Microsoft-Windows-AppModel-Runtime/Admin` 事件 201，限定 `ApplicationName=OpenAI.Codex_2p2nqsd0c76g0!App`、`Message=[LaunchProcess]`，排除命令执行辅助进程。任务直接执行圆环 EXE，并通过 `ValueQueries` 传入事件的目标 PID。

`CodexStartupProcess` 以有限查询权限核对目标进程的应用 ID、用户和会话。目标退出、不可读或不匹配时静默结束；已有圆环实例时不弹窗、不重复检查更新。首次启动仍遵守“启动时检查更新”开关。

没有额外监听进程、轮询任务或 PowerShell 脚本；托盘未运行时，触发工作交给 Windows 已有服务。任务仅在用户已登录时运行，不保存密码、不要求最高权限、不唤醒电脑、不受电池状态或默认三天执行时限限制。程序启动和打开窗口时异步读取任务状态，点击开关时注册/删除任务；决定自动退出前再确认任务仍启用，正常每分钟刷新不访问任务服务。

仅支持当前已验证的打包版 Codex 桌面端，不支持纯 CLI 或普通 ChatGPT。依赖上游包标识、事件格式和系统日志/任务服务；不自动启用日志或进程审计，权限不足会提示失败。v1.5.1 起选项名称为“随 Codex 桌面端启动和退出（测试）”。关闭选项删除任务并取消跟随退出，不立即退出圆环。移动程序后需重新开关注册。方案比较和验收步骤见 [架构与代价](architecture-and-tradeoffs.md#91-随-codex-桌面端启动测试)。

### 跟随退出

`CodexExitMonitor` 在当前用户、当前会话中筛选桌面应用进程，选取最早启动的一个，通过 `Process.WaitForExitAsync` 等待退出通知。结束后重新枚举：有其他实例则继续等待；全部退出后留出 2 秒宽限期，复查仍为空才通过原有 `ExitThread` 路径退出。会取消额度和更新请求、清理托盘以及本程序自己的 app-server 查询子进程，不终止 Codex 宿主。

不增加周期定时器、监听进程或阻塞等待线程。无进程可绑定或查询失败时，复用已有的一分钟刷新再次尝试；因此手动/登录启动圆环后才启动 Codex，首次绑定最多可能等待约一分钟。手动打开圆环且尚未观察到 Codex 时保留窗口供设置；由任务启动则已有明确的桌面启动记录。关闭跟随选项会取消等待并清除观察状态。

只关闭窗口但 Codex 仍在后台时不会退出；纯 CLI 进程不阻止桌面跟随退出。权限不足、进程身份不明或任务状态读取失败时保留圆环。已经启用旧版跟随启动任务的用户升级后即可使用，无需重新注册任务（程序路径须保持不变）。

## 启动检查更新

主窗口提供“启动时检查更新”开关，默认开启，保存在 settings.json 的 `CheckForUpdatesOnStartup` 中。旧设置缺少该字段时保持开启。关闭后不发起启动检查，并取消尚未完成的请求；重新开启从下次启动生效，不立即联网。

开启时，首个 `Application.Idle` 后匿名请求本仓库 GitHub Latest Release，并立即注销事件，每次正常启动只检查一次。诊断、重复唤起已有实例不检查；无定时检查或手动检查按钮。

`UpdateClient` 使用独立 HttpClient，不发送 Codex 凭据。超时 10 秒，响应上限 256 KiB，只接受非草稿、非预发布的数字版本，按三段/四段数字比较。

发现新版弹出“是/否”：是打开对应 GitHub Release 页面，否关闭弹窗。地址由固定仓库和验证后的数字标签构造，不使用响应中的任意链接。网络失败静默跳过，下次正常启动再检查。

没有下载、覆盖、安装或重启模块。仅保留已发布 v1.3.0.2 更新器需要的启动确认参数，方便旧版交接到当前版本。

## 构建与自动发布

`build.ps1 -Publish` 执行离线检查并生成两个 win-x64 单文件 ZIP：内置运行时版和无运行时版。后者需要 .NET 10 Desktop Runtime x64。ZIP 只包含 EXE 与 README。

`.github/workflows/release.yml` 对 main 的 push 使用 `paths-ignore`：全部改动都位于根目录 `README.md` 或 `docs/**` 时跳过；包含其他路径时照常构建。手动触发仍可发布。

流水线为 Windows 构建和检查 → 两个 ZIP 与 SHA-256 校验文件 → 上传草稿 → 附件齐全后公开为 Latest。版本为项目三段版本加 Actions 构建序号。工作流固定官方 Action 提交，只授予发布 job 写权限。发布前若 main 已变化，会检查后续差异；仅有文档变更时继续发布已构建的代码，包含代码变更时跳过。比较结果达到 300 个文件或无法确认时保守停止，可再手动触发。

离线检查使用模拟账号和协议数据，覆盖额度解析、进程识别、切换竞态、取消、匿名更新检查及图标回归，不读取真实账号。实际桌面交互与登录兼容性由用户验收。

## 诊断

关闭已有圆环后，运行 `CodexQuotaTray.exe --diagnose diagnosis.json` 可执行一次当前数据来源的查询。报告包含额度、状态与识别出的 PID/目录，不含账号 ID、令牌或原始响应；本机目录与额度信息仍不应提交到公开仓库。
