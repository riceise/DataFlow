using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Abstractions;

public interface IDataFlowDbContext
{
    DbSet<DataSource> DataSources { get; }
    DbSet<ImportJob> ImportJobs { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
