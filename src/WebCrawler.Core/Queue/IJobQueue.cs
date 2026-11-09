// =============================================================================
// IJobQueue
// The API puts job ids on the queue; a worker picks them up. Messages carry the
// id only, never a big payload. Two versions: InMemoryJobQueue / RabbitJobQueue.
// =============================================================================
namespace WebCrawler.Core.Queue;

public interface IJobQueue
{
    Task EnqueueAsync(Guid jobId, CancellationToken cancel = default);

    // CheckReady - true when messages can be sent (used by /api/health)
    bool CheckReady();
}

// JobMessage - what travels over RabbitMQ (the in-memory queue just uses the Guid)
public class JobMessage
{
    public Guid JobId { get; set; }
}

// QueueUnavailableException - the broker is down or too slow; the API answers 503
public class QueueUnavailableException : Exception
{
    public QueueUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
