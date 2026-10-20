using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Abstractions;

public interface IDataFlowDbContext
{
    DbSet<DataSource> DataSources { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
