using DeepSeek.Harness.Sdk;
using Dsh.Sdk.CSharp.McpServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Threading.Channels;

namespace Dsh.Sdk.CSharp.Tests;

[CollectionDefinition("MCP SQLite", DisableParallelization = true)]
public sealed class McpSqliteTestCollection;

[Collection("MCP SQLite")]
public sealed class McpAsyncWorkflowTests
{
    [Fact]
    public void CallerContext_ReadsIdentityFromGatewayHeaders()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "xiaozhi-device-1";
        httpContext.Request.Headers["X-User-Email"] = "one@example.com";
        var callerContext = new McpCallerContext(new HttpContextAccessor
        {
            HttpContext = httpContext,
        });

        var caller = callerContext.GetRequired();

        Assert.Equal("xiaozhi-device-1", caller.UserId);
        Assert.Equal("one@example.com", caller.Email);
    }

    [Fact]
    public void CallerContext_RejectsMissingIdentityHeaders()
    {
        var callerContext = new McpCallerContext(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        });

        Assert.Throws<InvalidOperationException>(() => callerContext.GetRequired());
    }

    [Fact]
    public void CallerContext_AllowsMissingEmailForNonNotificationCalls()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "xiaozhi-device-1";
        var callerContext = new McpCallerContext(new HttpContextAccessor
        {
            HttpContext = httpContext,
        });

        var caller = callerContext.GetRequired();

        Assert.Equal("xiaozhi-device-1", caller.UserId);
        Assert.Empty(caller.Email);
        Assert.Throws<InvalidOperationException>(() => callerContext.GetRequired(requireEmail: true));
    }

    [Fact]
    public async Task EmailService_WhenDisabledDoesNotRequireRecipientOrSmtp()
    {
        var configuration = new ConfigurationBuilder().Build();
        var settings = new McpBridgeSettings
        {
            ApiKey = new string('k', 32),
            EmailNotificationsEnabled = false,
        };
        var email = new EmailNotificationService(configuration, settings);

        email.ValidateRecipient("local-test@example.invalid");
        var result = await email.SendMessageAsync(
            "local-test@example.invalid",
            "local test",
            "Do not send",
            CancellationToken.None);

        Assert.False(email.IsEnabled);
        Assert.Equal("disabled", result.Status);
    }

    [Fact]
    public async Task Store_KeepsActiveSessionsPerOwnerAndQueuesResultEmail()
    {
        var (directory, store) = await CreateStoreAsync();
        try
        {
            var first = await store.GetOrCreateActiveSessionAsync(
                "user-1", "one@example.com", CancellationToken.None);
            var otherUser = await store.GetOrCreateActiveSessionAsync(
                "user-2", "two@example.com", CancellationToken.None);
            Assert.NotEqual(first.Id, otherUser.Id);

            var second = await store.CreateAndActivateSessionAsync(
                "user-1", "one@example.com", "Work", CancellationToken.None);
            Assert.NotEqual(first.DshSessionId, second.DshSessionId);
            await store.SwitchActiveSessionAsync(
                "user-1", "one@example.com", first.Id, CancellationToken.None);

            var job = await store.SubmitJobAsync(
                "user-1", "one@example.com", "run a long task", CancellationToken.None);
            Assert.Equal(first.Id, job.ConversationId);
            Assert.Equal("queued", job.Status);

            var claimed = await store.TryClaimNextJobAsync(CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(job.Id, claimed.Id);
            Assert.Equal("running", claimed.Status);

            await store.CompleteJobAsync(
                job.Id,
                new HarnessRunResult(
                    job.DshSessionId,
                    "Completed result",
                    "completed",
                    [],
                    []),
                CancellationToken.None);

            var completed = await store.GetJobAsync("user-1", job.Id, CancellationToken.None);
            Assert.NotNull(completed);
            Assert.Equal("completed", completed.Status);
            Assert.Equal("queued", completed.EmailStatus);

            var mail = await store.TryClaimNextEmailAsync(CancellationToken.None);
            Assert.NotNull(mail);
            Assert.Equal("one@example.com", mail.Recipient);
            Assert.Contains("Completed result", mail.Body, StringComparison.Ordinal);

            await store.MarkEmailSentAsync(mail.Id, CancellationToken.None);
            var delivered = await store.GetJobAsync("user-1", job.Id, CancellationToken.None);
            Assert.Equal("sent", delivered!.EmailStatus);

            var reopenedStore = new SqliteMcpStateStore(
                new McpBridgeSettings
                {
                    ApiKey = new string('k', 32),
                    DatabasePath = Path.Combine(directory, "state.db"),
                    EmailNotificationsEnabled = true,
                },
                NullLogger<SqliteMcpStateStore>.Instance);
            await reopenedStore.InitializeAsync(CancellationToken.None);
            var active = await store.GetOrCreateActiveSessionAsync(
                "user-1", "one@example.com", CancellationToken.None);
            Assert.Equal(first.Id, active.Id);
            var persisted = await reopenedStore.GetOrCreateActiveSessionAsync(
                "user-1", "one@example.com", CancellationToken.None);
            Assert.Equal(first.Id, persisted.Id);
            Assert.Equal(first.DshSessionId, persisted.DshSessionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Store_MarksInterruptedJobsWithoutAutomaticallyReplayingThem()
    {
        var (directory, store) = await CreateStoreAsync();
        try
        {
            var job = await store.SubmitJobAsync(
                "user-1", "one@example.com", "potentially side-effecting task", CancellationToken.None);
            Assert.NotNull(await store.TryClaimNextJobAsync(CancellationToken.None));

            var reopenedStore = new SqliteMcpStateStore(
                new McpBridgeSettings
                {
                    ApiKey = new string('k', 32),
                    DatabasePath = Path.Combine(directory, "state.db"),
                    EmailNotificationsEnabled = true,
                },
                NullLogger<SqliteMcpStateStore>.Instance);
            await reopenedStore.InitializeAsync(CancellationToken.None);

            var interrupted = await reopenedStore.GetJobAsync(
                "user-1", job.Id, CancellationToken.None);
            Assert.Equal("interrupted", interrupted!.Status);
            Assert.Contains("not automatically replayed", interrupted.Error, StringComparison.Ordinal);
            Assert.Null(await reopenedStore.TryClaimNextJobAsync(CancellationToken.None));

            var mail = await reopenedStore.TryClaimNextEmailAsync(CancellationToken.None);
            Assert.NotNull(mail);
            Assert.Contains("interrupted", mail.Subject, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledEmail_SubmitPersistsResultAndDoesNotQueueMail()
    {
        var (directory, store) = await CreateStoreAsync(emailEnabled: false);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Id"] = "local-test-user";
        var callerContext = new McpCallerContext(new HttpContextAccessor
        {
            HttpContext = httpContext,
        });
        var email = new FakeEmailNotificationService(isEnabled: false);
        try
        {
            var response = await DshAsyncTools.SubmitAsync(
                "long task",
                store,
                callerContext,
                email,
                new McpBridgeSettings
                {
                    ApiKey = new string('k', 32),
                    DatabasePath = Path.Combine(directory, "state.db"),
                });

            Assert.Contains("\"emailNotifications\":\"disabled\"", response, StringComparison.Ordinal);
            Assert.Equal(0, email.ValidationCalls);

            var job = await store.TryClaimNextJobAsync(CancellationToken.None);
            Assert.NotNull(job);
            Assert.Empty(job.NotificationEmail);
            await store.CompleteJobAsync(
                job.Id,
                new HarnessRunResult(job.DshSessionId, "Local test result", "completed", [], []),
                CancellationToken.None);

            var saved = await store.GetJobAsync("local-test-user", job.Id, CancellationToken.None);
            Assert.Equal("Local test result", saved!.Result);
            Assert.Equal("disabled", saved.EmailStatus);
            Assert.Null(await store.TryClaimNextEmailAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JobWorker_CompletesInBackgroundAndQueuesAllowlistedEmail()
    {
        var (directory, store) = await CreateStoreAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var execution = new FakeDshExecutionService();
        var email = new FakeEmailNotificationService();
        DshJobWorker? jobWorker = null;
        EmailOutboxWorker? emailWorker = null;
        try
        {
            await store.SubmitJobAsync(
                "user-1", "one@example.com", "long-running prompt", cancellation.Token);

            jobWorker = new DshJobWorker(
                store,
                execution,
                new McpBridgeSettings { ApiKey = new string('k', 32) },
                NullLogger<DshJobWorker>.Instance);
            emailWorker = new EmailOutboxWorker(
                store, email, NullLogger<EmailOutboxWorker>.Instance);
            await jobWorker.StartAsync(cancellation.Token);
            await emailWorker.StartAsync(cancellation.Token);

            var sent = await email.Sent.Task.WaitAsync(cancellation.Token);
            var sessions = await store.ListSessionsAsync("user-1", cancellation.Token);
            Assert.Single(sessions);
            Assert.Contains("Async result", sent.Body, StringComparison.Ordinal);
        }
        finally
        {
            if (jobWorker is not null)
            {
                await jobWorker.StopAsync(CancellationToken.None);
            }

            if (emailWorker is not null)
            {
                await emailWorker.StopAsync(CancellationToken.None);
            }

            execution.CompleteNotifications();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JobWorker_PersistsDshErrorMessageWhenMessageLoggingIsEnabled()
    {
        var (directory, store) = await CreateStoreAsync(emailEnabled: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var execution = new FakeDshExecutionService
        {
            RunFailure = new HarnessRpcException(-32001, "Synthetic DSH runtime rejection."),
        };
        DshJobWorker? worker = null;
        try
        {
            var job = await store.SubmitJobAsync(
                "user-1", string.Empty, "trigger error", cancellation.Token);
            worker = new DshJobWorker(
                store,
                execution,
                new McpBridgeSettings
                {
                    ApiKey = new string('k', 32),
                    LogDshMessages = true,
                },
                NullLogger<DshJobWorker>.Instance);
            await worker.StartAsync(cancellation.Token);

            McpJob? failedJob = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                failedJob = await store.GetJobAsync("user-1", job.Id, cancellation.Token);
                if (failedJob?.Status == "failed")
                {
                    break;
                }

                await Task.Delay(20, cancellation.Token);
            }

            Assert.NotNull(failedJob);
            Assert.Equal("failed", failedJob.Status);
            Assert.Contains("Synthetic DSH runtime rejection.", failedJob.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None);
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JobWorker_MarksDshErrorFinishReasonAsFailed()
    {
        var (directory, store) = await CreateStoreAsync(emailEnabled: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var errorEvent = JsonSerializer.SerializeToElement(new
        {
            type = "turn/end",
            data = new
            {
                reason = new
                {
                    kind = "error",
                    message = "Synthetic DSH turn failure detail.",
                },
            },
        });
        var execution = new FakeDshExecutionService
        {
            RunResultOverride = new HarnessRunResult(
                "dsh-session",
                string.Empty,
                "error",
                [errorEvent],
                []),
        };
        DshJobWorker? worker = null;
        try
        {
            var job = await store.SubmitJobAsync(
                "user-1", string.Empty, "trigger DSH turn error", cancellation.Token);
            worker = new DshJobWorker(
                store,
                execution,
                new McpBridgeSettings
                {
                    ApiKey = new string('k', 32),
                    LogDshMessages = true,
                },
                NullLogger<DshJobWorker>.Instance);
            await worker.StartAsync(cancellation.Token);

            McpJob? failedJob = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                failedJob = await store.GetJobAsync("user-1", job.Id, cancellation.Token);
                if (failedJob?.Status == "failed")
                {
                    break;
                }

                await Task.Delay(20, cancellation.Token);
            }

            Assert.NotNull(failedJob);
            Assert.Equal("failed", failedJob.Status);
            Assert.Contains("Synthetic DSH turn failure detail.", failedJob.Error, StringComparison.Ordinal);
            Assert.Equal("disabled", failedJob.EmailStatus);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None);
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HostScheduleMonitor_EmailsObservedCompletionForKnownSession()
    {
        var (directory, store) = await CreateStoreAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var execution = new FakeDshExecutionService();
        DshHostScheduleMonitor? monitor = null;
        try
        {
            var session = await store.GetOrCreateActiveSessionAsync(
                "user-1", "one@example.com", cancellation.Token);
            monitor = new DshHostScheduleMonitor(
                execution, store, NullLogger<DshHostScheduleMonitor>.Instance);
            await monitor.StartAsync(cancellation.Token);

            execution.Publish(Notification("session.event", new
            {
                sessionId = session.DshSessionId,
                @event = new
                {
                    type = "agent/inbox/spliced",
                    data = new { inserted = new[] { new { id = "scheduled-message-1" } } },
                },
            }));
            execution.Publish(Notification("session.status", new
            {
                sessionId = session.DshSessionId,
                status = "running",
            }));
            execution.Publish(Notification("session.event", new
            {
                sessionId = session.DshSessionId,
                @event = new
                {
                    type = "assistant/message",
                    data = new
                    {
                        message = new
                        {
                            content = new[] { new { type = "text", text = "Schedule completed." } },
                        },
                    },
                },
            }));
            execution.Publish(Notification("session.event", new
            {
                sessionId = session.DshSessionId,
                @event = new { type = "turn/end", data = new { reason = new { kind = "completed" } } },
            }));
            execution.Publish(Notification("session.status", new
            {
                sessionId = session.DshSessionId,
                status = "idle",
            }));

            var mail = await WaitForEmailAsync(store, cancellation.Token);
            Assert.Equal("one@example.com", mail.Recipient);
            Assert.Contains("Schedule completed.", mail.Body, StringComparison.Ordinal);
        }
        finally
        {
            if (monitor is not null)
            {
                await monitor.StopAsync(CancellationToken.None);
            }

            execution.CompleteNotifications();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledEmail_HostRunsRemainPersistedAndQueryable()
    {
        var (directory, store) = await CreateStoreAsync(emailEnabled: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var execution = new FakeDshExecutionService();
        DshHostScheduleMonitor? monitor = null;
        try
        {
            var session = await store.GetOrCreateActiveSessionAsync(
                "user-1", string.Empty, cancellation.Token);
            monitor = new DshHostScheduleMonitor(
                execution, store, NullLogger<DshHostScheduleMonitor>.Instance);
            await monitor.StartAsync(cancellation.Token);

            execution.Publish(Notification("session.event", new
            {
                sessionId = session.DshSessionId,
                @event = new
                {
                    type = "agent/inbox/spliced",
                    data = new { inserted = new[] { new { id = "local-schedule-message" } } },
                },
            }));
            execution.Publish(Notification("session.event", new
            {
                sessionId = session.DshSessionId,
                @event = new
                {
                    type = "assistant/message",
                    data = new
                    {
                        message = new
                        {
                            content = new[] { new { type = "text", text = "Saved without sending email." } },
                        },
                    },
                },
            }));
            execution.Publish(Notification("session.status", new
            {
                sessionId = session.DshSessionId,
                status = "idle",
            }));

            IReadOnlyList<McpHostRun> runs = [];
            for (var attempt = 0; attempt < 100 && runs.Count == 0; attempt++)
            {
                runs = await store.GetRecentHostRunsAsync("user-1", 5, cancellation.Token);
                if (runs.Count == 0)
                {
                    await Task.Delay(20, cancellation.Token);
                }
            }

            var hostRun = Assert.Single(runs);
            Assert.Equal("Saved without sending email.", hostRun.Response);
            Assert.Equal("disabled", hostRun.EmailStatus);
            Assert.Null(await store.TryClaimNextEmailAsync(CancellationToken.None));
        }
        finally
        {
            if (monitor is not null)
            {
                await monitor.StopAsync(CancellationToken.None);
            }

            execution.CompleteNotifications();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(string Directory, SqliteMcpStateStore Store)> CreateStoreAsync(
        bool emailEnabled = true)
    {
        var directory = Directory.CreateTempSubdirectory("dsh-mcp-tests-").FullName;
        var settings = new McpBridgeSettings
        {
            ApiKey = new string('k', 32),
            DatabasePath = Path.Combine(directory, "state.db"),
            EmailNotificationsEnabled = emailEnabled,
        };
        var store = new SqliteMcpStateStore(settings, NullLogger<SqliteMcpStateStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        return (directory, store);
    }

    private static HarnessNotification Notification(string method, object parameters) =>
        new(method, JsonSerializer.SerializeToElement(parameters));

    private static async Task<McpOutboxMessage> WaitForEmailAsync(
        IMcpStateStore store,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await store.TryClaimNextEmailAsync(cancellationToken);
            if (message is not null)
            {
                return message;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class FakeDshExecutionService : IDshExecutionService
    {
        private readonly Channel<HarnessNotification> _notifications =
            Channel.CreateUnbounded<HarnessNotification>();

        public Exception? RunFailure { get; init; }
        public HarnessRunResult? RunResultOverride { get; init; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HarnessRunResult> RunAsync(
            string prompt,
            string sessionId,
            CancellationToken cancellationToken) =>
            RunResultOverride is { } result
                ? Task.FromResult(result)
                : RunFailure is { } failure
                ? Task.FromException<HarnessRunResult>(failure)
                : Task.FromResult(new HarnessRunResult(
                sessionId,
                "Async result",
                "completed",
                [],
                []));

        public IAsyncEnumerable<HarnessNotification> ReadNotificationsAsync(
            CancellationToken cancellationToken) =>
            _notifications.Reader.ReadAllAsync(cancellationToken);

        public bool IsSessionRunActive(string sessionId) => false;

        public void Publish(HarnessNotification notification) =>
            _notifications.Writer.TryWrite(notification);

        public void CompleteNotifications() => _notifications.Writer.TryComplete();
    }

    private sealed class FakeEmailNotificationService : IEmailNotificationService
    {
        public FakeEmailNotificationService(bool isEnabled = true)
        {
            IsEnabled = isEnabled;
        }

        public bool IsEnabled { get; }
        public int ValidationCalls { get; private set; }

        public TaskCompletionSource<McpOutboxMessage> Sent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ValidateRecipient(string recipient)
        {
            ValidationCalls++;
            if (!IsEnabled)
            {
                return;
            }

            if (!recipient.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Recipient is not allowlisted.");
            }
        }

        public Task<EmailNotificationResult> SendResultAsync(
            string recipient,
            HarnessRunResult result,
            CancellationToken cancellationToken) =>
            SendMessageAsync(recipient, "DSH task", result.FinalResponse, cancellationToken);

        public Task<EmailNotificationResult> SendMessageAsync(
            string recipient,
            string subject,
            string body,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                return Task.FromResult(new EmailNotificationResult("disabled", recipient, null));
            }

            ValidateRecipient(recipient);
            Sent.TrySetResult(new McpOutboxMessage("sent", null, recipient, subject, body, 1));
            return Task.FromResult(new EmailNotificationResult("sent", recipient, null));
        }
    }
}
