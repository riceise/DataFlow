using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataFlow.Infrastructure.Persistence;

public sealed class DataFlowDbContextFactory : IDesignTimeDbContextFactory<DataFlowDbContext>
{
    public DataFlowDbContext CreateDbContext(string[] args)
    {
        // Scaffolding needs no database. Applying migrations requires an explicit connection.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DataFlow")
            ?? "Host=127.0.0.1;Database=dataflow;Username=dataflow";
        return new DataFlowDbContext(new DbContextOptionsBuilder<DataFlowDbContext>()
            .UseNpgsql(connectionString).Options);
    }
}
