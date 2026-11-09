// =============================================================================
// InMemoryJobQueue
// Channel<T> queue for the zero-setup mode (one process, no broker).
// Known issue: messages are lost on restart; JobWorker re-queues stuck jobs on startup.
// =============================================================================
using System.Threading.Channels;

namespace WebCrawler.Core.Queue;

public class InMemoryJobQueue : IJobQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>();

    public async Task EnqueueAsync(Guid jobId, CancellationToken cancel = default)
    {
        await channel.Writer.WriteAsync(jobId, cancel);
    }

    // DequeueAsync - waits until a job id arrives (used by JobWorker loops)
    public async Task<Guid> DequeueAsync(CancellationToken cancel)
    {
        return await channel.Reader.ReadAsync(cancel);
    }

    public bool CheckReady()
    {
        return true;
    }
}
