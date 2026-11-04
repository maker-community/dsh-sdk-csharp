using System.Threading.Channels;

namespace DeepSeek.Harness.Sdk;

/// <summary>A disposable, independently buffered stream of runtime notifications.</summary>
public sealed class NotificationSubscription : IAsyncDisposable
{
    private readonly ChannelReader<HarnessNotification> _reader;
    private readonly Action _unsubscribe;
    private int _disposed;

    internal NotificationSubscription(
        ChannelReader<HarnessNotification> reader,
        Action unsubscribe)
    {
        _reader = reader;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Reads the next notification, or throws when the runtime transport closes.</summary>
    public ValueTask<HarnessNotification> ReadAsync(CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(cancellationToken);

    /// <summary>Returns the next queued notification without waiting, if one is available.</summary>
    public bool TryRead(out HarnessNotification? notification) =>
        _reader.TryRead(out notification);

    /// <summary>Enumerates notifications until cancellation or transport closure.</summary>
    public IAsyncEnumerable<HarnessNotification> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _reader.ReadAllAsync(cancellationToken);

    /// <summary>Unsubscribes and releases the notification queue.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _unsubscribe();
        }

        return ValueTask.CompletedTask;
    }
}
