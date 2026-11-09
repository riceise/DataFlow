using System.Text.Json;
using DataFlow.Application.Abstractions;
using DataFlow.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DataFlow.Infrastructure.Persistence;

public sealed class DataFlowDbContext(DbContextOptions<DataFlowDbContext> options) : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataFlowDbContext
{
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<UploadRequest> UploadRequests => Set<UploadRequest>();
    public DbSet<RuleDocument> RuleDocuments => Set<RuleDocument>();
    public DbSet<RuleSet> RuleSets => Set<RuleSet>();
    public DbSet<RuleSetVersion> RuleSetVersions => Set<RuleSetVersion>();
    public DbSet<RuleVersionGrant> RuleVersionGrants => Set<RuleVersionGrant>();
    public DbSet<RuleVersionPublication> RuleVersionPublications => Set<RuleVersionPublication>();
    public DbSet<UserRuleLibraryEntry> UserRuleLibraryEntries => Set<UserRuleLibraryEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>()
            .HaveColumnType("timestamp with time zone");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DataFlowDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            }
        }
    }
}

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(), value => value.ToUniversalTime());
