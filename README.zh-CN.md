# dsh-sdk-csharp

**面向官方 DeepSeek Harness 运行时的社区 C#/.NET SDK。**

本项目通过子进程启动官方 `dsh --profile sdk`，并使用其基于标准输入/输出的 JSON-RPC 2.0 行协议驱动任务；它不是用 C# 重写的 Harness。

SDK 默认使用 `deepseek-official` Provider 和规范模型 ID `deepseek-flash`。DSH 协议初始化仍要求显式发送 provider/model 字段，但 MCP 服务不重复提供这两个配置项。

> **非官方、早期版本：** 本 SDK 由社区维护，不代表 DeepSeek 官方。DeepSeek Harness 目前处于 Developer Preview，SDK 协议尚无版本协商或兼容性承诺。部署时请固定并验证 DSH 版本。

仓库名采用 `dsh-sdk-csharp`，便于从名称辨认目标生态；计划使用的 NuGet 包 ID 为 `DeepSeek.Harness.Sdk`。稳定版发布前，这些名称仍可能调整。
官方 SDK stdio 协议没有会话列表方法；应用若要恢复或展示旧会话，应自行保存 Session ID。

## 当前能力

- 目标框架：`.NET 10`
- 启动并管理本地 DSH SDK profile 子进程
- 完成 `initialize` 握手，并向 Session 提交对话
- `RunAsync` 在任务到达 Session inbox 并回到 idle 后返回
- 支持 prompt、resume、wait 和工作目录查询/切换
- 可订阅运行时通知
- 支持一次性操作审批和原生用户提问回调
- 优雅关闭并回收 DSH 子进程
- 运行库无第三方依赖

## 前置条件

1. 安装官方 DeepSeek Harness CLI，并确保 `dsh` 在 `PATH` 中可用。
2. 确认安装中包含可用的 `sdk` profile。
3. 在对应的 DSH home 配置模型提供方和凭据。
4. 运行模型生成的命令时，使用一次性或有适当隔离的工作目录。

SDK 不会内置凭据，也不会自动下载或替换 Harness 运行时。默认继承父进程环境，也可以显式传入环境变量覆盖值。

## 快速开始

NuGet 包目前尚未发布。从源码使用时，添加对 `src/Dsh.Sdk.CSharp/Dsh.Sdk.CSharp.csproj` 的项目引用：

```csharp
using DeepSeek.Harness.Sdk;

var harness = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
{
    DshExecutable = "dsh",
    Profile = "sdk",
    DshHome = Path.GetFullPath(".dsh-home"),
    WorkingDirectory = Path.GetFullPath("./workspace"),
    Provider = "deepseek-official",
    Model = "deepseek-flash",
});

await using (harness)
{
    var result = await harness.RunAsync("总结这个项目的主要模块。");
    Console.WriteLine(result.FinalResponse);
    Console.WriteLine($"Session: {result.SessionId}; finish: {result.FinishReason}");
}
```

SDK 默认模型 ID 为 `deepseek-flash`，这是 DeepSeek 当前规范的 Flash API 模型名。DSH SDK 协议仍要求在 `initialize` 中发送 provider 和 model；通常保留默认 provider `deepseek-official` 即可。

## 命令行 Demo 与官方 CLI

官方 Harness 已有自己的 CLI：`dsh headless "你的任务"` 可无 UI 执行一次任务，`dsh tui` 提供终端交互，`dsh web` 启动 Web UI。它们都是官方 Harness 的运行模式，不是这个 C# SDK 写的客户端。

若要从自己的 .NET 程序控制 Harness、连续发送多轮输入并接收 SDK 通知，可运行本仓库的交互式 Demo：

```powershell
dsh --profile dsh-sdk-csharp-chat --from-default-profile sdk --dump-config | Out-Null
dotnet run --project .\examples\ConsoleChat\ConsoleChat.csproj -- `
  "C:\path\to\dsh.cmd" `
  "$HOME\.dsh" `
  dsh-sdk-csharp-chat `
  "$HOME\.dsh\workspaces\dsh-sdk-csharp-chat"
```

