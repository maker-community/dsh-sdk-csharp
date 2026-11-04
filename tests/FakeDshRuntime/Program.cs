using System.Text.Json.Nodes;

while (Console.ReadLine() is { } line)
{
    var request = JsonNode.Parse(line)?.AsObject()
        ?? throw new InvalidDataException("Expected a JSON-RPC object.");
    var capturePath = Environment.GetEnvironmentVariable("FAKE_DSH_CAPTURE");
    if (!string.IsNullOrWhiteSpace(capturePath))
    {
        await File.AppendAllTextAsync(capturePath, request.ToJsonString() + Environment.NewLine);
    }

    var id = request["id"]?.DeepClone();
    var method = request["method"]?.GetValue<string>();
    if (method is null)
    {
        continue;
    }

    var parameters = request["params"] as JsonObject ?? [];

    switch (method)
    {
        case "initialize":
            await DelayFromEnvironmentAsync("FAKE_DSH_DELAY_INITIALIZE_MS");
            var failureMarker = Environment.GetEnvironmentVariable("FAKE_DSH_FAIL_INITIALIZE_ONCE");
            if (!string.IsNullOrWhiteSpace(failureMarker) && !File.Exists(failureMarker))
            {
                await File.WriteAllTextAsync(failureMarker, "failed once");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32001, message = "Synthetic one-time initialization failure." },
                }));
                break;
            }

            if (Environment.GetEnvironmentVariable("FAKE_DSH_MALFORMED_INITIALIZE") == "1")
            {
                Respond(id, new { serverInfo = (object?)null });
                break;
            }

            if (Environment.GetEnvironmentVariable("FAKE_DSH_SERVER_REQUEST") == "1")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "server-request-1",
                    method = "unsupported/server-method",
                    @params = new { sessionId = "fake-session", toolName = "bash" },
                }));
                var responseLine = await Console.In.ReadLineAsync()
                    ?? throw new InvalidDataException("Expected a response to the server request.");
                var responsePath = Environment.GetEnvironmentVariable("FAKE_DSH_SERVER_RESPONSE");
                if (!string.IsNullOrWhiteSpace(responsePath))
                {
                    await File.WriteAllTextAsync(responsePath, responseLine);
                }
            }

            if (Environment.GetEnvironmentVariable("FAKE_DSH_APPROVAL_REQUEST") == "1")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "approval-request-1",
                    method = "approval/request",
                    @params = new
                    {
                        sessionId = "fake-session",
                        toolName = "bash",
                        callId = "call-1",
                        reason = "The command needs approval.",
                    },
                }));
                await CaptureServerRequestResponseAsync("approval-request-1");
            }

            if (Environment.GetEnvironmentVariable("FAKE_DSH_USER_QUESTION") == "1")
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "user-question-1",
                    method = "user-question/ask",
                    @params = new
                    {
                        requestId = "question-request-1",
                        sessionId = "fake-session",
                        questions = new[]
                        {
                            new
                            {
                                id = "question-1",
                                question = "Proceed?",
                                options = new[] { new { label = "Yes", description = "Continue." } },
                            },
                        },
                    },
                }));
                await CaptureServerRequestResponseAsync("user-question-1");
            }

            Respond(id, new
            {
                serverInfo = new { name = "deepseek-harness-sdk-runtime", version = "test" },
            });
            break;
        case "session/query":
            {
                var sessionId = parameters["sessionId"]?.GetValue<string>()
                    ?? throw new InvalidDataException("sessionId is required.");
                await DelayFromEnvironmentAsync("FAKE_DSH_DELAY_QUERY_MS");
                if (Environment.GetEnvironmentVariable("FAKE_DSH_EXIT_ON_QUERY") == "1")
                {
                    return;
                }

                var promptText = parameters["contentBlocks"]?[0]?["text"]?.GetValue<string>();
                if (Environment.GetEnvironmentVariable("FAKE_DSH_LEGACY_PROTOCOL") == "1")
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id,
                        error = new
                        {
                            code = -32603,
                            message = "unknown DeepSeek Harness SDK runtime method: session/query",
                        },
                    }));
                    break;
                }

                if (Environment.GetEnvironmentVariable("FAKE_DSH_MALFORMED_QUERY") == "1")
                {
                    Respond(id, new { kind = "unknown" });
                    break;
                }

                if (Environment.GetEnvironmentVariable("FAKE_DSH_CANCEL_USER_QUESTION") == "1")
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = "cancelled-question-1",
                        method = "user-question/ask",
                        @params = new
                        {
                            requestId = "cancelled-question-request",
                            sessionId,
                            questions = new[]
                            {
                                new { id = "cancel-question", question = "Continue?" },
                            },
                        },
                    }));
                    Notify("user-question.cancelled", new
                    {
                        requestId = "cancelled-question-request",
                        sessionId,
                    });
                    await Task.Delay(150);
                }

                if (Environment.GetEnvironmentVariable("FAKE_DSH_NOISE") == "1")
                {
                    Notify("session.status", new { sessionId = "unrelated-session", status = "idle" });
                    Notify("session.event", new
                    {
                        sessionId,
                        @event = new { type = "turn/start", data = new { turn = 0 } },
                    });
                }

                if (promptText == "/fake-command")
                {
                    const string commandId = "fake-command-1";
                    Notify("session.event", new
                    {
                        sessionId,
                        @event = new { type = "command/run", data = new { commandId } },
                    });
                    Notify("session.event", new
                    {
                        sessionId,
                        @event = new { type = "command/done", data = new { commandId } },
                    });
                    Notify("session.query.completed", new
                    {
                        sessionId,
                        commandId,
                        finalResponse = "command response",
                        finishReason = "completed",
                    });
                    Respond(id, new
                    {
                        kind = "command",
                        execution = new { commandId },
                    });
                    break;
                }

                var messageId = Guid.NewGuid().ToString("N");
                Respond(id, new { kind = "prompt", messageId });
                Notify("session.event", new
                {
                    sessionId,
                    @event = new
                    {
                        type = "agent/inbox/spliced",
                        data = new { inserted = new[] { new { id = messageId } } },
                    },
                });
                Notify("session.status", new { sessionId, status = "running" });
                if (Environment.GetEnvironmentVariable("FAKE_DSH_MALFORMED_ASSISTANT") == "1")
                {
                    Notify("session.event", new
                    {
                        sessionId,
                        @event = new
                        {
                            type = "assistant/message",
                            data = new { message = new { content = "not-an-array" } },
                        },
                    });
                    break;
                }

                Notify("session.event", new
                {
                    sessionId,
                    @event = new
                    {
                        type = "assistant/message",
                        data = new { message = new { content = new[] { new { type = "text", text = "fake response" } } } },
                    },
                });
                if (Environment.GetEnvironmentVariable("FAKE_DSH_SUBAGENT") == "1")
                {
                    Notify("subagent.started", new { parentSessionId = sessionId, childSessionId = "fake-child" });
                    Notify("session.event", new
                    {
                        sessionId = "fake-child",
                        @event = new
                        {
                            type = "assistant/message",
                            data = new
                            {
                                message = new
                                {
                                    content = new[] { new { type = "text", text = "child-only response" } },
                                },
                            },
                        },
                    });
                    Notify("subagent.finished", new
                    {
                        parentSessionId = sessionId,
                        childSessionId = "fake-child",
                        status = "ok",
                        stopReason = "completed",
                    });
                }

                Notify("session.event", new
                {
                    sessionId,
                    @event = new { type = "turn/end", data = new { reason = new { kind = "completed" } } },
                });
                Notify("session.status", new { sessionId, status = "idle" });
                break;
            }
        case "session/prompt":
            {
                var sessionId = parameters["sessionId"]?.GetValue<string>()
                    ?? throw new InvalidDataException("sessionId is required.");
                var messageId = Guid.NewGuid().ToString("N");
                Respond(id, new { messageId });
                Notify("session.event", new
                {
                    sessionId,
                    @event = new
                    {
                        type = "agent/inbox/spliced",
                        data = new { inserted = new[] { new { id = messageId } } },
                    },
                });
                Notify("session.status", new { sessionId, status = "running" });
                Notify("session.event", new
                {
                    sessionId,
                    @event = new
                    {
                        type = "assistant/message",
                        data = new { message = new { content = new[] { new { type = "text", text = "legacy response" } } } },
                    },
                });
                Notify("session.event", new
                {
                    sessionId,
                    @event = new { type = "turn/end", data = new { reason = new { kind = "completed" } } },
                });
                Notify("session.status", new { sessionId, status = "idle" });
                break;
            }
        case "session/wait":
        case "session/resume":
            if (Environment.GetEnvironmentVariable("FAKE_DSH_RESUME_ERROR") == "1")
            {
                RespondError(id, -32004, "session not found");
            }
            else
            {
                Respond(id, new { });
            }
            break;
        case "session/working-directory/get":
            if (Environment.GetEnvironmentVariable("FAKE_DSH_MALFORMED_DIRECTORY") == "1")
            {
                Respond(id, new { });
            }
            else
            {
                Respond(id, new { cwd = Environment.CurrentDirectory });
            }
            break;
        case "session/working-directory/set":
            if (Environment.GetEnvironmentVariable("FAKE_DSH_MALFORMED_DIRECTORY") == "1")
            {
                Respond(id, new { });
            }
            else
            {
                var path = parameters["path"]?.GetValue<string>()
                    ?? throw new InvalidDataException("path is required.");
                Respond(id, new { cwd = Path.GetFullPath(path) });
            }
            break;
        case "shutdown":
            Respond(id, new { });
            return;
        default:
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32601, message = $"Unknown method {method}" },
            }));
            break;
    }
}

static void Respond(JsonNode? id, object result) =>
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));

static void Notify(string method, object parameters) =>
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));

static void RespondError(JsonNode? id, int code, string message) =>
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message },
    }));

static async Task DelayFromEnvironmentAsync(string name)
{
    if (int.TryParse(Environment.GetEnvironmentVariable(name), out var milliseconds) && milliseconds > 0)
    {
        await Task.Delay(milliseconds);
    }
}

static async Task CaptureServerRequestResponseAsync(string requestId)
{
    var responseLine = await Console.In.ReadLineAsync()
        ?? throw new InvalidDataException("Expected a response to the server request.");
    var responsePath = Environment.GetEnvironmentVariable("FAKE_DSH_SERVER_RESPONSE");
    if (!string.IsNullOrWhiteSpace(responsePath))
    {
        await File.AppendAllTextAsync(
            responsePath,
            requestId + ":" + responseLine + Environment.NewLine);
    }
}
