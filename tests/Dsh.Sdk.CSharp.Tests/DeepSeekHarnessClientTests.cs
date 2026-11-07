using DeepSeek.Harness.Sdk;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.Tests;

public sealed class DeepSeekHarnessClientTests
{
    [Fact]
    public async Task RunAsync_CollectsReceiptMatchedTurnAndShutsRuntimeDown()
    {
        var fakeRuntimeProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime",
            "FakeDshRuntime.csproj"));

        await using var client = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
        {
            DshExecutable = "dotnet",
            DshArguments = ["run", "--no-build", "--configuration", "Release", "--project", fakeRuntimeProject, "--"],
            WorkingDirectory = Environment.CurrentDirectory,
        });

        var result = await client.RunAsync("Say hello.");

        Assert.StartsWith("session-", result.SessionId);
        Assert.Equal("fake response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(5, result.Notifications.Count);
    }

    [Fact]
    public async Task RunAsync_RejectsBlankInputBeforeLaunchingRuntime()
    {
        await using var client = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
        {
            DshExecutable = "dsh-that-does-not-exist",
        });

        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync("  "));
    }

    [Fact]
    public async Task RunAsync_CollectsCorrelatedNativeCommandCompletion()
    {
        var fakeRuntimeProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime",
            "FakeDshRuntime.csproj"));

        await using var client = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
        {
            DshExecutable = "dotnet",
            DshArguments = ["run", "--no-build", "--configuration", "Release", "--project", fakeRuntimeProject, "--"],
            WorkingDirectory = Environment.CurrentDirectory,
        });

        var result = await client.RunAsync("/fake-command");

        Assert.Equal("command response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(3, result.Notifications.Count);
    }

    [Fact]
    public async Task RunAsync_FallsBackToPromptWhenRuntimeDoesNotSupportQuery()
    {
        var fakeRuntimeProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime",
            "FakeDshRuntime.csproj"));

        await using var client = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
        {
            DshExecutable = "dotnet",
            DshArguments = ["run", "--no-build", "--configuration", "Release", "--project", fakeRuntimeProject, "--"],
            WorkingDirectory = Environment.CurrentDirectory,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["FAKE_DSH_LEGACY_PROTOCOL"] = "1",
            },
        });

        var result = await client.RunAsync("Say hello.");

        Assert.Equal("legacy response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(5, result.Notifications.Count);
    }

    [Fact]
    public async Task RunAsync_ResumesExistingSessionAndRetriesQuery()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_QUERY_SESSION_EXISTS"] = "1" }));

        var result = await client.RunAsync("Continue the existing session.", "existing-session");

        Assert.Equal("fake response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
        Assert.Equal(3, result.Events.Count);
        Assert.Equal(5, result.Notifications.Count);
    }

    [Fact]
    public async Task RunAsync_ResumesExistingSessionWhenLegacyPromptReportsCollision()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_LEGACY_PROTOCOL"] = "1",
                ["FAKE_DSH_PROMPT_SESSION_EXISTS"] = "1",
            }));

        var result = await client.RunAsync("Continue the existing session.", "existing-session");

        Assert.Equal("legacy response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
    }

    [Fact]
    public async Task RunAsync_PreservesCommandRoutingAfterResumingExistingSession()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_QUERY_SESSION_EXISTS"] = "1" }));

        var result = await client.RunAsync("/fake-command", "existing-session");

        Assert.Equal("command response", result.FinalResponse);
        Assert.Equal("completed", result.FinishReason);
    }

    [Fact]
    public async Task RunAsync_DoesNotResumeWhenCollisionRefersToAnotherSession()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_QUERY_OTHER_SESSION_EXISTS"] = "1" }));

        var exception = await Assert.ThrowsAsync<HarnessRpcException>(
            () => client.RunAsync("Run a task.", "existing-session"));

        Assert.Equal(-32603, exception.Code);
        Assert.Contains("another-session", exception.RpcMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PropagatesResumeFailureForExistingSession()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_QUERY_SESSION_EXISTS"] = "1",
                ["FAKE_DSH_RESUME_ERROR"] = "1",
            }));

        var exception = await Assert.ThrowsAsync<HarnessRpcException>(
            () => client.RunAsync("Continue the existing session.", "existing-session"));

        Assert.Equal(-32004, exception.Code);
        Assert.Contains("session not found", exception.RpcMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_ReapsFailedRuntimeAndAllowsRetry()
    {
        var fakeRuntimeProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime",
            "FakeDshRuntime.csproj"));
        var failureMarker = Path.Combine(Path.GetTempPath(), $"dsh-sdk-{Guid.NewGuid():N}.marker");

        await using var client = new DeepSeekHarnessClient(new DeepSeekHarnessOptions
        {
            DshExecutable = "dotnet",
            DshArguments = ["run", "--no-build", "--configuration", "Release", "--project", fakeRuntimeProject, "--"],
            WorkingDirectory = Environment.CurrentDirectory,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["FAKE_DSH_FAIL_INITIALIZE_ONCE"] = failureMarker,
            },
        });

        try
        {
            await Assert.ThrowsAsync<HarnessRpcException>(() => client.StartAsync());
            await client.StartAsync();
            Assert.True(client.IsInitialized);
        }
        finally
        {
            if (File.Exists(failureMarker))
            {
                File.Delete(failureMarker);
            }
        }
    }

    [Fact]
    public void Options_RejectNonPositiveTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeepSeekHarnessClient(
            new DeepSeekHarnessOptions { RequestTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Options_UseOfficialDeepSeekDefaults()
    {
        var options = new DeepSeekHarnessOptions();

        Assert.Equal("deepseek-official", options.Provider);
        Assert.Equal("deepseek-flash", options.Model);
    }

    [Fact]
    public async Task StartAndRunAsync_SendConfiguredRouteAndOrderedImageBlocks()
    {
        var capturePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-capture-{Guid.NewGuid():N}.jsonl");
        var workingDirectory = Environment.CurrentDirectory;
        var options = CreateFakeOptions(new Dictionary<string, string?>
        {
            ["FAKE_DSH_CAPTURE"] = capturePath,
        });
        options = new DeepSeekHarnessOptions
        {
            DshExecutable = options.DshExecutable,
            DshArguments = options.DshArguments,
            WorkingDirectory = workingDirectory,
            Provider = "test-provider",
            Model = "test-model",
            ReasoningEffort = "high",
            MaxTokens = 512,
            EnvironmentVariables = options.EnvironmentVariables,
        };

        try
        {
            await using (var client = new DeepSeekHarnessClient(options))
            {
                await client.StartAsync();
                await client.StartAsync();
                Assert.True(client.IsInitialized);

                var result = await client.RunAsync(
                [
                    PromptContentBlock.FromText("Describe this image."),
                    PromptContentBlock.FromImage(new PromptImage("aW1hZ2U=", "image/png", "sample.png")),
                ],
                    "image-session");
                Assert.Equal("fake response", result.FinalResponse);
            }

            var requests = File.ReadAllLines(capturePath)
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToArray();
            Assert.Equal(["initialize", "session/query", "shutdown"], requests
                .Select(request => request.GetProperty("method").GetString()).ToArray());

            var initialize = requests[0].GetProperty("params");
            Assert.Equal(Path.GetFullPath(workingDirectory), initialize.GetProperty("cwd").GetString());
            Assert.Equal("test-provider", initialize.GetProperty("provider").GetString());
            Assert.Equal("test-model", initialize.GetProperty("model").GetString());
            Assert.Equal("high", initialize.GetProperty("reasoningEffort").GetString());
            Assert.Equal(512, initialize.GetProperty("maxTokens").GetInt32());

            var query = requests[1].GetProperty("params");
            Assert.Equal("image-session", query.GetProperty("sessionId").GetString());
            Assert.Equal("text", query.GetProperty("contentBlocks")[0].GetProperty("type").GetString());
            Assert.Equal("image", query.GetProperty("contentBlocks")[1].GetProperty("type").GetString());
            Assert.Equal("aW1hZ2U=", query.GetProperty("contentBlocks")[1].GetProperty("data").GetString());
            Assert.Equal("image/png", query.GetProperty("contentBlocks")[1].GetProperty("mimeType").GetString());
        }
        finally
        {
            if (File.Exists(capturePath))
            {
                File.Delete(capturePath);
            }
        }
    }

    [Fact]
    public async Task PromptResumeWaitAndWorkingDirectoryMethodsUseExpectedResponses()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions());

        var messageId = await client.PromptAsync("low-level-session", "queue this");
        await client.ResumeSessionAsync("saved-session");
        await client.WaitForSessionAsync("low-level-session");
        var originalDirectory = await client.GetWorkingDirectoryAsync("directory-session");
        var changedDirectory = await client.SetWorkingDirectoryAsync("directory-session", "child");

        Assert.False(string.IsNullOrWhiteSpace(messageId));
        Assert.Equal(Path.GetFullPath(Path.Combine(originalDirectory, "child")), changedDirectory);
    }

    [Fact]
    public async Task ResumeSessionAsync_PropagatesRuntimeError()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_RESUME_ERROR"] = "1" }));

        var exception = await Assert.ThrowsAsync<HarnessRpcException>(
            () => client.ResumeSessionAsync("missing-session"));

        Assert.Equal(-32004, exception.Code);
        Assert.Contains("session not found", exception.RpcMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkingDirectoryMethods_RejectMalformedResponses()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_MALFORMED_DIRECTORY"] = "1" }));

        await Assert.ThrowsAsync<HarnessProtocolException>(
            () => client.GetWorkingDirectoryAsync("directory-session"));
        await Assert.ThrowsAsync<HarnessProtocolException>(
            () => client.SetWorkingDirectoryAsync("directory-session", "child"));
    }

    [Fact]
    public async Task RunAsync_ExposesNotificationsToObserversAndSubscriptions()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions());
        await using var subscription = client.SubscribeNotifications();
        var observed = new List<string>();

        var result = await client.RunAsync(
            "Say hello.",
            onNotification: notification => observed.Add(notification.Method));

        Assert.Contains("session.event", observed);
        Assert.Contains("session.status", observed);
        var subscribedCount = 0;
        while (subscription.TryRead(out var notification) && notification is not null)
        {
            subscribedCount++;
        }

        Assert.Equal(result.Notifications.Count, subscribedCount);
    }

    [Fact]
    public async Task RunAsync_IgnoresPreReceiptNoiseAndKeepsChildEventsOutOfRootEvents()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_NOISE"] = "1",
                ["FAKE_DSH_SUBAGENT"] = "1",
            }));

        var result = await client.RunAsync("Delegate this.", "parent-session");

        Assert.DoesNotContain(result.Events, item =>
            item.GetProperty("type").GetString() == "turn/start");
        Assert.DoesNotContain(result.Events, item =>
            item.TryGetProperty("data", out var data)
            && data.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ToString().Contains("child-only response", StringComparison.Ordinal));
        Assert.Contains(result.Notifications, notification => notification.Method == "subagent.started");
        Assert.Contains(result.Notifications, notification =>
            notification.Method == "session.event"
            && notification.Parameters.GetProperty("sessionId").GetString() == "fake-child");
        Assert.Contains(result.Notifications, notification => notification.Method == "subagent.finished");
    }

    [Fact]
    public async Task RunAsync_PropagatesObserverExceptions()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions());
        var observerFailure = new InvalidOperationException("observer failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RunAsync("Say hello.", onNotification: _ => throw observerFailure));

        Assert.Same(observerFailure, exception);
    }

    [Fact]
    public async Task RunAsync_RejectsMalformedQueryResult()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_MALFORMED_QUERY"] = "1" }));

        await Assert.ThrowsAsync<HarnessProtocolException>(() => client.RunAsync("Say hello."));
    }

    [Fact]
    public async Task RunAsync_RejectsMalformedAssistantEvent()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_MALFORMED_ASSISTANT"] = "1" }));

        await Assert.ThrowsAsync<HarnessProtocolException>(() => client.RunAsync("Say hello."));
    }

    [Fact]
    public async Task StartAsync_RejectsUnsupportedServerRequestExplicitly()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-server-request-{Guid.NewGuid():N}.json");
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_SERVER_REQUEST"] = "1",
                ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
            }));

        try
        {
            await client.StartAsync();
            using var response = JsonDocument.Parse(await File.ReadAllTextAsync(responsePath));
            Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Contains(
                "Unsupported server request",
                response.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_HandlesApprovalRequestOnlyWhenExplicitlyEnabled()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-approval-{Guid.NewGuid():N}.jsonl");
        ApprovalRequest? received = null;
        var options = CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_APPROVAL_REQUEST"] = "1",
                ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                ["FAKE_DSH_CAPTURE"] = responsePath + ".requests",
            },
            approvalRequests: true,
            onApprovalRequest: (request, _) =>
            {
                received = request;
                return Task.FromResult(ApprovalRequestDecision.AllowOnce);
            });

        try
        {
            await using (var client = new DeepSeekHarnessClient(options))
            {
                await client.StartAsync();
            }

            Assert.NotNull(received);
            Assert.Equal("fake-session", received.SessionId);
            Assert.Equal("bash", received.ToolName);
            Assert.Equal("call-1", received.CallId);
            Assert.Equal("The command needs approval.", received.Reason);

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            Assert.Equal("approval-request-1", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("allowed-once", document.RootElement.GetProperty("result").GetProperty("outcome").GetString());
            using var initialize = JsonDocument.Parse(File.ReadLines(responsePath + ".requests").First());
            Assert.True(initialize.RootElement.GetProperty("params").GetProperty("approvalRequests").GetBoolean());
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }

            if (File.Exists(responsePath + ".requests"))
            {
                File.Delete(responsePath + ".requests");
            }
        }
    }

    [Fact]
    public async Task StartAsync_RejectsApprovalWhenNoHandlerIsConfigured()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-approval-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var client = new DeepSeekHarnessClient(CreateFakeOptions(
                new Dictionary<string, string?>
                {
                    ["FAKE_DSH_APPROVAL_REQUEST"] = "1",
                    ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                })))
            {
                await client.StartAsync();
            }

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_MapsApprovalCallbackFailureToJsonRpcError()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-approval-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var client = new DeepSeekHarnessClient(CreateFakeOptions(
                new Dictionary<string, string?>
                {
                    ["FAKE_DSH_APPROVAL_REQUEST"] = "1",
                    ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                },
                approvalRequests: true,
                onApprovalRequest: (_, _) => throw new InvalidOperationException("do not expose details"))))
            {
                await client.StartAsync();
            }

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            Assert.Equal(-32603, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.DoesNotContain(
                "do not expose details",
                document.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_AnswersUserQuestionAndAdvertisesCapability()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-question-{Guid.NewGuid():N}.jsonl");
        SdkUserQuestionRequest? received = null;
        var options = CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_USER_QUESTION"] = "1",
                ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                ["FAKE_DSH_CAPTURE"] = responsePath + ".requests",
            },
            onUserQuestion: (request, _) =>
            {
                received = request;
                return Task.FromResult(SdkUserQuestionResponse.Answer(
                    [new SdkUserQuestionAnswer("question-1", ["Yes"])]));
            });

        try
        {
            await using (var client = new DeepSeekHarnessClient(options))
            {
                await client.StartAsync();
            }

            Assert.NotNull(received);
            Assert.Equal("question-request-1", received.RequestId);
            Assert.Equal("fake-session", received.SessionId);
            Assert.Equal("Proceed?", received.Questions[0].GetProperty("question").GetString());

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var responseDocument = JsonDocument.Parse(response);
            Assert.Equal("user-question-1", responseDocument.RootElement.GetProperty("id").GetString());
            var answer = responseDocument.RootElement.GetProperty("result").GetProperty("answers")[0];
            Assert.Equal("question-1", answer.GetProperty("id").GetString());
            Assert.Equal("Yes", answer.GetProperty("selected")[0].GetString());
            Assert.False(answer.TryGetProperty("custom", out _));

            var initialize = File.ReadLines(responsePath + ".requests")
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .First(frame => frame.GetProperty("method").GetString() == "initialize");
            Assert.True(initialize.GetProperty("params").GetProperty("capabilities")
                .GetProperty("userQuestions").GetBoolean());
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }

            if (File.Exists(responsePath + ".requests"))
            {
                File.Delete(responsePath + ".requests");
            }
        }
    }

    [Fact]
    public async Task StartAsync_UserQuestionCallbackCanDismissWithoutInventingAnswers()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-question-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var client = new DeepSeekHarnessClient(CreateFakeOptions(
                new Dictionary<string, string?>
                {
                    ["FAKE_DSH_USER_QUESTION"] = "1",
                    ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                },
                onUserQuestion: (_, _) => Task.FromResult(SdkUserQuestionResponse.Dismiss()))))
            {
                await client.StartAsync();
            }

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            var result = document.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("cancelled").GetBoolean());
            Assert.False(result.TryGetProperty("answers", out _));
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_RejectsMalformedUserQuestionAnswers()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-question-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var client = new DeepSeekHarnessClient(CreateFakeOptions(
                new Dictionary<string, string?>
                {
                    ["FAKE_DSH_USER_QUESTION"] = "1",
                    ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                },
                onUserQuestion: (_, _) => Task.FromResult(SdkUserQuestionResponse.Answer([])))))
            {
                await client.StartAsync();
            }

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            Assert.Equal(-32603, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_MapsUserQuestionCallbackFailureToJsonRpcError()
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-question-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var client = new DeepSeekHarnessClient(CreateFakeOptions(
                new Dictionary<string, string?>
                {
                    ["FAKE_DSH_USER_QUESTION"] = "1",
                    ["FAKE_DSH_SERVER_RESPONSE"] = responsePath,
                },
                onUserQuestion: (_, _) => throw new InvalidOperationException("private callback failure"))))
            {
                await client.StartAsync();
            }

            var response = File.ReadAllText(responsePath).Split(':', 2)[1];
            using var document = JsonDocument.Parse(response);
            Assert.Equal(-32603, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.DoesNotContain(
                "private callback failure",
                document.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_CancelsUserQuestionAndIgnoresLateCallbackAnswer()
    {
        var capturePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-question-cancel-{Guid.NewGuid():N}.jsonl");
        var callbackCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_CANCEL_USER_QUESTION"] = "1",
                ["FAKE_DSH_CAPTURE"] = capturePath,
            },
            onUserQuestion: async (request, cancellationToken) =>
            {
                Assert.Equal("cancelled-question-request", request.RequestId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    callbackCancelled.TrySetResult();
                    await Task.Delay(50);
                    return SdkUserQuestionResponse.Answer(
                        [new SdkUserQuestionAnswer("cancel-question", ["Continue"])]);
                }

                throw new InvalidOperationException("The scripted user question was not cancelled.");
            });

        await using var client = new DeepSeekHarnessClient(options);
        var result = await client.RunAsync("Exercise cancellation.", "question-session");
        await callbackCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("fake response", result.FinalResponse);
        var frames = File.ReadAllLines(capturePath)
            .Select(line => JsonDocument.Parse(line))
            .Select(document => document.RootElement.Clone())
            .ToArray();
        Assert.DoesNotContain(frames, frame =>
            frame.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() == "cancelled-question-1");
        File.Delete(capturePath);
    }

    [Fact]
    public async Task RunAsync_ReportsRequestTimeoutWithoutRetryingAcceptedWork()
    {
        var capturePath = Path.Combine(Path.GetTempPath(), $"dsh-sdk-timeout-{Guid.NewGuid():N}.jsonl");
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?>
            {
                ["FAKE_DSH_DELAY_QUERY_MS"] = "250",
                ["FAKE_DSH_CAPTURE"] = capturePath,
            },
            requestTimeout: TimeSpan.FromMilliseconds(50)));

        try
        {
            var exception = await Assert.ThrowsAsync<HarnessRequestTimeoutException>(
                () => client.RunAsync("Slow response."));
            Assert.Equal("session/query", exception.Method);
            await Task.Delay(300);
        }
        finally
        {
            await client.DisposeAsync();
            if (File.Exists(capturePath))
            {
                var methods = File.ReadAllLines(capturePath)
                    .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString())
                    .ToArray();
                Assert.Single(methods.Where(method => method == "session/query"));
                File.Delete(capturePath);
            }
        }
    }

    [Fact]
    public async Task StartAsync_ReportsMalformedInitializeAndCleansUpRuntime()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_MALFORMED_INITIALIZE"] = "1" }));

        await Assert.ThrowsAsync<HarnessProtocolException>(() => client.StartAsync());
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task StartAsync_EnforcesInitializationTimeoutAndReapsRuntime()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_DELAY_INITIALIZE_MS"] = "250" },
            initializationTimeout: TimeSpan.FromMilliseconds(50)));

        var exception = await Assert.ThrowsAsync<HarnessRequestTimeoutException>(() => client.StartAsync());

        Assert.Equal("initialize", exception.Method);
        Assert.False(client.IsInitialized);
    }

    [Fact]
    public async Task RunAsync_ReportsUnexpectedRuntimeExit()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_EXIT_ON_QUERY"] = "1" }));

        await Assert.ThrowsAsync<HarnessTransportException>(() => client.RunAsync("Exit now."));
    }

    [Fact]
    public async Task StartAsync_IncludesChildStderrWhenPipeClosesBeforeHandshake()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions(
            new Dictionary<string, string?> { ["FAKE_DSH_EXIT_BEFORE_REQUEST"] = "1" }));

        var exception = await Assert.ThrowsAsync<HarnessTransportException>(() => client.StartAsync());

        Assert.Contains("The DSH runtime exited", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Synthetic DSH startup failure before initialize.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SerializesCallsForTheSameSession()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions());

        var results = await Task.WhenAll(
            client.RunAsync("First.", "shared-session"),
            client.RunAsync("Second.", "shared-session"));

        Assert.Equal(2, results.Length);
        Assert.All(results, result => Assert.Equal("shared-session", result.SessionId));
        Assert.All(results, result => Assert.Equal("fake response", result.FinalResponse));
    }

    [Fact]
    public async Task RunAsync_RejectsInvalidImageBlocksBeforeStartingRuntime()
    {
        await using var client = new DeepSeekHarnessClient(CreateFakeOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(
            [PromptContentBlock.FromImage(new PromptImage("not-base64!", "image/png"))]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(
            [PromptContentBlock.FromImage(new PromptImage("aW1hZ2U=", "image/svg+xml"))]));
    }

    private static DeepSeekHarnessOptions CreateFakeOptions(
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? initializationTimeout = null,
        bool approvalRequests = false,
        ApprovalRequestHandler? onApprovalRequest = null,
        UserQuestionHandler? onUserQuestion = null)
    {
        var fakeRuntimeProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime",
            "FakeDshRuntime.csproj"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";

        return new DeepSeekHarnessOptions
        {
            DshExecutable = "dotnet",
            DshArguments =
            [
                "run", "--no-build", "--configuration", configuration, "--project", fakeRuntimeProject, "--",
            ],
            WorkingDirectory = Environment.CurrentDirectory,
            RequestTimeout = requestTimeout,
            InitializationTimeout = initializationTimeout ?? TimeSpan.FromSeconds(10),
            ApprovalRequests = approvalRequests,
            OnApprovalRequest = onApprovalRequest,
            OnUserQuestion = onUserQuestion,
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string?>(),
        };
    }
}
