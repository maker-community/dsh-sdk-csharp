# DSH MCP Bridge

An ASP.NET Core MCP server that exposes the C# DSH SDK as a Streamable HTTP MCP endpoint.

The first version exposes one tool, `dsh_run`, which runs a prompt against the configured DSH profile and returns the final response. It can optionally email the result to an address explicitly listed in `Email:AllowedRecipients` (or the legacy `DSH_EMAIL_ALLOWED_RECIPIENTS` setting).

## Requirements

- .NET 10
- The official DSH CLI installed on the same host as this service
- A configured DSH home/profile and model provider
- A randomly generated MCP API key

The MCP service starts DSH lazily on the first `dsh_run` call. MCP calls share a DSH process and its default Session for the lifetime of the service. An optional `conversationId` selects a separate Session for calls that use the same value. Session ids are scoped to this service process; automatic Session restore after an MCP service restart is not implemented.

The service launches the configured local DSH executable through the C# SDK; it does not call a remote DSH service. The SDK uses its default provider/model route and supplies the protocol-required `provider` and `model` fields during initialization. The MCP service has no separate settings for these values; DSH credentials and profile configuration remain in the configured local DSH home.

## Configure and run

The project includes an `appsettings.json` with non-secret defaults for local development. ASP.NET Core loads it automatically; environment variables and command-line configuration override these values. `MCP_API_KEY` is intentionally not included: provide a random key of at least 32 characters through a secret store or environment variable. Do not put production credentials in the checked-in settings file.

PowerShell example:

```powershell
$env:MCP_API_KEY = [Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ASPNETCORE_URLS = "http://127.0.0.1:8000"
$env:DSH_EXECUTABLE = "C:\path\to\dsh.cmd"
$env:DSH_HOME = "$HOME\.dsh"
$env:DSH_PROFILE = "sdk"
$env:DSH_WORKSPACE = "$HOME\workspaces\dsh-mcp"

dotnet run --project .\src\Dsh.Sdk.CSharp.McpServer\Dsh.Sdk.CSharp.McpServer.csproj
```

The API key is required and must be at least 32 characters. The service never logs or returns it. Every request to `/mcp` must include:

```http
Authorization: Bearer <MCP_API_KEY>
```

`/health` is unauthenticated and returns only a basic liveness status. The server rejects browser `Origin` headers unless their exact origin is included in `MCP_ALLOWED_ORIGINS` (comma- or semicolon-separated). Keep the MCP endpoint on a private network or behind HTTPS and a trusted reverse proxy; do not expose plain HTTP to the public internet.

## MCP client integration

This service exposes a standard Streamable HTTP MCP endpoint for compatible clients. Device-specific protocol adaptation belongs in a separate gateway. For Xiaozhi and Tuya smart-device integration, see [verdure-mcp-for-xiaozhi](https://github.com/maker-community/verdure-mcp-for-xiaozhi).

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
| `DSH_EXECUTABLE` | `dsh` | DSH executable path; on Windows Desktop, use the bundled `dsh.cmd` |
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