请选一个尚不存在的 profile 名；`--from-default-profile` 会用默认模板重建同名 profile。Demo 复用一个 `DeepSeekHarnessClient` 和 Session，输入 `/exit` 或按 Ctrl+C 结束。它会持续监听该 Session 的 assistant 消息，因此投递到**当前会话**的 Schedule 回复也会显示；投递到其他或新建 Session 的消息不会显示在这个 Demo 中。到期投递需要 DSH Host 保持运行，固定间隔的最小值为 1 分钟。

不要把 Agent 说“我会等一分钟再提醒你”当成 Schedule 已创建的证据；应确认 Schedule 工具实际创建了任务，并且目标是当前 Session。模型自己串联倒计时只是在当前 Session/运行时存活期间尽力工作，不是持久闹钟，也不是操作系统推送通知。重要提醒请使用手机或系统闹钟。

在 Windows 上，`sandbox-local windows-acl temp grant materialization failed` 属于 DSH 运行时沙箱错误，不是 SDK 的审批错误。不要为了提醒切换 Full Access，也不要运行宽泛的 ACL 修复脚本。上游报告过一种规避方法：只为 DSH 子进程将 `TEMP`、`TMP` 指向一个能设置完整性标签的用户目录；SDK 的 `EnvironmentVariables` 可传入这两个变量。详见[上游 Windows ACL 讨论](https://github.com/deepseek-ai/deepseek-harness/discussions/8624)。

桌面版 Windows 可使用随安装提供的 `dsh.cmd`，SDK 会通过 `cmd.exe` 启动它。凭据由指定的 DSH home 管理；Demo 不会要求输入或打印 API Key。

也可以在 VS Code 中选择 **Terminal → Run Task → DSH C# SDK ConsoleChat**；它会为 Demo 创建持久工作目录。

### MCP 桥接服务的 DSH_HOME

`DSH_HOME` 是 DSH 数据目录，通常为 `C:\Users\<用户名>\.dsh` 或 `$HOME/.dsh`，其中包含 profiles、凭据、设置和会话记录。MCP 服务从自己的配置读取该值，并将绝对路径传给 DSH 子进程。若 MCP 服务和 ConsoleChat 以同一个 Windows 用户运行且使用同一个 `DSH_HOME`，两者读取的是同一份 DSH 配置。`DSH_HOME` 不负责选择 `dsh.cmd`；可执行文件路径由 `DSH_EXECUTABLE` 单独指定。

ConsoleChat 会显式传入 `DSH_EXECUTABLE`、`DSH_HOME`、`DSH_PROFILE` 和 `DSH_WORKSPACE`；MCP Host 则有自己的环境变量和 PATH。出现“管道正在被关闭”通常表示 DSH 子进程在 SDK 握手前退出。请查看 MCP 服务启动时输出的 executable/home/profile/workspace，再查看新版 SDK 错误中附带的子进程 stderr。当前 `appsettings.json` 默认 profile 是 `sdk`；若要与 ConsoleChat 完全一致，可在启动 MCP 服务的环境中设置 `DSH_PROFILE=dsh-sdk-csharp-chat`。

本机可用以下 PowerShell 设置与 ConsoleChat 相同的 DSH 启动配置：

```powershell
$env:DSH_EXECUTABLE = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "dsh-sdk-csharp-chat"
$env:DSH_WORKSPACE = "$HOME\.dsh\workspaces\dsh-mcp"
```

设置必须在启动 MCP 服务的那个终端/进程中生效；服务运行后再改环境变量不会影响已启动的进程。

## Streamable HTTP MCP 服务桥接

仓库新增独立的 ASP.NET Core MCP 服务项目：`src/Dsh.Sdk.CSharp.McpServer`。它通过官方 MCP C# SDK 提供 `/mcp` Streamable HTTP 端点，并调用本仓库的 DSH C# SDK。

服务通过 SDK 在本机启动 DSH 可执行程序，不调用远程 DSH 服务。`DSH_HOME` 和 `DSH_PROFILE` 指定本机运行时配置与凭据。SDK 使用自身的 provider/model 默认路由，并在初始化时发送协议要求的字段；MCP 服务不再提供重复的模型配置项。

MCP 项目包含 `appsettings.json`，提供非敏感的本地默认配置；ASP.NET Core 会自动加载，运行时环境变量和命令行配置可以覆盖它。`MCP_API_KEY` 不写入文件，仍须通过密钥存储或环境变量提供至少 32 个字符的随机值。不要把生产凭据提交到配置文件。

### 发布到 Windows

在装有 .NET 10 SDK 的开发电脑上，从仓库根目录发布 Windows x64 自包含版本：

```powershell
dotnet publish .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output .\publish\win-x64
```

根目录下的 `publish` 已加入 Git 忽略。将发布目录复制到目标 Windows x64 电脑即可，无需单独安装 .NET 运行时。但发布包不包含官方 DSH：目标电脑仍须安装 DSH，并为运行服务的账户配置可用的 `sdk` profile、模型提供方及凭据。

分发前检查发布目录：本地的 `appsettings.Development.json` 可能被复制进去，其中可能含有密钥，请勿分发该文件或生产凭据。DSH 凭据和 SQLite 数据库也应与发布文件分开保护。

在目标电脑的 `appsettings.json` 中添加或修改以下非敏感配置，保留文件中的其他配置项：

```json
{
  "urls": "http://127.0.0.1:5211",
  "DSH_PROFILE": "sdk",
  "DSH_WORKSPACE": "C:\\DshWorkspace",
  "DSH_MCP_LOG_DSH_MESSAGES": false
}
```

工作目录应由服务账户访问，且范围适当隔离；DSH 使用服务账户的权限运行。未配置 `DSH_HOME` 时默认使用当前用户的 `.dsh`，未配置 `DSH_EXECUTABLE` 时程序会自动查找；若目标电脑使用其他路径，请在目标电脑上配置。JSON 路径不会展开 `$HOME` 等环境变量。环境变量和命令行参数会覆盖 JSON 配置。

在目标电脑通过密钥存储或环境变量提供至少 32 个字符的随机 `MCP_API_KEY`，客户端必须使用同一密钥。以下为本地交互式启动示例：

```powershell
Set-Location C:\path\to\published\win-x64
$env:MCP_API_KEY = Read-Host "Enter the MCP API key"
.\Dsh.Sdk.CSharp.McpServer.exe
```

`Read-Host` 会显示输入内容；无人值守或生产环境应使用合适的密钥存储。默认环境为 Production。仅在开发时，如需加载目标电脑上单独配置的 `appsettings.Development.json`，可使用 `.\Dsh.Sdk.CSharp.McpServer.exe --environment Development`；环境专属配置覆盖基础配置，环境变量覆盖两者。

启动后可用 `Invoke-RestMethod http://127.0.0.1:5211/health` 检查服务是否存活，再通过已认证的 MCP 客户端验证 DSH 执行。`/health` 成功不代表 DSH 监控器已连接。该地址仅供目标电脑本机访问，远程访问应使用经过适当保护的网关。更多配置见 [MCP Server README](./src/Dsh.Sdk.CSharp.McpServer/README.md#publish-for-windows)。

长任务使用异步工具：`dsh_submit_task` 快速返回 job ID，后台 Worker 通过 SDK 执行 DSH，并将结果写入本地 SQLite。邮件开关 `Email:Enabled`（环境变量 `Email__Enabled`）默认关闭；关闭时无需邮箱/SMTP 配置，也不会发送邮件，但任务结果仍可通过 `dsh_task_status`、`dsh_task_result` 查询，host-run 结果可通过 `dsh_host_runs` 查看。`dsh_run` 保留给短任务使用。

DSH 通讯日志默认记录会话 ID、提示长度、无文本助手消息的内容块类型、完成原因和回复长度；`finishReason=error` 会将任务标记为失败。设置 `DSH_MCP_LOG_DSH_MESSAGES=true` 后还会记录用户提示、助手消息正文和 DSH RPC/turn 错误详情，提示及助手消息按 `DSH_MCP_MAX_RESULT_CHARACTERS` 截断。疑似含常见凭据的提示不会记录，但检测不能识别所有秘密；请勿在提示中输入凭据，并仅在本地诊断时开启，日志可能包含隐私数据。其他通知类型在 Debug 日志级别记录。

SQLite 同时维护 MCP 自己的会话记录、任务结果、可观察到的 host-run 结果和每个调用者的活动会话指针。普通请求使用当前活动会话；只有 `dsh_create_session` 或 `dsh_switch_session` 会切换。默认使用可信网关注入的 `X-User-Id` 和 `X-User-Email`；它们不是自行可信的身份凭证，中转网关必须先认证并覆盖客户端提供的同名 header。

也可在本地配置固定调用者，优先使用配置并忽略两个请求头：

```json
{
  "Caller": {
    "DefaultUserId": "local-user",
    "DefaultEmail": "recipient@example.com"
  }
}
```

这是配置片段，保留文件中的其他设置。两项必须同时填写或同时留空；只填写一项或填写无效值会导致启动失败。也可使用 `Caller__DefaultUserId` 和 `Caller__DefaultEmail` 环境变量。启用固定调用者后，所有通过认证的请求共享该用户的 MCP 会话和任务，不能用于区分用户。启用邮件通知时仍须配置 SMTP，并将该邮箱加入 `Email:AllowedRecipients` 精确允许名单；收件人不是模型工具参数。已排队任务和邮件保留原收件人，现有会话的邮箱也只有后续会话操作才会更新；更改默认值不会重写历史数据。详见 [MCP Server README](./src/Dsh.Sdk.CSharp.McpServer/README.md#optional-fixed-local-caller)。

数据库默认放在用户本地应用数据目录，而不是仓库内；其中可能包含任务提示、结果和邮箱，须保护数据库文件及备份。SQLite 保存的是 MCP 会话映射，并不提供 DSH 会话列表，也不能保证 DSH Host 在 MCP 重启后恢复对话状态。后台监听会观察 SQLite 已登记会话的收件箱插入、助手回复和 idle 通知；当前 SDK 事件未必能证明这条 host 任务就是 Schedule，因此只是 host 活动的 best-effort 识别，需用实际安装的 DSH 版本联调验证。

最小启动示例：

```powershell
$env:MCP_API_KEY = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ASPNETCORE_URLS = "http://127.0.0.1:5211"
$env:DSH_EXECUTABLE = "C:\path\to\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "sdk"
$env:DSH_WORKSPACE = "$HOME\workspaces\dsh-mcp"

dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

`MCP_API_KEY` 必填且至少 32 个字符；访问 `/mcp` 时须带 `Authorization: Bearer <MCP_API_KEY>`。服务不会记录或回显 API Key。服务默认拒绝未列入 `MCP_ALLOWED_ORIGINS` 的浏览器 `Origin`；生产部署应使用 HTTPS 或置于可信 TLS 反向代理后，避免将明文 HTTP 暴露到公网。

本项目提供标准 Streamable HTTP MCP 端点，可由兼容的 MCP Client 调用。设备协议适配由独立网关负责；例如 [verdure-mcp-for-xiaozhi](https://github.com/maker-community/verdure-mcp-for-xiaozhi) 用于将 MCP 服务转换为 Xiaozhi 与 Tuya 智能设备可用的服务。

**边界：** SQLite 队列适用于单机单实例测试，不是分布式作业系统。服务重启时，运行中的任务标记为 `interrupted` 且不会自动重放，以免重复执行有副作用的代码任务。邮件发送失败会记录在任务状态中。MCP 服务本身不创建 Cron；DSH Schedule 由 DSH Host 管理，MCP 仅尝试监听已登记会话的运行时通知并邮件转发可识别的完成结果。详细环境变量、安全边界和验证方式见 [MCP 服务 README](./src/Dsh.Sdk.CSharp.McpServer/README.md)。

多次使用同一个 `DeepSeekHarnessClient`，并传入相同 `sessionId`，可以继续该 Session：

```csharp
var first = await harness.RunAsync("检查最近一次部署。", "job-42");
var second = await harness.RunAsync("与昨天的部署进行比较。", "job-42");
```

同一客户端对同一个 `sessionId` 的 `RunAsync` 调用会串行执行；不同 Session 可以并行。

需要独立任务上下文时，使用独立的 `sessionId`、工作目录和 DSH home。`RunAsync` 返回的是该 Session 本次观察到的活动区间；如果有其他调用者同时向同一个 Session 排队，结果不保证只归因于单条输入。

## 运行时通知

```csharp
var result = await harness.RunAsync(
    "运行检查并总结失败项。",
    onNotification: notification =>
        Console.WriteLine($"{notification.Method}: {notification.Parameters}"));
```

`PromptAsync` 是低层接口：它只等待 DSH 接受消息并返回 `messageId`，不等待 Agent 执行结束。可在提交前订阅，然后调用 `WaitForSessionAsync` 或读取通知：

```csharp
await using var notifications = harness.SubscribeNotifications();
var messageId = await harness.PromptAsync("job-42", "运行检查。");
await harness.WaitForSessionAsync("job-42");
Console.WriteLine($"已入队消息：{messageId}");
```

### 审批与用户提问

操作审批需显式开启。只有返回 `AllowOnce` 才会批准**当前这一次操作**；SDK 不会默认自动授权：

```csharp
var options = new DeepSeekHarnessOptions
{
    ApprovalRequests = true,
    OnApprovalRequest = async (request, cancellationToken) =>
    {
        Console.WriteLine(
            $"是否仅允许本次 {request.ToolName} 操作？{request.Reason ?? string.Empty}");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            ? ApprovalRequestDecision.AllowOnce
            : ApprovalRequestDecision.Reject;
    },
};
```

设置 `OnUserQuestion` 可处理 `ask_user_question` 和计划审阅。回调收到的每个问题都有原始 ID，必须逐个回答；用户取消时返回 `SdkUserQuestionResponse.Dismiss()`。只有配置了回调，SDK 才会向 DSH 声明支持用户提问。DSH 取消问题时会取消回调的 `CancellationToken`，并丢弃迟到的答案。

## 重要边界

- DSH 的 stdout 专用于 JSON-RPC 帧；SDK profile 不应把普通日志写入 stdout。
- `RunAsync` 使用 `session/query`，并按收件箱回执和 Agent 状态收集运行过程；低层 `PromptAsync` 使用 `session/prompt`。
- 如果运行时报告 Session 已存在，`RunAsync` 会尝试 `session/resume`，然后重试一次原请求；恢复失败会明确报错，不会静默创建新会话。如果旧版运行时不支持 `session/query`，则回退到 `session/prompt`；该回退路径不提供原生斜杠命令路由。
- 请求超时或调用方取消只停止等待，不会取消或回滚已被 DSH 接受的任务。
- 原始订阅会收到运行时内各个 Session 的通知；高层 `RunAsync` 仅收集目标 Session 和发现的子 Agent。
- 未支持的服务端请求会被拒绝。操作审批必须显式启用，且只对当前操作生效；无效决定或回调异常会返回协议错误，保持 fail-closed。原生用户提问需配置 `OnUserQuestion`，除非 DSH 组合本身提供了回答器。
- SDK 是本地进程客户端，不是远程服务。若要让其他服务通过网络提交工作，应在外层服务实现认证、授权、配额和审计。

## 构建和测试

```powershell
dotnet test .\dsh-sdk-csharp.slnx --configuration Release
dotnet pack .\src\Dsh.Sdk.CSharp\Dsh.Sdk.CSharp.csproj --configuration Release
```

测试使用内置模拟 JSON-RPC 运行时，不需要模型 API 凭据。

要用本机实际安装的 DSH 做联调，可运行 [`examples/LiveSmoke`](./examples/LiveSmoke)。它会调用已配置的模型，并在指定 DSH home 中保存一个测试 Session。

## 参与贡献

欢迎提交 Issue 和 PR。报告协议兼容问题时，请注明操作系统和 DSH 版本，并提供已脱敏的错误或协议日志；不要提交 API Key、个人会话记录或敏感项目数据。

## 许可证

MIT。DeepSeek Harness 是独立项目，其许可证和第三方声明请见[官方仓库](https://github.com/deepseek-ai/deepseek-harness)。
