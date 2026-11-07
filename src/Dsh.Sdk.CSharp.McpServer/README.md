# DSH MCP Bridge

An ASP.NET Core MCP server that exposes the C# DSH SDK as a Streamable HTTP MCP endpoint.

The MCP service exposes a short synchronous tool, `dsh_run`, and a persistent asynchronous workflow through `dsh_submit_task`, `dsh_task_status`, and `dsh_task_result`. The async path is intended for clients with short tool timeouts: submit returns a small job ID, while the worker runs DSH and persists the terminal result. Email delivery is optional and disabled by default.

## Requirements

- .NET 10
- The official DSH CLI installed on the same host as this service
- A configured DSH home/profile and model provider
- A randomly generated MCP API key

The host-schedule monitor starts DSH when the MCP service starts so it can observe runtime notifications. SQLite stores MCP-owned conversations and each caller's active-session pointer. Normal tasks stay in that session; `dsh_create_session` and `dsh_switch_session` are the only tools that change the active session. Jobs capture their session at submission, so a later switch does not move a running job.

The database also stores queued jobs, results, observed host-run results, email state, and a mail outbox. By default it is written outside the repository under the current user's local application data directory. Prompts, results, and email addresses are stored as plaintext in SQLite; protect the database file and backups accordingly.

SQLite preserves the application session mapping and DSH session ID, but it does not provide a DSH session-list operation or guarantee restoration of DSH conversation state after the host restarts. Session continuity across a restart depends on the installed DSH runtime and must be validated separately.

The service launches the configured local DSH executable through the C# SDK; it does not call a remote DSH service. The SDK uses its default route (`deepseek-official` / `deepseek-flash`) and supplies the protocol-required `provider` and `model` fields during initialization. The MCP service has no separate settings for these values; DSH credentials and profile configuration remain in the configured local DSH home.

`DSH_HOME` selects the DSH data directory, normally `<Windows user profile>\.dsh` or `$HOME/.dsh`. It contains the profiles, credentials, settings, and Session storage. The bridge resolves it from configuration and passes the absolute path to the child DSH process. If the MCP host and ConsoleChat run as the same OS user and use the same DSH_HOME, they read the same DSH configuration. `DSH_HOME` does not select the CLI executable; configure `DSH_EXECUTABLE` separately.

The ConsoleChat example passes `DSH_EXECUTABLE`, `DSH_HOME`, `DSH_PROFILE`, and `DSH_WORKSPACE` explicitly. The MCP host has its own environment/PATH. A closed stdin pipe means the DSH child exited before accepting the SDK handshake; use the bridge startup log to compare the resolved executable, home, profile, and workspace, then inspect the child stderr included by the latest SDK build.

By default, the tools require an `X-User-Id` HTTP header from the authenticated gateway. When email notifications are enabled, async submission also requires `X-User-Email`, validated against `Email:AllowedRecipients`; it is not a model-controlled tool argument. Alternatively, configure a fixed local caller as described below. The MCP API key is shared-key authentication, not per-user identity. When using headers, only a trusted gateway that authenticates a caller should set them; do not forward arbitrary client-supplied values as trusted identity.

The host-run monitor observes SDK notifications for sessions registered in SQLite. It persists a host-run result when it sees an inbox insertion, assistant response, and idle transition for a known session with no MCP job running, and enqueues email only when notifications are enabled. The current SDK event shape does not identify whether an out-of-band inbox message came specifically from DSH Schedule or another host-side source, so this is best-effort host-activity detection, not a durable scheduler. Validate it against the installed DSH version; it cannot discover sessions that were never registered by this MCP service.

## Configure and run

The project includes an `appsettings.json` with non-secret defaults for local development. ASP.NET Core loads it automatically; environment variables and command-line configuration override these values. `MCP_API_KEY` is intentionally not included: provide a random key of at least 32 characters through a secret store or environment variable. Do not put production credentials in the checked-in settings file.

PowerShell example:

