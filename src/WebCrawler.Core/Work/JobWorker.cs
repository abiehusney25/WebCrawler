// =============================================================================
// JobWorker
// BackgroundService. On startup re-queues jobs stuck in Pending/Running (in-memory
// queues lose messages on restart; also covers a crash). Then, for the in-memory
// queue, runs WorkerCount loops: DequeueAsync -> JobRunner.RunJob.
// With RabbitMQ the MassTransit consumer (JobConsumer) does the dequeuing instead.
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using WebCrawler.Core.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core.Work;

public class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IJobQueue queue;
    private readonly Settings settings;
    private readonly ILogger<JobWorker> logger;

    public JobWorker(IServiceScopeFactory scopeFactory, IJobQueue queue, Settings settings, ILogger<JobWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.queue = queue;
        this.settings = settings;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stopToken)
    {
        await Task.Yield();                                                   // let the host finish starting
        await CheckStuckJobs(stopToken);

        if (queue is InMemoryJobQueue memoryQueue)
        {
            int workerCount = Math.Max(1, settings.GetInt("WorkerCount", 2));
            logger.LogInformation("Worker started: {Count} loops on the in-memory queue", workerCount);
            await Task.WhenAll(Enumerable.Range(1, workerCount).Select(_ => RunLoop(memoryQueue, stopToken)));
        }
    }

    // CheckStuckJobs - Running -> Pending, then put every Pending job back on the queue
    private async Task CheckStuckJobs(CancellationToken stopToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Jobs
            .Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, JobStatus.Pending), stopToken);
        List<Guid> stuckIds = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .ToListAsync(stopToken);

        foreach (Guid jobId in stuckIds)
            await queue.EnqueueAsync(jobId, stopToken);
        if (stuckIds.Count > 0)
            logger.LogInformation("Re-queued {Count} stuck jobs", stuckIds.Count);
    }

    // RunLoop - take the next job id and run it; one bad job never stops the loop
    private async Task RunLoop(InMemoryJobQueue memoryQueue, CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            Guid jobId;
            try
            {
                jobId = await memoryQueue.DequeueAsync(stopToken);
            }
            catch (OperationCanceledException)
            {
                return;                                                       // shutting down
            }

            using IServiceScope scope = scopeFactory.CreateScope();          // fresh DbContext per job
            JobRunner runner = scope.ServiceProvider.GetRequiredService<JobRunner>();
            await runner.RunJob(jobId, stopToken);
        }
    }
}
