using DataFlow.Application.Abstractions;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Parsing;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
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
builder.Services.AddDataFlowStorage();
builder.Services.AddScoped<ICsvRowParser, CsvRowParser>();
builder.Services.AddScoped<ImportJobStore>();
builder.Services.AddScoped<CsvImportProcessor>();
builder.Services.AddScoped<OutboxStore>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<ImportConsumer>();

var host = builder.Build();
await host.RunAsync();
