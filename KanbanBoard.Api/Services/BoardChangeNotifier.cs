using System.Collections.Concurrent;
using System.Threading.Channels;

namespace KanbanBoard.Api.Services;

public sealed class BoardChangeNotifier
{
    private readonly ConcurrentDictionary<Guid, Channel<BoardChangeEvent>> _subscriptions = new();
    private long _sequence;

    public BoardChangeSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<BoardChangeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscriptions[id] = channel;
        return new BoardChangeSubscription(channel.Reader, () => _subscriptions.TryRemove(id, out _));
    }

    public void Publish()
    {
        var change = new BoardChangeEvent(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow);

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Writer.TryWrite(change);
        }
    }
}

public sealed record BoardChangeEvent(long Sequence, DateTimeOffset ChangedAtUtc);

public sealed class BoardChangeSubscription(ChannelReader<BoardChangeEvent> reader, Action dispose) : IDisposable
{
    private readonly Action _dispose = dispose;
    private int _disposed;

    public ChannelReader<BoardChangeEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}
