using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

if (args is ["--init-storage"])
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(
            Environment.GetEnvironmentVariable("Storage__AccessKey"), Environment.GetEnvironmentVariable("Storage__SecretKey")),
            new AmazonS3Config { ServiceURL = Environment.GetEnvironmentVariable("Storage__Endpoint"), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var bucket = Environment.GetEnvironmentVariable("Storage__Bucket") ?? "dataflow-imports";
        try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, timeout.Token); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou") { }
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }, timeout.Token);
        Console.WriteLine("Private storage bucket provisioned.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Storage initialization failed ({ex.GetType().Name}).");
        return 1;
    }
}

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DataFlow");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__DataFlow is required.");
    return 2;
}
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(timeout.Token);
    // A session lock covers the whole migration sequence, including EF's own transactions.
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT pg_advisory_lock(741821101)";
    await command.ExecuteNonQueryAsync(timeout.Token);
    await using var db = new DataFlowDbContext(new DbContextOptionsBuilder<DataFlowDbContext>().UseNpgsql(connection).Options);
    await db.Database.MigrateAsync(timeout.Token);
    command.CommandText = "SELECT pg_advisory_unlock(741821101)";
    await command.ExecuteNonQueryAsync(timeout.Token);
    Console.WriteLine("DataFlow migrations applied successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed ({ex.GetType().Name}). Check database availability and migration compatibility.");
    return 1;
}
