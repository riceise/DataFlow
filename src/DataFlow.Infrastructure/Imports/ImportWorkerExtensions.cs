using DataFlow.Application.Abstractions;
using DataFlow.Infrastructure.Messaging;
using DataFlow.Infrastructure.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace DataFlow.Infrastructure.Imports;

public static class ImportWorkerExtensions
{
    public static IServiceCollection AddDataFlowImportWorker(this IServiceCollection services)
    {
        services.AddScoped<ICsvRowParser, CsvRowParser>();
        services.AddScoped<IXlsxRowParser, XlsxRowParser>();
        services.AddScoped<ImportJobStore>();
        services.AddScoped<CsvImportProcessor>();
        services.AddScoped<OutboxStore>();
        services.AddScoped<ImportReconciliationStore>();
        services.AddHostedService<ImportReconciliation>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<ImportConsumer>();
        return services;
    }
}
