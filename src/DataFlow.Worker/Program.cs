using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Worker;
using Serilog;
using Serilog.Formatting.Json;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