```powershell
$env:MCP_API_KEY = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ASPNETCORE_URLS = "http://127.0.0.1:5211"
$env:DSH_EXECUTABLE = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "dsh-sdk-csharp-chat"
$env:DSH_WORKSPACE = "$HOME\workspaces\dsh-mcp"

dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

The service discovers DSH from the current process `PATH` when `DSH_EXECUTABLE` is not set. On Windows it checks `dsh.cmd`, `dsh.bat`, and `dsh.exe`, then the standard DeepSeek Desktop CLI installation path under `%LOCALAPPDATA%`; on other platforms it checks `dsh`. If DSH is installed elsewhere, set `DSH_EXECUTABLE` to its full path (for example, `C:\path\to\dsh.cmd`).

Optional: set `DSH_MCP_DATABASE_PATH` to choose another SQLite file. The default is outside the checkout under the current user's local application data directory. Keep that file and its backups private.

The bridge reads `DSH_HOME`, `DSH_PROFILE`, and `DSH_WORKSPACE` from its own ASP.NET Core configuration, then passes them to the child DSH process. If `DSH_HOME` is omitted, the bridge explicitly uses `<current Windows user profile>\.dsh`; it is not inferred from the ConsoleChat process. The checked-in `appsettings.json` selects profile `sdk`. If ConsoleChat works with a different profile or DSH installation, set the bridge's `DSH_PROFILE` and `DSH_EXECUTABLE` to those exact working values, for example:

```powershell
$env:DSH_EXECUTABLE = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "dsh-sdk-csharp-chat"
$env:DSH_WORKSPACE = "$HOME\.dsh\workspaces\dsh-mcp"
```

`DSH_EXECUTABLE` and `DSH_PROFILE` are independent: matching the home alone does not make the MCP bridge use the same CLI/profile as ConsoleChat. When DSH exits before handshake, the C# SDK reports its exit code and recent stderr to help identify an invalid executable, profile, or startup failure.

### Optional fixed local caller

For a single shared local identity, set both values in `appsettings.json` or an environment-specific settings file:

```json
{
  "Caller": {
    "DefaultUserId": "local-user",
    "DefaultEmail": "recipient@example.com"
  }
}
```

This is a configuration fragment; keep the other settings. The equivalent environment variables are `Caller__DefaultUserId` and `Caller__DefaultEmail`. Both values must be nonempty or both omitted; a partial or invalid pair prevents startup. When configured, all MCP tools use this identity and ignore both request headers, even if supplied. If both values are omitted, the existing gateway-header behavior remains unchanged.

All authenticated callers then share the same MCP session and task ownership; this is not per-user authentication or isolation. Use this mode only when that sharing is intended. If email is enabled, configure SMTP and include the default email in `Email:AllowedRecipients`; the allowlist still applies. Existing queued jobs and outbox messages retain their captured recipients, and stored sessions retain their email until updated by a later session operation; changing the defaults does not rewrite existing data.

### Publish for Windows

From the repository root on a machine with the .NET 10 SDK, publish a self-contained Windows x64 build:

```powershell
dotnet publish .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output .\publish\win-x64
```

The root `publish` directory is ignored by Git. Copy the published directory to the target Windows x64 machine; it does not need a separate .NET runtime. The official DSH CLI is not included: install it on the target machine and configure a working `sdk` profile, model provider, and credentials for the account running the service.

Before distributing the directory, inspect it for sensitive files. Publishing can copy a local `appsettings.Development.json`, which may contain secrets; do not distribute it or production credentials. Keep DSH credentials and the SQLite database separate from the published files.

Edit the target machine's `appsettings.json` to add or change these non-secret settings, keeping the other existing settings:

```json
{
  "urls": "http://127.0.0.1:5211",
  "DSH_PROFILE": "sdk",
  "DSH_WORKSPACE": "C:\\DshWorkspace",
  "DSH_MCP_LOG_DSH_MESSAGES": false
}
```

Create or select an appropriately isolated workspace that the service account can access. DSH runs with that account's permissions. If omitted, `DSH_HOME` defaults to the current user's `.dsh` directory and `DSH_EXECUTABLE` is discovered as described above; configure explicit paths on the target machine if necessary. JSON paths do not expand PowerShell or Windows environment variables. Environment variables and command-line settings override the JSON configuration.

Provide a random `MCP_API_KEY` of at least 32 characters through a secret store or environment variable on the target machine. For a local PowerShell session, retrieve the existing key securely and set it before starting the service; MCP clients must use the same key. Start from the published directory:

```powershell
Set-Location C:\path\to\published\win-x64
$env:MCP_API_KEY = Read-Host "Enter the MCP API key"
.\Dsh.Sdk.CSharp.McpServer.exe
```

`Read-Host` displays the entered value; use a suitable secret store for unattended or production deployments. To load a separately provisioned local `appsettings.Development.json` for development only, start the executable with `--environment Development`; the default is Production. Environment-specific settings override `appsettings.json`, while environment variables override both.

Check liveness with `Invoke-RestMethod http://127.0.0.1:5211/health`, then use an authenticated MCP client to verify DSH execution. A successful health response does not prove that the DSH monitor connected. The loopback URL is local to the target machine; use an appropriately secured gateway for remote access.

