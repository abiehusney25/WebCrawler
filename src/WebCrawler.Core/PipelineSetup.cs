// =============================================================================
// PipelineSetup
// Registers everything the API and the Worker share: settings, database, queue,
// HTTP client (retries + timeouts), fetcher, runner and (optionally) the worker.
// Each mode is ONE switch in Config/settings.csv.
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Queue;
using WebCrawler.Core.Work;
using WebCrawler.Core.Work.Pages;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core;

public static class PipelineSetup
{
    // SetUpPipeline - runWorker is true for WorkerMode=InProcess (API) and in the Worker host
    public static void SetUpPipeline(IServiceCollection services, Settings settings, bool runWorker)
    {
        services.AddSingleton(settings);
        SetUpLogging(services, settings);
        DbSetup.AddDatabase(services, settings);
        SetUpQueue(services, settings, runWorker);
        SetUpHttp(services, settings);

        services.AddScoped<JobRunner>();
        SetUpWork(services);
        if (runWorker)
            services.AddHostedService<JobWorker>();
    }

    // SetUpLogging - console logs with scopes (JobId on every line of a job); LogFormat=Json for Docker
    private static void SetUpLogging(IServiceCollection services, Settings settings)
    {
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            if (settings.IsMode("LogFormat", "Json", "Simple"))
                logging.AddJsonConsole(options => options.IncludeScopes = true);
            else
                logging.AddSimpleConsole(options => { options.IncludeScopes = true; options.SingleLine = true; });
        });
    }

    // SetUpWork - one IJobWork per InputType; adding a kind of work = one line here
    private static void SetUpWork(IServiceCollection services)
    {
        services.AddSingleton<IPageFetcher, PageFetcher>();
        services.AddScoped<IJobWork, PageWork>();
    }

    // SetUpQueue - InMemory (default) or RabbitMq; the consumer is only added where work runs
    private static void SetUpQueue(IServiceCollection services, Settings settings, bool runWorker)
    {
        if (!settings.IsMode("QueueMode", "RabbitMq", "InMemory"))
        {
            services.AddSingleton<IJobQueue, InMemoryJobQueue>();
            return;
        }

        services.AddSingleton<IJobQueue, RabbitJobQueue>();
        services.AddMassTransit(bus =>
        {
            if (runWorker)
            {
                bus.AddConsumer<JobConsumer>();
                bus.AddConsumer<JobFaultConsumer>();
            }

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(settings.Get("RabbitHost", "localhost"), "/", host =>
                {
                    host.Username(settings.Get("RabbitUser", "guest"));
                    host.Password(settings.Get("RabbitPassword", "guest"));
                });

                if (!runWorker)
                    return;                                                   // API only sends
                rabbit.ReceiveEndpoint(RabbitJobQueue.GetQueueName(settings), endpoint =>
                {
                    endpoint.ConcurrentMessageLimit = Math.Max(1, settings.GetInt("WorkerCount", 2));
                    endpoint.UseMessageRetry(retry => retry.Intervals(1000, 5000, 15000));
                    endpoint.ConfigureConsumer<JobConsumer>(context);
                });
                rabbit.ReceiveEndpoint(RabbitJobQueue.GetQueueName(settings) + "-faults", endpoint =>
                    endpoint.ConfigureConsumer<JobFaultConsumer>(context));
            });
        });
    }

    // SetUpHttp - one named client: user agent, retries with backoff (honors Retry-After),
    // per-try and per-page timeouts, a circuit breaker per host
    private static void SetUpHttp(IServiceCollection services, Settings settings)
    {
        int requestTimeout = Math.Max(1, settings.GetInt("RequestTimeoutSeconds", 10));
        int pageTimeout = Math.Max(requestTimeout, settings.GetInt("PageTimeoutSeconds", 45));

        services.AddHttpClient(PageFetcher.ClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;                   // the resilience handler owns timeouts
                client.DefaultRequestHeaders.UserAgent.ParseAdd(settings.Get("UserAgent", "WebCrawler/1.0"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => PageFetcher.SetUpHandler(settings))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = Math.Max(1, settings.GetInt("MaxRetries", 3));
                options.Retry.Delay = TimeSpan.FromSeconds(1);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(requestTimeout);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(pageTimeout);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(requestTimeout * 2 + 1);   // must be >= 2x attempt timeout
            })
            .SelectPipelineByAuthority();                                     // one dead site doesn't block the others
    }
}
