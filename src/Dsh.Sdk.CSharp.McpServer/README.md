# DSH MCP Bridge

An ASP.NET Core MCP server that exposes the C# DSH SDK as a Streamable HTTP MCP endpoint.

The first version exposes one tool, `dsh_run`, which runs a prompt against the configured DSH profile and returns the final response. It can optionally email the result to an address explicitly listed in `Email:AllowedRecipients` (or the legacy `DSH_EMAIL_ALLOWED_RECIPIENTS` setting).

## Requirements

- .NET 10
- The official DSH CLI installed on the same host as this service
- A configured DSH home/profile and model provider
- A randomly generated MCP API key

The MCP service starts DSH lazily on the first `dsh_run` call. MCP calls share a DSH process and its default Session for the lifetime of the service. An optional `conversationId` selects a separate Session for calls that use the same value. Session ids are scoped to this service process; automatic Session restore after an MCP service restart is not implemented.

The service launches the configured local DSH executable through the C# SDK; it does not call a remote DSH service. The SDK uses its default route (`deepseek-official` / `deepseek-flash`) and supplies the protocol-required `provider` and `model` fields during initialization. The MCP service has no separate settings for these values; DSH credentials and profile configuration remain in the configured local DSH home.

`DSH_HOME` selects the DSH data directory, normally `<Windows user profile>\.dsh` or `$HOME/.dsh`. It contains the profiles, credentials, settings, and Session storage. The bridge resolves it from configuration and passes the absolute path to the child DSH process. If the MCP host and ConsoleChat run as the same OS user and use the same DSH_HOME, they read the same DSH configuration. `DSH_HOME` does not select the CLI executable; configure `DSH_EXECUTABLE` separately.

The ConsoleChat example passes `DSH_EXECUTABLE`, `DSH_HOME`, `DSH_PROFILE`, and `DSH_WORKSPACE` explicitly. The MCP host has its own environment/PATH. A closed stdin pipe means the DSH child exited before accepting the SDK handshake; use the bridge startup log to compare the resolved executable, home, profile, and workspace, then inspect the child stderr included by the latest SDK build.

## Configure and run

The project includes an `appsettings.json` with non-secret defaults for local development. ASP.NET Core loads it automatically; environment variables and command-line configuration override these values. `MCP_API_KEY` is intentionally not included: provide a random key of at least 32 characters through a secret store or environment variable. Do not put production credentials in the checked-in settings file.

PowerShell example:

```powershell
$env:MCP_API_KEY = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ASPNETCORE_URLS = "http://127.0.0.1:8000"
$env:DSH_EXECUTABLE = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "dsh-sdk-csharp-chat"
$env:DSH_WORKSPACE = "$HOME\workspaces\dsh-mcp"

dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

The service discovers DSH from the current process `PATH` when `DSH_EXECUTABLE` is not set. On Windows it checks `dsh.cmd`, `dsh.bat`, and `dsh.exe`, then the standard DeepSeek Desktop CLI installation path under `%LOCALAPPDATA%`; on other platforms it checks `dsh`. If DSH is installed elsewhere, set `DSH_EXECUTABLE` to its full path (for example, `C:\path\to\dsh.cmd`).

The bridge reads `DSH_HOME`, `DSH_PROFILE`, and `DSH_WORKSPACE` from its own ASP.NET Core configuration, then passes them to the child DSH process. If `DSH_HOME` is omitted, the bridge explicitly uses `<current Windows user profile>\.dsh`; it is not inferred from the ConsoleChat process. The checked-in `appsettings.json` selects profile `sdk`. If ConsoleChat works with a different profile or DSH installation, set the bridge's `DSH_PROFILE` and `DSH_EXECUTABLE` to those exact working values, for example:

```powershell
$env:DSH_EXECUTABLE = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "dsh-sdk-csharp-chat"
$env:DSH_WORKSPACE = "$HOME\.dsh\workspaces\dsh-mcp"
```

`DSH_EXECUTABLE` and `DSH_PROFILE` are independent: matching the home alone does not make the MCP bridge use the same CLI/profile as ConsoleChat. When DSH exits before handshake, the C# SDK reports its exit code and recent stderr to help identify an invalid executable, profile, or startup failure.

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
      "url": "http://127.0.0.1:8000/mcp",
      "headers": {
        "Authorization": "Bearer ${input:dshMcpApiKey}"
      }
    }
  },
  "inputs": [
    {
      "type": "promptString",
      "id": "dshMcpApiKey",
      "description": "Local DSH MCP API key",
      "password": true
    }
  }
}
```

Enter the same API key configured for the service when VS Code prompts. For Xiaozhi Server, run the bridge as an HTTP service and configure its MCP client to use `https://<host>/mcp` with the bearer header.

