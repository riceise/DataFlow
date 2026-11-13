using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Observability;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Serilog;
using Serilog.Formatting.Json;

namespace DataFlow.Worker;

public static class WorkerProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) { builder.WebHost.UseUrls("http://127.0.0.1:5031"); }

        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "DataFlow.Worker")
                .WriteTo.Console(new JsonFormatter(renderMessage: true));
        });

        builder.Services.AddDataFlowConfiguration(builder.Configuration);
        builder.Services.AddDataFlowPersistence();
        builder.Services.AddDataFlowStorage();
        builder.Services.AddDataFlowImportWorker();
        builder.Services.AddDataFlowObservability(builder.Configuration, "DataFlow.Worker", worker: true);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        var host = builder.Build();
        host.MapDataFlowHealth();
        await host.RunAsync();
    }
}
