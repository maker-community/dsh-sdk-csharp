# Project Guidelines

## Project boundaries

- This is a community C#/.NET SDK for the official DeepSeek Harness (DSH). The SDK starts the official DSH process and communicates through its stdio JSON-RPC protocol; do not reimplement the Harness or bypass the SDK from the MCP service.
- The SDK protocol does not provide a session-list operation. Do not promise session discovery or restoration beyond the behavior documented by the SDK.
- The MCP server is a separate ASP.NET Core application exposing Streamable HTTP at `/mcp`; `/health` is a liveness endpoint. Treat device-specific protocol adapters as separate clients/gateways; do not assume that a device connects directly to this MCP endpoint.
- `dsh_run` is synchronous. Do not describe or implement it as a durable job queue, pollable job API, or built-in Cron scheduler unless those capabilities are actually added and verified. Conversation IDs are not user identities or authorization boundaries.
- See [README.md](./README.md), [README.zh-CN.md](./README.zh-CN.md), and the [MCP Server README](./src/Dsh.Sdk.CSharp.McpServer/README.md) for user-facing behavior, configuration, and examples. Keep those documents authoritative for operational details.

## Architecture and dependency direction

- Keep SDK protocol/process concerns in the SDK and MCP transport/tool concerns in the MCP Server. Put reusable orchestration behind narrow, purpose-specific interfaces where it creates a real substitution or test seam.
- MCP tool handlers and application orchestration should prefer depending on contracts rather than concrete infrastructure implementations (for example, DSH execution or email delivery). Register implementations through dependency injection and cover the wiring with tests.
- Avoid adding interfaces or abstractions solely for indirection; keep the dependency direction clear and do not make the SDK depend on the MCP Server.
- When changing a service contract, update its implementation, DI registration, tool behavior, and relevant tests together.

## Security and behavior

- Require authenticated access to `/mcp` using the configured bearer API key; preserve the minimum key length and Origin protections. Never log, return, or commit secrets.
- Keep email delivery optional and restricted to the exact configured recipient allowlist. Report delivery failures explicitly; do not claim an email was sent when it failed.
- DSH runs with the host account's permissions. Preserve least-privilege execution and narrowly scoped workspaces; do not add generic shell tools or enable approvals by default.
- For Windows DSH ACL/sandbox errors, do not recommend Full Access or broad ACL repair scripts as a workaround.
- Surface configuration and execution errors explicitly. Do not silently convert failures into successful-looking results.

## Build and test

Use the repository's .NET SDK and validate changes with:

```powershell
dotnet build .\dsh-sdk-csharp.slnx --configuration Release
dotnet test .\dsh-sdk-csharp.slnx --configuration Release
```

For code changes, run the smallest relevant test set first when practical, then the solution checks when the change crosses project boundaries.