The bridge logs the resolved DSH executable, home, profile, workspace, provider, and model at startup. These values contain no API key. If DSH exits before the SDK handshake, the SDK error includes the child exit code and recent stderr.

`/health` is unauthenticated and returns only a basic liveness status. The server rejects browser `Origin` headers unless their exact origin is included in `MCP_ALLOWED_ORIGINS` (comma- or semicolon-separated). Keep the MCP endpoint on a private network or behind HTTPS and a trusted reverse proxy; do not expose plain HTTP to the public internet.

## MCP client integration

This service exposes a standard Streamable HTTP MCP endpoint for compatible clients. Device-specific protocol adaptation belongs in a separate gateway. For Xiaozhi and Tuya smart-device integration, see [verdure-mcp-for-xiaozhi](https://github.com/maker-community/verdure-mcp-for-xiaozhi).

## VS Code local debugging

The workspace `.vscode/mcp.json` registers this service as an HTTP MCP server at `http://127.0.0.1:8000/mcp`. It does not launch the ASP.NET Core process; start it separately in a PowerShell terminal with the same local API key you enter when VS Code prompts:

```powershell
$secureKey = Read-Host "Local MCP_API_KEY (at least 32 characters)" -AsSecureString
$env:MCP_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:ASPNETCORE_URLS = "http://127.0.0.1:8000"
dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

In VS Code, start `dsh-csharp-local` from the MCP server list and enter the same key. Keep the listener bound to loopback for local debugging.

## MCP tool

`dsh_run(prompt, conversationId?, notifyEmail?)`

- `prompt` is required and limited by `DSH_MCP_MAX_PROMPT_CHARACTERS` (default 20,000).
- `conversationId` is optional. Reusing the same value reuses a Session within the current service process. It is not an authenticated user identity or authorization boundary.
- `notifyEmail` is optional. Email is sent only if the recipient exactly matches `Email:AllowedRecipients` (or the legacy `DSH_EMAIL_ALLOWED_RECIPIENTS` setting).
- The tool waits for DSH to finish its observed run before returning. Long tasks may exceed a caller's timeout; a durable asynchronous job queue and pollable job IDs are not included yet.
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
| `DSH_MCP_MAX_PROMPT_CHARACTERS` | `20000` | Maximum accepted prompt length |
| `Email__SmtpHost` (`SMTP_HOST` legacy) | Empty | SMTP host |
| `Email__SmtpPort` (`SMTP_PORT` legacy) | `587` | SMTP port |
| `Email__SmtpSecurity` (`SMTP_SECURITY` legacy) | `StartTls` | `StartTls` or `SslOnConnect` |
| `Email__SmtpUsername` / `Email__SmtpPassword` (`SMTP_USERNAME` / `SMTP_PASSWORD` legacy) | Empty | Optional SMTP credentials; set both or neither |
| `Email__FromEmail` (`SMTP_FROM` legacy) | Empty | Sender address |
| `Email__FromName` (`SMTP_FROM_NAME` legacy) | `DSH MCP` | Sender display name |
| `Email__AllowedRecipients` (`DSH_EMAIL_ALLOWED_RECIPIENTS` legacy) | Empty | Comma/semicolon-separated exact recipient allowlist |

The checked-in `appsettings.json` groups email settings under `Email`, matching the layout used by `verdure-mcp`. Use the double-underscore form for environment variables; environment values override the JSON defaults. The old flat environment variable names remain supported. Email failures are returned explicitly alongside the DSH result; the service does not claim delivery succeeded when SMTP fails. SMTP credentials should be injected from a secret store, not committed to configuration. The checked-in file leaves SMTP host/sender unset and the recipient allowlist empty, so email remains disabled until configured.

## Security boundaries

- The API key authorizes callers to invoke the DSH agent; treat it as a powerful credential.
- DSH runs with the host account's filesystem and process access. Use a dedicated OS account/container, a narrowly scoped workspace, and a least-privilege DSH profile.
- Do not add a generic shell/command MCP tool or automatically grant DSH approvals. The C# SDK's approval callback is fail-closed unless an explicit handler is configured.
- The email allowlist prevents the model from choosing arbitrary recipients, but it does not replace caller authentication or per-user authorization.
- The MCP API key is shared-key authentication, not OAuth or multi-tenant identity. For multiple users/devices, put a gateway with per-principal authorization in front.

## Verify

Build and test the repository:

```powershell
dotnet build .\dsh-sdk-csharp.slnx --configuration Release
dotnet test .\dsh-sdk-csharp.slnx --configuration Release
```

The MCP service can be verified without making a DSH model call by sending an authenticated MCP `initialize` and `tools/list` request to `/mcp`.
