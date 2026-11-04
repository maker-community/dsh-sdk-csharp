using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace DeepSeek.Harness.Sdk;

/// <summary>
/// Drives the official DeepSeek Harness SDK profile over newline-delimited JSON-RPC on stdio.
/// The client owns the spawned DSH process until <see cref="DisposeAsync"/> is called.
/// </summary>
public sealed class DeepSeekHarnessClient : IAsyncDisposable
{
    private const int StderrTailSize = 40;
    private const string RuntimeName = "deepseek-harness-sdk-runtime";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeepSeekHarnessOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, PendingServerRequest> _serverRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingServerRequest> _userQuestions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionRunGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Channel<HarnessNotification>> _subscribers = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private int _closing;
    private int _closed;
    private int _suppressTransportFailure;
    private Exception? _transportFailure;
    private bool _initialized;

    /// <summary>Creates a client. The DSH process starts lazily on the first operation.</summary>
    public DeepSeekHarnessClient(DeepSeekHarnessOptions? options = null)
    {
        _options = options ?? new DeepSeekHarnessOptions();
        ValidateOptions(_options);
    }

    /// <summary>True after the runtime has completed its initialize handshake.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Starts DSH and completes the SDK initialize handshake. Subsequent calls are no-ops.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            if (_initialized)
            {
                return;
            }

            if (_process is null)
            {
                StartProcess();
            }

