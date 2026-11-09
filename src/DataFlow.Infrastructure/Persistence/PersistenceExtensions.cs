using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public static IServiceCollection AddDataFlowPersistence(this IServiceCollection services)
    {
        services.AddScoped<JobControlHandler>();
        services.AddScoped<IRequestHandler<RetryJobCommand, JobAcceptedDto>>(p => p.GetRequiredService<JobControlHandler>());
        services.AddScoped<IRequestHandler<CancelJobCommand, JobAcceptedDto>>(p => p.GetRequiredService<JobControlHandler>());
        services.AddDbContext<DataFlowDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.DataFlow));
        return services.AddScoped<IDataFlowDbContext>(provider => provider.GetRequiredService<DataFlowDbContext>());
    }
}
