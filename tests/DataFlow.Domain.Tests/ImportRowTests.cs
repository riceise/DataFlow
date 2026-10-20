using DataFlow.Domain.Entities;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;

namespace DataFlow.Domain.Tests;

public sealed class ImportRowTests
{
    [Theory]
    [InlineData(ImportRowStatus.Inserted)]
    [InlineData(ImportRowStatus.Updated)]
    [InlineData(ImportRowStatus.Duplicate)]
    public void SuccessfulRow_CanOmitRawDataAndErrors(ImportRowStatus status)
    {
        var row = ImportRow.Create(Guid.NewGuid(), 2, status, null, "key", null, DateTimeOffset.UtcNow);
        Assert.Equal(status, row.Status);
        Assert.Null(row.RawData);
        Assert.Empty(row.GetErrors());
    }

    [Fact]
    public void InvalidRow_RequiresOriginalDataAndErrorDetails()
    {
        var jobId = Guid.NewGuid();
        var errors = new[] { new ImportRowError("email", "invalid_email", "Invalid email") };
        Assert.Throws<ArgumentException>(() => ImportRow.Create(jobId, 2, ImportRowStatus.Invalid, null, null, errors, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => ImportRow.Create(jobId, 2, ImportRowStatus.Invalid, "{}", null, null, DateTimeOffset.UtcNow));
        var row = ImportRow.Create(jobId, 2, ImportRowStatus.Invalid, "{}", null, errors, DateTimeOffset.UtcNow);
        Assert.Equal(errors, row.GetErrors());
    }

    [Theory]
    [InlineData(0, ImportRowStatus.Inserted)]
    [InlineData(-1, ImportRowStatus.Inserted)]
    [InlineData(2, (ImportRowStatus)99)]
    public void Row_RejectsInvalidNumberAndStatus(int number, ImportRowStatus status)
    {
        Assert.Throws<ArgumentException>(() =>
            ImportRow.Create(Guid.NewGuid(), number, status, null, null, null, DateTimeOffset.UtcNow));
    }
}