The API key is required and must be at least 32 characters. The service never logs or returns it. Every request to `/mcp` must include:

```http
Authorization: Bearer <MCP_API_KEY>
```

### VS Code HTTP MCP client configuration

This project exposes Streamable HTTP at `/mcp`; VS Code connects to the running ASP.NET Core service as an HTTP client. The workspace [`.vscode/mcp.json`](../../.vscode/mcp.json) follows this pattern. Start the service separately and configure DSH variables on that service process; changing variables in a different terminal does not affect an already-running process.

```json
{
  "servers": {
    "dsh-csharp-local": {
      "type": "http",
      "url": "http://127.0.0.1:5211/mcp",
      "headers": {
        "Authorization": "Bearer ${input:dshMcpApiKey}",
        "X-User-Id": "${input:dshMcpUserId}",
        "X-User-Email": "${input:dshMcpUserEmail}"
      }
    }
  },
  "inputs": [
    {
      "type": "promptString",
      "id": "dshMcpApiKey",
      "description": "Local DSH MCP API key",
      "password": true
    },
    {
      "type": "promptString",
      "id": "dshMcpUserId",
      "description": "Stable user/device ID from the trusted gateway context"
    },
    {
      "type": "promptString",
      "id": "dshMcpUserEmail",
      "description": "Email address allowed by the MCP server"
    }
  ]
}
```

Enter the same API key configured for the service when VS Code prompts. In header mode, also provide a stable test user/device ID and an allowlisted email; in fixed local caller mode, the identity headers are ignored and may be omitted. In production header mode, the Xiaozhi gateway must authenticate the device and inject or overwrite both identity headers; do not trust arbitrary client-provided headers. Configure its MCP client to use `https://<host>/mcp` with the bearer header.

The bridge logs the resolved DSH executable, home, profile, workspace, provider, and model at startup. These values contain no API key. If DSH exits before the SDK handshake, the SDK error includes the child exit code and recent stderr.

`/health` is unauthenticated and returns only a basic liveness status. The server rejects browser `Origin` headers unless their exact origin is included in `MCP_ALLOWED_ORIGINS` (comma- or semicolon-separated). Keep the MCP endpoint on a private network or behind HTTPS and a trusted reverse proxy; do not expose plain HTTP to the public internet.

## MCP client integration

