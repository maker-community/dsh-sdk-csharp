using DeepSeek.Harness.Sdk;
using System.Text.Json;

if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine(
        "Usage: ConsoleChat <dsh-executable-or-dsh.cmd> <dsh-home> [profile] [workspace]");
    return 2;
}

var dshExecutable = Path.GetFullPath(args[0]);
var dshHome = Path.GetFullPath(args[1]);
var profile = args.Length > 2 ? args[2] : "sdk";
var deleteWorkspaceOnExit = args.Length < 4;
var workspace = deleteWorkspaceOnExit
    ? Directory.CreateTempSubdirectory("dsh-sdk-csharp-chat-")
    : Directory.CreateDirectory(Path.GetFullPath(args[3]));
using var cancellation = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    var options = new DeepSeekHarnessOptions
    {
        DshExecutable = dshExecutable,
        DshHome = dshHome,
        Profile = profile,
        ProcessWorkingDirectory = workspace.FullName,
        WorkingDirectory = workspace.FullName,
    };

    await using var harness = new DeepSeekHarnessClient(options);
    await harness.StartAsync(cancellation.Token);

    var sessionId = $"dsh-sdk-csharp-chat-{Guid.NewGuid():N}";
    var waitingForInput = 0;
    await using var notifications = harness.SubscribeNotifications();
    var notificationMonitor = MonitorSessionNotificationsAsync(
        notifications, sessionId, () => Volatile.Read(ref waitingForInput) != 0, cancellation);

    Console.WriteLine($"Connected to DSH profile '{profile}'.");
    Console.WriteLine($"Session: {sessionId}");
    Console.WriteLine("Schedule tasks delivered to this Session appear here. Type /exit to quit.");

    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            Console.Write("You> ");
            Volatile.Write(ref waitingForInput, 1);
            string? prompt;
            try
            {
                prompt = await Console.In.ReadLineAsync(cancellation.Token);
            }
            finally
            {
                Volatile.Write(ref waitingForInput, 0);
            }

            if (prompt is null || string.Equals(prompt.Trim(), "/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            var result = await harness.RunAsync(prompt, sessionId, cancellationToken: cancellation.Token);
            var hasAssistantMessage = result.Events.Any(item =>
                item.TryGetProperty("type", out var eventType)
                && eventType.ValueKind == JsonValueKind.String
                && eventType.GetString() == "assistant/message");
            if (!hasAssistantMessage && !string.IsNullOrWhiteSpace(result.FinalResponse))
            {
                Console.WriteLine($"DSH> {result.FinalResponse}");
            }
        }
    }
    finally
    {
        cancellation.Cancel();
        await notifications.DisposeAsync();
        await notificationMonitor;
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Stopping the DSH session...");
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    if (deleteWorkspaceOnExit)
    {
        workspace.Delete(recursive: true);
    }
}

static async Task MonitorSessionNotificationsAsync(
    NotificationSubscription subscription,
    string sessionId,
    Func<bool> isWaitingForInput,
    CancellationTokenSource cancellation)
{
    try
    {
        await foreach (var notification in subscription.ReadAllAsync(cancellation.Token))
        {
            if (notification.Method != "session.event"
                || !notification.Parameters.TryGetProperty("sessionId", out var eventSession)
                || eventSession.ValueKind != JsonValueKind.String
                || eventSession.GetString() != sessionId
                || !notification.Parameters.TryGetProperty("event", out var sessionEvent)
                || !sessionEvent.TryGetProperty("type", out var eventType)
                || eventType.ValueKind != JsonValueKind.String
                || eventType.GetString() != "assistant/message")
            {
                continue;
            }

            var text = ReadAssistantText(sessionEvent);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var showPromptAgain = isWaitingForInput();
                if (showPromptAgain)
                {
                    Console.WriteLine();
                }

                Console.WriteLine($"DSH> {text}");
                if (showPromptAgain && isWaitingForInput())
                {
                    Console.Write("You> ");
                }
            }
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"[DSH notification listener failed] {exception}");
        cancellation.Cancel();
    }
}

static string ReadAssistantText(JsonElement sessionEvent)
{
    if (!sessionEvent.TryGetProperty("data", out var data)
        || !data.TryGetProperty("message", out var message)
        || !message.TryGetProperty("content", out var content)
        || content.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    return string.Concat(content.EnumerateArray()
        .Where(block => block.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "text")
        .Select(block => block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : string.Empty));
}
