using System.Threading.Channels;

namespace ParserService.Core;

public class TaskQueue
{
    // SingleReader=false — несколько worker'ов читают параллельно (см. CollectionTaskBackgroundService).
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = false });

    public async ValueTask EnqueueAsync(Guid taskId, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(taskId, ct);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