This service exposes a standard Streamable HTTP MCP endpoint for compatible clients. Device-specific protocol adaptation belongs in a separate gateway. For Xiaozhi and Tuya smart-device integration, see [verdure-mcp-for-xiaozhi](https://github.com/maker-community/verdure-mcp-for-xiaozhi).

## VS Code local debugging

The workspace `.vscode/mcp.json` registers this service as an HTTP MCP server at `http://127.0.0.1:5211/mcp`. It does not launch the ASP.NET Core process; start it separately in a PowerShell terminal with the same local API key you enter when VS Code prompts:

```powershell
$secureKey = Read-Host "Local MCP_API_KEY (at least 32 characters)" -AsSecureString
$env:MCP_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:ASPNETCORE_URLS = "http://127.0.0.1:5211"
dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

In VS Code, start `dsh-csharp-local` from the MCP server list and enter the same key. Keep the listener bound to loopback for local debugging.

## MCP tool

`dsh_run(prompt)` is synchronous, returns a bounded response, and is intended only for short tasks. For XiaoZhi or any short-timeout client, use the async tools:

- `dsh_submit_task(prompt)` — persists a job and returns its `jobId` immediately. The worker runs it in the caller's active session. When email is disabled, the full result remains available in local task history.
- `dsh_task_status(jobId)` — returns concise state, a bounded preview, and email-delivery status (`disabled` when notifications are off).
- `dsh_task_result(jobId, startIndex?, maxCharacters?)` — returns a bounded result slice.
- `dsh_current_session()` / `dsh_list_sessions()` — inspect MCP-owned sessions for the current caller identity.
- `dsh_create_session(title)` / `dsh_switch_session(sessionId)` — explicitly create or switch the active session.
- `dsh_host_runs(count?)` — reads recent DSH host-originated results observed on this caller's registered sessions, including when email is disabled.

Email notifications are controlled by `Email:Enabled` (`Email__Enabled` environment variable) and default to `false`. With email disabled, async submission does not require `X-User-Email`, no SMTP messages are sent, and task/host results are still stored in SQLite and available through the status/result/host-run tools. Set `Email__Enabled=true` to enable delivery. When enabled, the caller's email comes from the fixed local caller if configured, otherwise from `X-User-Email`, and must exactly match `Email:AllowedRecipients` (or the legacy `DSH_EMAIL_ALLOWED_RECIPIENTS` setting). It is captured when the job is submitted; no recipient parameter is exposed to the model.

DSH communication logs include session ID, prompt length, assistant message block types when there is no text, finish reason, and response length. A DSH `finishReason` of `error` is recorded as a failed job rather than successful completion. When `DSH_MCP_LOG_DSH_MESSAGES=true`, prompt text, assistant text, and DSH RPC/turn failure details may also be logged; prompt and assistant text are truncated to `DSH_MCP_MAX_RESULT_CHARACTERS`. Prompts matching common credential indicators are omitted, but this detection cannot identify every secret. Enable this only for local diagnostics and never include credentials in a prompt; logs may contain private information. Other notification types are logged at Debug level.

The background worker persists jobs and an email outbox, but it is a single-process SQLite worker, not a distributed queue. Interrupted DSH jobs are marked `interrupted` on service restart and are not replayed automatically because coding tasks may have side effects. When enabled, email delivery is attempted asynchronously; failures are stored and visible in `dsh_task_status`. Outbox delivery is at-least-once: a process crash after SMTP accepts a message but before SQLite marks it sent can cause a duplicate.

- DSH host Schedule events can be emailed only when they are observed on a session already registered by this service and match the notification pattern described above. The monitor cannot currently prove that an observed host run originated specifically from Schedule.
- This service does not provide its own cron/scheduler. Configure DSH Schedule separately when available, or have an external scheduler call an MCP task tool.

## Settings

| Environment variable | Default | Description |
|---|---|---|
| `MCP_API_KEY` | Required | Shared bearer API key; generate a random secret of at least 32 characters |
| `MCP_ALLOWED_ORIGINS` | Empty | Exact browser origins allowed to call the MCP endpoint; non-browser requests without `Origin` are allowed |
| `ASPNETCORE_URLS` | ASP.NET Core default | Listening address. Prefer loopback/private binding behind TLS termination |
| `DSH_EXECUTABLE` | Discovered from `PATH` or standard Windows Desktop install | Optional DSH executable override; Windows searches `dsh.cmd`, `dsh.bat`, then `dsh.exe` |
| `DSH_HOME` | DSH default home | DSH home containing the selected profile, credentials, and persistence |
| `DSH_PROFILE` | `sdk` | Profile that serves the SDK JSON-RPC runtime |
| `DSH_WORKSPACE` | Service working directory | Workspace available to DSH tools |
| `DSH_MCP_DATABASE_PATH` | User local application data directory | SQLite database path; keep outside the repository and restrict access |
| `DSH_MCP_MAX_PROMPT_CHARACTERS` | `20000` | Maximum accepted prompt length |
| `DSH_MCP_MAX_RESULT_CHARACTERS` | `4000` | Maximum characters in synchronous or paged MCP result responses |
| `DSH_MCP_LOG_DSH_MESSAGES` | `false` | Include bounded prompt/assistant text and error details in DSH communication logs |
| `Caller__DefaultUserId` | Empty | Fixed shared caller ID; must be configured together with `Caller__DefaultEmail` |
| `Caller__DefaultEmail` | Empty | Fixed shared caller email; must be configured together with `Caller__DefaultUserId` and be allowlisted when email is enabled |
| `Email__Enabled` | `false` | Enable asynchronous SMTP notifications; restarting the service is required after changing it |
| `Email__SmtpHost` (`SMTP_HOST` legacy) | Empty | SMTP host |
| `Email__SmtpPort` (`SMTP_PORT` legacy) | `587` | SMTP port |
| `Email__SmtpSecurity` (`SMTP_SECURITY` legacy) | `StartTls` | `StartTls` or `SslOnConnect` |
| `Email__SmtpUsername` / `Email__SmtpPassword` (`SMTP_USERNAME` / `SMTP_PASSWORD` legacy) | Empty | Optional SMTP credentials; set both or neither |
| `Email__FromEmail` (`SMTP_FROM` legacy) | Empty | Sender address |
| `Email__FromName` (`SMTP_FROM_NAME` legacy) | `DSH MCP` | Sender display name |
| `Email__AllowedRecipients` (`DSH_EMAIL_ALLOWED_RECIPIENTS` legacy) | Empty | Comma/semicolon-separated exact recipient allowlist |

The checked-in `appsettings.json` groups email settings under `Email`, matching the layout used by `verdure-mcp`. Use the double-underscore form for environment variables; environment values override the JSON defaults. The old flat environment variable names remain supported. Async job email outcomes are stored in the job/outbox state and exposed through `dsh_task_status`; failed delivery is not reported as successful. SMTP credentials should be injected from a secret store, not committed to configuration. The checked-in file leaves SMTP host/sender unset and the recipient allowlist empty, so email remains disabled until configured.

## Security boundaries

- The API key authorizes callers to invoke the DSH agent; treat it as a powerful credential.
- DSH runs with the host account's filesystem and process access. Use a dedicated OS account/container, a narrowly scoped workspace, and a least-privilege DSH profile.
- Do not add a generic shell/command MCP tool or automatically grant DSH approvals. The C# SDK's approval callback is fail-closed unless an explicit handler is configured.
- The email allowlist prevents the model from choosing arbitrary recipients, but it does not replace caller authentication or per-user authorization.
- The MCP API key is shared-key authentication, not OAuth or multi-tenant identity. For multiple users/devices, put a gateway with per-principal authorization in front.
- A fixed local caller intentionally shares MCP session and task ownership across all authenticated requests; use gateway headers instead when callers need separate identities.

## Verify

Build and test the repository:

```powershell
dotnet build .\dsh-sdk-csharp.slnx --configuration Release
dotnet test .\dsh-sdk-csharp.slnx --configuration Release
```

The MCP service can be verified without making a DSH model call by sending an authenticated MCP `initialize` and `tools/list` request to `/mcp`.
