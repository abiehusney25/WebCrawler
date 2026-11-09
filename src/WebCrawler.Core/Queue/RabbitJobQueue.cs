// =============================================================================
// RabbitJobQueue + JobConsumer
// QueueMode=RabbitMq: MassTransit 8.x sends the job id to one named queue; the
// consumer runs JobRunner and the message is acked only after the job is done.
// Retries 1 s / 5 s / 15 s, then MassTransit moves it to the <queue>_error queue
// (dead letter) and publishes a Fault; JobFaultConsumer marks the job Failed so
// nothing stays Running forever. Crawl errors never throw (they fail the page or
// the job), so retries are for infrastructure trouble like the database being down.
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using WebCrawler.Core.Work;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core.Queue;

public class RabbitJobQueue : IJobQueue
{
    private readonly IBus bus;
    private readonly IBusControl busControl;
    private readonly Uri queueUri;
    private readonly TimeSpan sendTimeout;

    public RabbitJobQueue(IBus bus, IBusControl busControl, Settings settings)
    {
        this.bus = bus;
        this.busControl = busControl;
        queueUri = new Uri("queue:" + GetQueueName(settings));             // Send declares the queue, so no message is lost before a worker binds
        sendTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.GetInt("QueueSendTimeoutSeconds", 5)));
    }

    // EnqueueAsync - give up after QueueSendTimeoutSeconds instead of hanging while the broker is down
    public async Task EnqueueAsync(Guid jobId, CancellationToken cancel = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(sendTimeout);
        try
        {
            ISendEndpoint endpoint = await bus.GetSendEndpoint(queueUri);
            await endpoint.Send(new JobMessage { JobId = jobId }, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new QueueUnavailableException("RabbitMQ did not accept the message in time", ex);
        }
        catch (MassTransitException ex)
        {
            throw new QueueUnavailableException("RabbitMQ error: " + ex.Message, ex);
        }
    }

    public bool CheckReady()
    {
        return busControl.CheckHealth().Status == BusHealthStatus.Healthy;
    }

    public static string GetQueueName(Settings settings)
    {
        return settings.Get("RabbitQueue", "webcrawler-jobs");
    }
}

// JobConsumer - one message = one job; MassTransit gives it its own DI scope (fresh DbContext)
public class JobConsumer : IConsumer<JobMessage>
{
    private readonly JobRunner runner;

    public JobConsumer(JobRunner runner)
    {
        this.runner = runner;
    }

    // Consume - a redelivered message means the worker that had it died: take the job over
    public async Task Consume(ConsumeContext<JobMessage> context)
    {
        await runner.RunJob(context.Message.JobId, context.CancellationToken, takeOver: context.ReceiveContext.Redelivered);
    }
}

// JobFaultConsumer - the dead-letter side: after the last retry, fail the job with the reason
public class JobFaultConsumer : IConsumer<Fault<JobMessage>>
{
    private readonly AppDbContext db;
    private readonly ILogger<JobFaultConsumer> logger;

    public JobFaultConsumer(AppDbContext db, ILogger<JobFaultConsumer> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    public async Task Consume(ConsumeContext<Fault<JobMessage>> context)
    {
        Guid jobId = context.Message.Message.JobId;
        string error = context.Message.Exceptions.FirstOrDefault()?.Message ?? "unknown error";
        string reason = "Message failed after retries (moved to the error queue): " + error;
        DateTime now = DateTime.UtcNow;

        await db.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.FailureReason, reason.Length > 1000 ? reason.Substring(0, 1000) : reason));
        logger.LogWarning("FAIL job {JobId} dead-lettered: {Reason}", jobId, error);
    }
}
