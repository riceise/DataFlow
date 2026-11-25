using DataFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataFlow.Application.Abstractions;

public interface IDataFlowDbContext
{
    DbSet<DataSource> DataSources { get; }
    DbSet<ImportJob> ImportJobs { get; }
    DbSet<ImportPart> ImportParts { get; }
    DbSet<ImportRow> ImportRows { get; }
    DbSet<PreparedUpload> PreparedUploads { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
