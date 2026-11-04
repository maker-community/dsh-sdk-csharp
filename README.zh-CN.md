# dsh-sdk-csharp

**面向官方 DeepSeek Harness 运行时的社区 C#/.NET SDK。**

本项目通过子进程启动官方 `dsh --profile sdk`，并使用其基于标准输入/输出的 JSON-RPC 2.0 行协议驱动任务；它不是用 C# 重写的 Harness。

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
    Model = "deepseek-v4-flash",
});

await using (harness)
{
    var result = await harness.RunAsync("总结这个项目的主要模块。");
    Console.WriteLine(result.FinalResponse);
    Console.WriteLine($"Session: {result.SessionId}; finish: {result.FinishReason}");
}
```

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
- 如果旧版运行时不支持 `session/query`，`RunAsync` 会回退到 `session/prompt`，等待对应的收件箱回执和 idle 状态。回退路径不提供原生斜杠命令路由。
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