            try
            {
                var parameters = new Dictionary<string, object?>
                {
                    ["cwd"] = Path.GetFullPath(_options.WorkingDirectory),
                    ["provider"] = _options.Provider,
                    ["model"] = _options.Model,
                };
                AddIfPresent(parameters, "reasoningEffort", _options.ReasoningEffort);
                AddIfPresent(parameters, "maxTokens", _options.MaxTokens);
                if (_options.ApprovalRequests)
                {
                    parameters["approvalRequests"] = true;
                }

                if (_options.OnUserQuestion is not null)
                {
                    parameters["capabilities"] = new { userQuestions = true };
                }

                var result = await SendRequestAsync<InitializeResult>(
                    "initialize", parameters, cancellationToken).ConfigureAwait(false);
                if (result.ServerInfo is null
                    || !string.Equals(result.ServerInfo.Name, RuntimeName, StringComparison.Ordinal))
                {
                    throw new HarnessProtocolException(
                        $"Unexpected SDK runtime '{result.ServerInfo?.Name ?? "(missing)"}'. Expected '{RuntimeName}'.");
                }

                _initialized = true;
            }
            catch (Exception exception) when (
                exception is DeepSeekHarnessException or IOException or InvalidOperationException
                    or OperationCanceledException)
            {
                await StopProcessAfterStartupFailureAsync(exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Subscribes to every runtime notification received by this client.</summary>
    public NotificationSubscription SubscribeNotifications()
    {
        ThrowIfClosed();
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<HarnessNotification>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        if (!_subscribers.TryAdd(id, channel))
        {
            throw new InvalidOperationException("Could not register the notification subscription.");
        }

        if (_transportFailure is { } failure)
        {
            channel.Writer.TryComplete(failure);
        }

        return new NotificationSubscription(channel.Reader, () =>
        {
            if (_subscribers.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }
        });
    }

    /// <summary>
    /// Queues a prompt and returns its durable message receipt without waiting for execution.
    /// Use <see cref="RunAsync(string, string?, Action{HarnessNotification}?, CancellationToken)"/>
    /// when a completed run result is required.
    /// </summary>
    public async Task<string> PromptAsync(
        string sessionId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var result = await SendRequestAsync<SessionPromptResult>(
            "session/prompt",
            new
            {
                sessionId,
                contentBlocks = new[] { PromptContentBlock.FromText(prompt) },
            },
            cancellationToken).ConfigureAwait(false);
        return RequireNonEmpty(result.MessageId, "session/prompt returned no messageId.");
    }

    /// <summary>
    /// Runs text through the Harness query path and returns when its activity becomes idle.
    /// A timeout or caller cancellation does not cancel or roll back work already accepted by DSH.
    /// </summary>
    public Task<HarnessRunResult> RunAsync(
        string prompt,
        string? sessionId = null,
        Action<HarnessNotification>? onNotification = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return RunAsync(
            [PromptContentBlock.FromText(prompt)],
            sessionId,
            onNotification,
            cancellationToken);
    }

    /// <summary>
    /// Runs ordered text and inline-image blocks through the Harness query path.
    /// </summary>
    public async Task<HarnessRunResult> RunAsync(
        IReadOnlyList<PromptContentBlock> contentBlocks,
        string? sessionId = null,
        Action<HarnessNotification>? onNotification = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContentBlocks(contentBlocks);
        sessionId ??= $"session-{Guid.NewGuid():N}";
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var sessionGate = _sessionRunGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunStartedAsync(contentBlocks, sessionId, onNotification, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private async Task<HarnessRunResult> RunStartedAsync(
        IReadOnlyList<PromptContentBlock> contentBlocks,
        string sessionId,
        Action<HarnessNotification>? onNotification,
        CancellationToken cancellationToken)
    {
        await using var subscription = SubscribeNotifications();
        SessionQueryResult query;
        try
        {
            query = await SendRequestAsync<SessionQueryResult>(
                "session/query",
                new { sessionId, contentBlocks },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessRpcException exception) when (
            exception.Code == -32601
            || exception.RpcMessage.Contains(
                "unknown DeepSeek Harness SDK runtime method: session/query",
                StringComparison.OrdinalIgnoreCase))
        {
            return await RunPromptFallbackAsync(
                sessionId, contentBlocks, subscription, onNotification, cancellationToken).ConfigureAwait(false);
        }

        if (query.Kind == "command")
        {
            var commandId = RequireNonEmpty(
                query.Execution?.CommandId,
                "session/query returned a command without execution.commandId.");
            return await CollectCommandResultAsync(
                sessionId, commandId, subscription, onNotification, cancellationToken).ConfigureAwait(false);
        }

        if (query.Kind != "prompt")
        {
            throw new HarnessProtocolException($"session/query returned unknown result kind '{query.Kind}'.");
        }

        var messageId = RequireNonEmpty(query.MessageId, "session/query returned a prompt without messageId.");
        return await CollectPromptResultAsync(
            sessionId, messageId, subscription, onNotification, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HarnessRunResult> RunPromptFallbackAsync(
        string sessionId,
        IReadOnlyList<PromptContentBlock> contentBlocks,
        NotificationSubscription subscription,
        Action<HarnessNotification>? onNotification,
        CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync<SessionPromptResult>(
            "session/prompt",
            new { sessionId, contentBlocks },
            cancellationToken).ConfigureAwait(false);
        var messageId = RequireNonEmpty(result.MessageId, "session/prompt returned no messageId.");
        return await CollectPromptResultAsync(
            sessionId, messageId, subscription, onNotification, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HarnessRunResult> CollectPromptResultAsync(
        string sessionId,
        string messageId,
        NotificationSubscription subscription,
        Action<HarnessNotification>? onNotification,
        CancellationToken cancellationToken)
    {
        var activity = new RunActivity(sessionId, messageId, isCommand: false);
        while (true)
        {
            var notification = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            var state = activity.Record(notification, onNotification);
            if (state.PromptBecameIdle)
            {
                return activity.ToResult();
            }
        }
    }

    /// <summary>Waits for an SDK-owned session and managed descendants to become idle.</summary>
    public async Task WaitForSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await SendRequestAsync<JsonElement>(
            "session/wait",
            new { sessionId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes an existing persisted SDK session without enqueuing new input.</summary>
    public async Task ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await SendRequestAsync<JsonElement>(
            "session/resume",
            new { sessionId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the effective working directory for a session.</summary>
    public async Task<string> GetWorkingDirectoryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync<WorkingDirectoryResult>(
            "session/working-directory/get",
            new { sessionId },
            cancellationToken).ConfigureAwait(false);
        return RequireNonEmpty(result.Cwd, "Working-directory response contained no cwd.");
    }

    /// <summary>Changes and returns the effective working directory for a session.</summary>
    public async Task<string> SetWorkingDirectoryAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await SendRequestAsync<WorkingDirectoryResult>(
            "session/working-directory/set",
            new { sessionId, path },
            cancellationToken).ConfigureAwait(false);
        return RequireNonEmpty(result.Cwd, "Working-directory response contained no cwd.");
    }

    /// <summary>Shuts down DSH and reaps its child process. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        CancelPendingServerRequests();
        Exception? shutdownFailure = null;
        var process = _process;
        if (process is not null && !HasExited(process))
        {
            try
            {
                using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
                await SendRequestAsync<JsonElement>(
                    "shutdown", parameters: null, timeout.Token, allowClosing: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is DeepSeekHarnessException or IOException or OperationCanceledException
                    or InvalidOperationException)
            {
                shutdownFailure = exception;
            }
        }

        if (process is not null)
        {
            try
            {
                _stdin?.Close();
                if (!HasExited(process))
                {
                    using var grace = new CancellationTokenSource(_options.ProcessShutdownGrace);
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!HasExited(process))
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    shutdownFailure ??= exception;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                shutdownFailure ??= exception;
            }
        }

        Interlocked.Exchange(ref _closed, 1);
        var transportException = _transportFailure ?? new HarnessTransportException("Harness client was closed.");
        FailPendingRequests(transportException);
        CompleteSubscriptions(transportException);
        _process?.Dispose();
        _startGate.Dispose();
        _writeGate.Dispose();

        if (shutdownFailure is not null)
        {
            throw new DeepSeekHarnessException(
                "The Harness process was stopped, but its protocol shutdown did not complete cleanly.",
                shutdownFailure);
        }
    }

    private async Task<HarnessRunResult> CollectCommandResultAsync(
        string sessionId,
        string commandId,
        NotificationSubscription subscription,
        Action<HarnessNotification>? onNotification,
        CancellationToken cancellationToken)
    {
        var activity = new RunActivity(sessionId, commandId, isCommand: true);
        QueryCompleted? completed = null;
        while (completed is null)
        {
            var notification = await subscription.ReadAsync(cancellationToken).ConfigureAwait(false);
            activity.Record(notification, onNotification);
            if (notification.Method == "session.query.completed"
                && TryGetString(notification.Parameters, "sessionId", out var completedSession)
                && string.Equals(completedSession, sessionId, StringComparison.Ordinal)
                && TryGetString(notification.Parameters, "commandId", out var completedCommand)
                && string.Equals(completedCommand, commandId, StringComparison.Ordinal))
            {
                completed = ParseCompletion(notification.Parameters);
            }
        }

        while (subscription.TryRead(out var queued) && queued is not null)
        {
            activity.Record(queued, onNotification);
        }

        if (!activity.CommandCompleted)
        {
            throw new HarnessProtocolException(
                "session.query.completed arrived without the matching command/done lifecycle event.");
        }

        return activity.ToResult(completed);
    }

    private async Task<T> SendRequestAsync<T>(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        bool allowClosing = false)
    {
        ThrowIfClosed(allowClosing);
        if (_transportFailure is { } transportFailure)
        {
            throw new HarnessTransportException("The Harness stdio transport is closed.", transportFailure);
        }

        var process = _process ?? throw new InvalidOperationException(
            "The Harness process has not been started.");
        if (HasExited(process))
        {
            throw CreateTransportClosedException(process);
        }

        var id = Interlocked.Increment(ref _nextRequestId).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not register a JSON-RPC request.");
        }

        var request = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        try
        {
            await WriteFrameAsync(request, cancellationToken).ConfigureAwait(false);
            JsonElement response;
            if (method == "initialize")
            {
                using var timeout = new CancellationTokenSource(_options.InitializationTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new HarnessRequestTimeoutException(method, _options.InitializationTimeout);
                }
            }
            else if (_options.RequestTimeout is { } configuredTimeout)
            {
                using var timeout = new CancellationTokenSource(configuredTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new HarnessRequestTimeoutException(method, configuredTimeout);
                }
            }
            else
            {
                response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return response.Deserialize<T>(JsonOptions)
                    ?? throw new HarnessProtocolException($"The '{method}' response result was null.");
            }
            catch (JsonException exception)
            {
                throw new HarnessProtocolException(
                    $"The '{method}' response did not match the expected SDK protocol shape.", exception);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void StartProcess()
    {
        var executable = _options.DshExecutable;
        var arguments = new List<string>(_options.DshArguments);
        var isWindowsBatch = OperatingSystem.IsWindows()
            && Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || OperatingSystem.IsWindows()
                    && Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetFullPath(_options.ProcessWorkingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (isWindowsBatch)
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var commandArguments = new[] { executable }
                .Concat(arguments)
                .Append("--profile")
                .Append(_options.Profile)
                .Select(QuoteWindowsCommandArgument)
                .ToList();
            foreach (var patch in _options.Patches)
            {
                commandArguments.Add("--patch");
                commandArguments.Add(QuoteWindowsCommandArgument(Path.GetFullPath(patch)));
            }

            var command = string.Join(' ', commandArguments);
            startInfo.Arguments = $"/d /v:off /s /c \"{command}\"";
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(_options.Profile);
            foreach (var patch in _options.Patches)
            {
                startInfo.ArgumentList.Add("--patch");
                startInfo.ArgumentList.Add(Path.GetFullPath(patch));
            }
        }

        if (_options.DshHome is { } dshHome)
        {
            startInfo.Environment["DSH_HOME"] = Path.GetFullPath(dshHome);
        }

        foreach (var (key, value) in _options.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new HarnessTransportException("The DSH process could not be started.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            process.Dispose();
            throw new HarnessTransportException(
                $"Could not start DSH executable '{_options.DshExecutable}'.", exception);
        }

        _process = process;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n";
        process.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _closing) == 0
                && Volatile.Read(ref _suppressTransportFailure) == 0
                && ReferenceEquals(_process, process))
            {
                FailTransport(CreateTransportClosedException(process));
            }
        };
        _stdoutTask = ReadStdoutAsync(process);
        _stderrTask = ReadStderrAsync(process);
    }

    private static string QuoteWindowsCommandArgument(string argument)
    {
        if (argument.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DSH executable paths and arguments cannot contain double-quote characters on Windows.");
        }

        return $"\"{argument.Replace("%", "%%", StringComparison.Ordinal)}\"";
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                ProcessFrame(line);
            }

            if (Volatile.Read(ref _closing) == 0
                && Volatile.Read(ref _suppressTransportFailure) == 0
                && ReferenceEquals(_process, process))
            {
                FailTransport(CreateTransportClosedException(process));
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or JsonException or HarnessProtocolException)
        {
            FailTransport(new HarnessTransportException(
                "The Harness stdout stream stopped unexpectedly.", exception));
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > StderrTailSize)
                {
                    _stderrTail.TryDequeue(out _);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _closing) == 0)
            {
                FailTransport(new HarnessTransportException("The Harness stderr stream stopped unexpectedly.", exception));
            }
        }
    }

    private void ProcessFrame(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetString(root, "jsonrpc", out var version)
            || version != "2.0")
        {
            throw new HarnessProtocolException("The Harness runtime emitted an invalid JSON-RPC 2.0 frame.");
        }

        var hasId = root.TryGetProperty("id", out var idElement);
        var hasMethod = TryGetString(root, "method", out var method);
        if (hasId && hasMethod)
        {
            var parameters = root.TryGetProperty("params", out var requestParams)
                ? requestParams.Clone()
                : EmptyObject;
            _ = HandleServerRequestAsync(idElement.Clone(), method, parameters);
            return;
        }

        if (hasId)
        {
            var id = GetId(idElement);
            if (!_pending.TryGetValue(id, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.Object)
                {
                    pending.TrySetException(new HarnessProtocolException(
                        $"JSON-RPC response '{id}' contained an invalid error object."));
                    return;
                }

                var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                    ? value
                    : -32603;
                var message = TryGetString(error, "message", out var text) ? text : "Unknown JSON-RPC error";
                JsonElement? data = error.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null;
                pending.TrySetException(new HarnessRpcException(code, message, data));
            }
            else if (root.TryGetProperty("result", out var result))
            {
                pending.TrySetResult(result.Clone());
            }
            else
            {
                pending.TrySetException(new HarnessProtocolException(
                    $"JSON-RPC response '{id}' contained neither result nor error."));
            }

            return;
        }

        if (hasMethod)
        {
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : EmptyObject;
            if (method == "user-question.cancelled")
            {
                CancelUserQuestion(parameters);
            }

            PublishNotification(new HarnessNotification(method, parameters));
        }
    }

    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        if (method is not ("approval/request" or "user-question/ask"))
        {
            await WriteServerRequestErrorAsync(
                id,
                -32601,
                $"Unsupported server request '{method}'.").ConfigureAwait(false);
            return;
        }

        var idKey = GetId(id);
        var pending = new PendingServerRequest(idKey, id.Clone());
        if (!_serverRequests.TryAdd(idKey, pending))
        {
            await WriteServerRequestErrorAsync(
                id,
                -32600,
                "Duplicate server request id.").ConfigureAwait(false);
            return;
        }

        try
        {
            if (method == "approval/request")
            {
                await HandleApprovalRequestAsync(idKey, pending, parameters).ConfigureAwait(false);
            }
            else
            {
                await HandleUserQuestionRequestAsync(idKey, pending, parameters).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                $"The SDK handler for '{method}' failed.").ConfigureAwait(false);
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    private async Task HandleApprovalRequestAsync(
        string idKey,
        PendingServerRequest pending,
        JsonElement parameters)
    {
        if (!_options.ApprovalRequests || _options.OnApprovalRequest is null)
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32601,
                "No approval/request handler is configured.").ConfigureAwait(false);
            return;
        }

        if (!TryGetString(parameters, "sessionId", out var sessionId)
            || !TryGetString(parameters, "toolName", out var toolName))
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "approval/request contained invalid parameters.").ConfigureAwait(false);
            return;
        }

        var request = new ApprovalRequest(
            sessionId,
            toolName,
            TryGetString(parameters, "callId", out var callId) ? callId : null,
            TryGetString(parameters, "reason", out var reason) ? reason : null);
        var decision = await _options.OnApprovalRequest(request, pending.Cancellation.Token)
            .ConfigureAwait(false);
        if (decision is null
            || decision.Outcome is not ("allowed-once" or "rejected" or "cancelled"))
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "The approval handler returned an invalid decision.").ConfigureAwait(false);
            return;
        }

        await CompleteServerRequestAsync(idKey, pending, new { outcome = decision.Outcome })
            .ConfigureAwait(false);
    }

    private async Task HandleUserQuestionRequestAsync(
        string idKey,
        PendingServerRequest pending,
        JsonElement parameters)
    {
        if (_options.OnUserQuestion is null)
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32601,
                "No user-question/ask handler is configured.").ConfigureAwait(false);
            return;
        }

        if (!TryGetString(parameters, "requestId", out var requestId)
            || string.IsNullOrWhiteSpace(requestId)
            || !TryGetString(parameters, "sessionId", out var sessionId)
            || !parameters.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array
            || questions.GetArrayLength() == 0)
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "user-question/ask contained invalid parameters.").ConfigureAwait(false);
            return;
        }

        var questionItems = questions.EnumerateArray().Select(question => question.Clone()).ToArray();
        if (questionItems.Any(question =>
                question.ValueKind != JsonValueKind.Object
                || !TryGetString(question, "id", out var questionId)
                || string.IsNullOrWhiteSpace(questionId)))
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "user-question/ask contained a malformed question.").ConfigureAwait(false);
            return;
        }

        var questionIds = questionItems
            .Select(question =>
            {
                TryGetString(question, "id", out var questionId);
                return questionId;
            })
            .ToHashSet(StringComparer.Ordinal);
        if (questionIds.Count != questionItems.Length)
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "user-question/ask contained duplicate question ids.").ConfigureAwait(false);
            return;
        }

        pending.UserQuestionRequestId = requestId;
        if (!_userQuestions.TryAdd(requestId, pending))
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32600,
                "Duplicate user-question requestId.").ConfigureAwait(false);
            return;
        }

        var answer = await _options.OnUserQuestion(
                new SdkUserQuestionRequest(requestId, sessionId, questionItems),
                pending.Cancellation.Token)
            .ConfigureAwait(false);

        if (answer is null || !IsValidQuestionResponse(answer, questionIds))
        {
            await CompleteServerRequestWithErrorAsync(
                idKey,
                pending,
                -32603,
                "The user-question handler returned an invalid answer.").ConfigureAwait(false);
            return;
        }

        await CompleteServerRequestAsync(idKey, pending, answer).ConfigureAwait(false);
    }

    private static bool IsValidQuestionResponse(
        SdkUserQuestionResponse response,
        IReadOnlySet<string> expectedQuestionIds)
    {
        if (response.Cancelled == true)
        {
            return response.Answers is null;
        }

        if (response.Cancelled is not null
            || response.Answers is null
            || response.Answers.Count != expectedQuestionIds.Count)
        {
            return false;
        }

        var answeredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in response.Answers)
        {
            if (answer is null
                || string.IsNullOrWhiteSpace(answer.Id)
                || !expectedQuestionIds.Contains(answer.Id)
                || !answeredIds.Add(answer.Id)
                || answer.Selected is null
                || answer.Selected.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }
        }

        return answeredIds.SetEquals(expectedQuestionIds);
    }

    private void CancelUserQuestion(JsonElement parameters)
    {
        if (!TryGetString(parameters, "requestId", out var requestId)
            || !_userQuestions.TryRemove(requestId, out var pending))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref pending.State, PendingServerRequest.Cancelled, PendingServerRequest.Pending)
            == PendingServerRequest.Pending)
        {
            _serverRequests.TryRemove(pending.IdKey, out _);
            CancelRequestToken(pending);
        }
    }

    private async Task CompleteServerRequestAsync(
        string idKey,
        PendingServerRequest pending,
        object result)
    {
        if (Interlocked.CompareExchange(ref pending.State, PendingServerRequest.Completed, PendingServerRequest.Pending)
            != PendingServerRequest.Pending)
        {
            return;
        }

        RemovePendingServerRequest(idKey, pending);
        try
        {
            await WriteFrameAsync(new
            {
                jsonrpc = "2.0",
                id = pending.Id,
                result,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HarnessTransportException or IOException or ObjectDisposedException
                or InvalidOperationException)
        {
            FailTransport(new HarnessTransportException(
                "Could not return a server request result to the Harness runtime.", exception));
        }
    }

    private async Task CompleteServerRequestWithErrorAsync(
        string idKey,
        PendingServerRequest pending,
        int code,
        string message)
    {
        if (Interlocked.CompareExchange(ref pending.State, PendingServerRequest.Completed, PendingServerRequest.Pending)
            != PendingServerRequest.Pending)
        {
            return;
        }

        RemovePendingServerRequest(idKey, pending);
        try
        {
            await WriteServerRequestErrorAsync(pending.Id, code, message).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HarnessTransportException or IOException or ObjectDisposedException
                or InvalidOperationException)
        {
            FailTransport(new HarnessTransportException(
                "Could not return a server request error to the Harness runtime.", exception));
        }
    }

    private void RemovePendingServerRequest(string idKey, PendingServerRequest pending)
    {
        _serverRequests.TryRemove(idKey, out _);
        if (pending.UserQuestionRequestId is { } requestId)
        {
            _userQuestions.TryRemove(requestId, out _);
        }
    }

    private async Task WriteServerRequestErrorAsync(JsonElement id, int code, string message)
    {
        var error = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new { code, message },
        };
        await WriteFrameAsync(error, CancellationToken.None).ConfigureAwait(false);
    }

    private void CancelPendingServerRequests()
    {
        foreach (var (id, pending) in _serverRequests)
        {
            if (Interlocked.CompareExchange(
                    ref pending.State,
                    PendingServerRequest.Cancelled,
                    PendingServerRequest.Pending) != PendingServerRequest.Pending)
            {
                continue;
            }

            RemovePendingServerRequest(id, pending);
            CancelRequestToken(pending);
        }
    }

    private static void CancelRequestToken(PendingServerRequest pending)
    {
        try
        {
            pending.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WriteFrameAsync(object frame, CancellationToken cancellationToken)
    {
        var stdin = _stdin ?? throw new HarnessTransportException("The DSH stdin stream is not available.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(JsonSerializer.Serialize(frame, JsonOptions).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var transportException = new HarnessTransportException(
                "Could not write a JSON-RPC frame to the Harness process.", exception);
            FailTransport(transportException);
            throw transportException;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void PublishNotification(HarnessNotification notification)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(notification);
        }
    }

    private void FailTransport(Exception exception)
    {
        Interlocked.CompareExchange(ref _transportFailure, exception, null);
        var failure = _transportFailure!;
        CancelPendingServerRequests();
        FailPendingRequests(failure);
        CompleteSubscriptions(failure);
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }
    }

    private void CompleteSubscriptions(Exception exception)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete(exception);
        }
    }

    private async Task StopProcessAfterStartupFailureAsync(Exception initializationFailure)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        CancelPendingServerRequests();
        Interlocked.Exchange(ref _suppressTransportFailure, 1);
        Exception? cleanupFailure = null;
        try
        {
            if (!HasExited(process))
            {
                _stdin?.Close();
                using var grace = new CancellationTokenSource(_options.ProcessShutdownGrace);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            cleanupFailure = exception;
            if (!HasExited(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (Exception killException) when (
                    killException is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new AggregateException(
                        "Harness initialization failed and the child process could not be reaped.",
                        initializationFailure,
                        cleanupFailure,
                        killException);
                }
            }
        }

        if (!HasExited(process))
        {
            throw new AggregateException(
                "Harness initialization failed and process cleanup did not confirm child exit.",
                initializationFailure,
                cleanupFailure ?? new InvalidOperationException("The child process is still running."));
        }

        await Task.WhenAll(
            _stdoutTask ?? Task.CompletedTask,
            _stderrTask ?? Task.CompletedTask).ConfigureAwait(false);
        process.Dispose();
        _process = null;
        _stdin = null;
        _stdoutTask = null;
        _stderrTask = null;
        _initialized = false;
        Interlocked.Exchange(ref _transportFailure, null);
        Interlocked.Exchange(ref _suppressTransportFailure, 0);
    }

    private HarnessTransportException CreateTransportClosedException(Process process)
    {
        var exitCode = TryGetExitCode(process);
        var stderr = string.Join(Environment.NewLine, _stderrTail);
        var message = $"The DSH runtime exited (code {exitCode?.ToString() ?? "unknown"}).";
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            message += $"{Environment.NewLine}Recent stderr:{Environment.NewLine}{stderr}";
        }

        return new HarnessTransportException(message);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void ThrowIfClosed(bool allowClosing = false)
    {
        if (Volatile.Read(ref _closed) != 0
            || (!allowClosing && Volatile.Read(ref _closing) != 0))
        {
            throw new ObjectDisposedException(nameof(DeepSeekHarnessClient));
        }
    }

    private static void ValidateOptions(DeepSeekHarnessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DshExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProcessWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ValidatePositive(options.InitializationTimeout, nameof(options.InitializationTimeout));
        ValidatePositive(options.ShutdownTimeout, nameof(options.ShutdownTimeout));
        ValidatePositive(options.ProcessShutdownGrace, nameof(options.ProcessShutdownGrace));
        if (options.RequestTimeout is { } timeout)
        {
            ValidatePositive(timeout, nameof(options.RequestTimeout));
        }

        if (options.MaxTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxTokens), "MaxTokens must be positive.");
        }
    }

    private static void ValidateContentBlocks(IReadOnlyList<PromptContentBlock> contentBlocks)
    {
        ArgumentNullException.ThrowIfNull(contentBlocks);
        if (contentBlocks.Count == 0)
        {
            throw new ArgumentException("At least one prompt content block is required.", nameof(contentBlocks));
        }

        var hasContent = false;
        foreach (var block in contentBlocks)
        {
            ArgumentNullException.ThrowIfNull(block);
            if (block.Type == "text")
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }

                hasContent = true;
            }
            else if (block.Type == "image")
            {
                if (string.IsNullOrWhiteSpace(block.Data) || string.IsNullOrWhiteSpace(block.MimeType))
                {
                    throw new ArgumentException("Image blocks require base64 data and a MIME type.", nameof(contentBlocks));
                }

                if (block.MimeType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
                {
                    throw new ArgumentException($"Unsupported image MIME type '{block.MimeType}'.", nameof(contentBlocks));
                }

                try
                {
                    _ = Convert.FromBase64String(block.Data);
                }
                catch (FormatException exception)
                {
                    throw new ArgumentException("Image data must be valid base64.", nameof(contentBlocks), exception);
                }

                hasContent = true;
            }
            else
            {
                throw new ArgumentException($"Unsupported prompt content block type '{block.Type}'.", nameof(contentBlocks));
            }
        }

        if (!hasContent)
        {
            throw new ArgumentException("Prompt content cannot contain only blank text blocks.", nameof(contentBlocks));
        }
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout values must be positive.");
        }
    }

    private static void AddIfPresent(IDictionary<string, object?> values, string key, object? value)
    {
        if (value is not null)
        {
            values[key] = value;
        }
    }

    private static string RequireNonEmpty(string? value, string message) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new HarnessProtocolException(message);

    private static string GetId(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            _ => throw new HarnessProtocolException("JSON-RPC response id must be a string or number."),
        };

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static QueryCompleted ParseCompletion(JsonElement parameters)
    {
        if (!TryGetString(parameters, "finalResponse", out var response)
            || !TryGetString(parameters, "finishReason", out var reason)
            || string.IsNullOrWhiteSpace(reason))
        {
            throw new HarnessProtocolException("session.query.completed contained an invalid completion result.");
        }

        return new QueryCompleted(response, reason);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed class InitializeResult
    {
        public ServerInfo? ServerInfo { get; init; } = new();
    }

    private sealed class ServerInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
    }

    private sealed class SessionPromptResult
    {
        public string MessageId { get; init; } = string.Empty;
    }

    private sealed class SessionQueryResult
    {
        public string Kind { get; init; } = string.Empty;
        public string? MessageId { get; init; }
        public CommandExecution? Execution { get; init; }
    }

    private sealed class CommandExecution
    {
        public string? CommandId { get; init; }
    }

    private sealed class WorkingDirectoryResult
    {
        public string Cwd { get; init; } = string.Empty;
    }

    private sealed record QueryCompleted(string FinalResponse, string FinishReason);

    private sealed class PendingServerRequest
    {
        public const int Pending = 0;
        public const int Completed = 1;
        public const int Cancelled = 2;

        public PendingServerRequest(string idKey, JsonElement id)
        {
            IdKey = idKey;
            Id = id;
        }

        public string IdKey { get; }
        public JsonElement Id { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public string? UserQuestionRequestId { get; set; }
        public int State;
    }

    private sealed class RunActivity
    {
        private readonly string _sessionId;
        private readonly string _receiptId;
        private readonly bool _isCommand;
        private readonly HashSet<string> _sessionTree;
        private readonly List<JsonElement> _events = [];
        private readonly List<HarnessNotification> _notifications = [];
        private bool _receiptSeen;

        public RunActivity(string sessionId, string receiptId, bool isCommand)
        {
            _sessionId = sessionId;
            _receiptId = receiptId;
            _isCommand = isCommand;
            _sessionTree = [sessionId];
        }

        public bool CommandCompleted { get; private set; }

        public (bool PromptBecameIdle, bool CommandCompleted) Record(
            HarnessNotification notification,
            Action<HarnessNotification>? observer)
        {
            if (notification.Method == "subagent.started"
                && TryGetString(notification.Parameters, "parentSessionId", out var parent)
                && _sessionTree.Contains(parent)
                && TryGetString(notification.Parameters, "childSessionId", out var child))
            {
                _sessionTree.Add(child);
            }

            var relevant = notification.Method switch
            {
                "session.event" or "session.status" =>
                    TryGetString(notification.Parameters, "sessionId", out var eventSession)
                    && _sessionTree.Contains(eventSession),
                "subagent.started" or "subagent.finished" =>
                    IsInSessionTree(notification.Parameters),
                "session.query.completed" =>
                    TryGetString(notification.Parameters, "sessionId", out var querySession)
                    && string.Equals(querySession, _sessionId, StringComparison.Ordinal),
                "user-question.cancelled" =>
                    TryGetString(notification.Parameters, "sessionId", out var questionSession)
                    && _sessionTree.Contains(questionSession),
                _ => false,
            };

            if (!relevant)
            {
                return default;
            }

            var eventElement = default(JsonElement);
            var rootEvent = notification.Method == "session.event"
                && TryGetString(notification.Parameters, "sessionId", out var owner)
                && string.Equals(owner, _sessionId, StringComparison.Ordinal)
                && notification.Parameters.TryGetProperty("event", out eventElement);

            if (rootEvent)
            {
                ValidateSessionEvent(eventElement);
            }

            if (notification.Method == "session.status"
                && TryGetString(notification.Parameters, "sessionId", out var statusOwner)
                && _sessionTree.Contains(statusOwner)
                && (!TryGetString(notification.Parameters, "status", out var statusValue)
                    || statusValue is not ("running" or "idle")))
            {
                throw new HarnessProtocolException("session.status contained an unknown status.");
            }

            if (!_receiptSeen)
            {
                if (!rootEvent || !MatchesReceipt(eventElement))
                {
                    return default;
                }

                _receiptSeen = true;
            }

            _notifications.Add(notification);
            observer?.Invoke(notification);

            if (rootEvent)
            {
                _events.Add(eventElement.Clone());
                if (_isCommand
                    && TryGetString(eventElement, "type", out var type)
                    && type == "command/done"
                    && eventElement.TryGetProperty("data", out var data)
                    && TryGetString(data, "commandId", out var doneId)
                    && string.Equals(doneId, _receiptId, StringComparison.Ordinal))
                {
                    CommandCompleted = true;
                }
            }

            var isIdle = !_isCommand
                && notification.Method == "session.status"
                && TryGetString(notification.Parameters, "sessionId", out var statusSession)
                && string.Equals(statusSession, _sessionId, StringComparison.Ordinal)
                && TryGetString(notification.Parameters, "status", out var status)
                && status == "idle";

            return (isIdle, CommandCompleted);
        }

        public HarnessRunResult ToResult(QueryCompleted? completion = null)
        {
            var finalResponse = completion?.FinalResponse ?? GetFinalResponse(_events);
            var finishReason = completion?.FinishReason ?? GetFinishReason(_events);
            return new HarnessRunResult(_sessionId, finalResponse, finishReason, _events, _notifications);
        }

        private bool MatchesReceipt(JsonElement eventElement)
        {
            if (!TryGetString(eventElement, "type", out var type)
                || !eventElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (_isCommand)
            {
                return type == "command/run"
                    && TryGetString(data, "commandId", out var commandId)
                    && string.Equals(commandId, _receiptId, StringComparison.Ordinal);
            }

            if (type != "agent/inbox/spliced"
                || !data.TryGetProperty("inserted", out var inserted)
                || inserted.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return inserted.EnumerateArray().Any(item =>
                TryGetString(item, "id", out var messageId)
                && string.Equals(messageId, _receiptId, StringComparison.Ordinal));
        }

        private static void ValidateSessionEvent(JsonElement eventElement)
        {
            if (eventElement.ValueKind != JsonValueKind.Object
                || !TryGetString(eventElement, "type", out var eventType))
            {
                throw new HarnessProtocolException("session.event contained no valid event envelope.");
            }

            if (eventType == "assistant/message")
            {
                if (!eventElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("message", out var message)
                    || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array
                    || !content.EnumerateArray().All(block =>
                        block.ValueKind == JsonValueKind.Object
                        && TryGetString(block, "type", out _)))
                {
                    throw new HarnessProtocolException(
                        "assistant/message contained malformed content blocks.");
                }
            }
            else if (eventType == "turn/end"
                && (!eventElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("reason", out var reason)
                    || !TryGetString(reason, "kind", out _)))
            {
                throw new HarnessProtocolException("turn/end contained no valid reason.");
            }
        }

        private bool IsInSessionTree(JsonElement parameters)
        {
            var parentKnown = TryGetString(parameters, "parentSessionId", out var parent)
                && _sessionTree.Contains(parent);
            var childKnown = TryGetString(parameters, "childSessionId", out var child)
                && _sessionTree.Contains(child);
            return parentKnown || childKnown;
        }

        private static string GetFinalResponse(IReadOnlyList<JsonElement> events)
        {
            for (var index = events.Count - 1; index >= 0; index--)
            {
                var item = events[index];
                if (!TryGetString(item, "type", out var type) || type != "assistant/message"
                    || !item.TryGetProperty("data", out var data)
                    || !data.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                return string.Concat(content.EnumerateArray()
                    .Where(block => TryGetString(block, "type", out var blockType) && blockType == "text")
                    .Select(block => TryGetString(block, "text", out var text) ? text : string.Empty));
            }

            return string.Empty;
        }

        private static string? GetFinishReason(IReadOnlyList<JsonElement> events)
        {
            for (var index = events.Count - 1; index >= 0; index--)
            {
                var item = events[index];
                if (TryGetString(item, "type", out var type)
                    && type == "turn/end"
                    && item.TryGetProperty("data", out var data)
                    && data.TryGetProperty("reason", out var reason)
                    && TryGetString(reason, "kind", out var kind))
                {
                    return kind;
                }
            }

            return null;
        }
    }
}
