using DataFlow.Application.Uploads;
using DataFlow.Domain.Enums;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Application.Tests;

public sealed class UploadFingerprintTests
{
    private static readonly Guid User = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Source = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid Prepared = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
    private static readonly Guid Version = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Hash = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

    [Fact]
    public void PrepareV3_MatchesGoldenCanonicalJsonHash()
    {
        Assert.Equal("7520b53bc69964575687732a76367ad46153735eaa454ba0765d9de7ac9e9df8",
            UploadFingerprints.Prepare(User, Source, Hash, AcceptedFileFormat.Zip));
    }

    [Fact]
    public void CommitV3_MatchesGoldenIncludingDisabledPart()
    {
        var parts = new[]
        {
            new CommitFingerprintPart(1, false, null, null),
            new CommitFingerprintPart(0, true, Version, "customer:job")
        };
        Assert.Equal("5c2825670b910b0dc33eaccdc6d26feb03ba58235852c97d675b9b55c2ff3302",
            UploadFingerprints.Commit(User, Source, Prepared, Hash, AcceptedFileFormat.Zip, parts));
    }

    [Theory]
    [InlineData(DataSourceDataType.Customer, 3, null, "customer:job")]
    [InlineData(DataSourceDataType.Tabular, 3, null, "part:3")]
    [InlineData(DataSourceDataType.Tabular, 3, " Sales.Q1 ", "tabular-group:sales.q1")]
    [InlineData(DataSourceDataType.Tabular, 3, "SALES.Q1", "tabular-group:sales.q1")]
    public void Scopes_AreCanonical(DataSourceDataType type, int index, string? group, string expected) =>
        Assert.Equal(expected, DeduplicationScopes.Create(type, index, group));

    [Theory]
    [InlineData("")]
    [InlineData("bad group")]
    [InlineData("part:1")]
    [InlineData("é")]
    [InlineData(".leading")]
    public void InvalidGroups_AreRejected(string group) =>
        Assert.Throws<DomainException>(() => DeduplicationScopes.Create(DataSourceDataType.Tabular, 0, group));

    [Fact]
    public void CustomerExplicitGroup_IsRejected() =>
        Assert.Throws<DomainException>(() => DeduplicationScopes.Create(DataSourceDataType.Customer, 0, "sales"));
}
