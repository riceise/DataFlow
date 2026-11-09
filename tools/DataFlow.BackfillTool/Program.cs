using DataFlow.Infrastructure.Migrations;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length != 1 || !Guid.TryParse(args[0], out var userId) || userId == Guid.Empty)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/DataFlow.BackfillTool -- <existing-user-guid>");
    return 1;
}

using var host = Host.CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["ConnectionStrings:DataFlow"]
            ?? throw new InvalidOperationException("ConnectionStrings:DataFlow is required");

        services.AddDbContext<DataFlowDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<BackfillRulesCommand>();
    })
    .Build();

await using var scope = host.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<DataFlowDbContext>();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
var command = scope.ServiceProvider.GetRequiredService<BackfillRulesCommand>();

logger.LogInformation("Starting backfill for user {UserId}", userId);

try
{
    var result = await command.ExecuteAsync(userId, CancellationToken.None);
    logger.LogInformation("Backfill completed successfully: {Result}", result);

    // Verify integrity
    var unmapped = await db.ImportJobs.CountAsync(j => j.RuleDocumentId == null || j.RuleSetVersionId == null);
    if (unmapped > 0)
    {
        logger.LogWarning("{Count} jobs still unmapped after backfill", unmapped);
        return 1;
    }

    var orphanSources = await db.DataSources.CountAsync(s => s.OwnerUserId == null || s.DefaultRuleSetVersionId == null);
    if (orphanSources > 0)
    {
        logger.LogWarning("{Count} sources still unowned after backfill", orphanSources);
        return 1;
    }

    logger.LogInformation("Integrity check passed. All sources and jobs are now mapped.");
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Backfill failed");
    return 1;
}
