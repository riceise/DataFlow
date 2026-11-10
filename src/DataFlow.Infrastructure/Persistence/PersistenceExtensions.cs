using DataFlow.Application.Abstractions;
using DataFlow.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public static IServiceCollection AddDataFlowPersistence(this IServiceCollection services)
    {
        services.AddDbContext<DataFlowDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.DataFlow));
        return services.AddScoped<IDataFlowDbContext>(provider => provider.GetRequiredService<DataFlowDbContext>());
    }
}
