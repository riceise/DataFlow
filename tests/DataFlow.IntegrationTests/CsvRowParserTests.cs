using System.Text;
using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Parsing;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

// Parser unit tests: no database, broker or storage fixture is needed.
public sealed class CsvRowParserTests
{
    private const string Header = "externalId,name,email,registeredAt,isActive";
    private const string ValidRow = "ID-1,Alice,a@example.com,2024-02-29,true";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf8WithOrWithoutBom_PreservesCellsAndHeaderMapping(bool bom)
    {
        var csv = "email,isActive,name,externalId,registeredAt\r\na@example.com,true, Анна , ID-1 ,2024-02-29";
        var bytes = Encoding.UTF8.GetBytes(csv);
        await using var stream = new MemoryStream(bom ? [.. Encoding.UTF8.GetPreamble(), .. bytes] : bytes);
        var row = Assert.Single(await ReadAll(stream));
        Assert.Equal(2, row.RowNumber);
        Assert.Equal(2, row.CheckpointRowNumber);
        Assert.Equal(" Анна ", row.Values["name"]);
        Assert.Equal(" ID-1 ", row.Values["externalId"]);
        Assert.Equal("a@example.com", row.Values["email"]);
        Assert.Empty(row.Errors!);
        Assert.False(row.IsEmpty);
        Assert.True(stream.CanRead);
        using var raw = JsonDocument.Parse(row.RawData);
        Assert.Equal(" Анна ", raw.RootElement[2].GetString());
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    public async Task DelimiterComesFromSnapshot_AndQuotingPreservesDelimiterAndEscapedQuotes(string delimiter)
    {
        var csv = string.Join(delimiter, ["name", "externalId", "isActive", "email", "registeredAt"]) + "\n" +
            $"\"A{delimiter}B \"\"C\"\"\"{delimiter}ID{delimiter}false{delimiter}a@b.com{delimiter}";
        var row = Assert.Single(await ReadAll(csv, schema: Schema(delimiter)));
        Assert.Equal($"A{delimiter}B \"C\"", row.Values["name"]);
        Assert.Equal("", row.Values["registeredAt"]);
        Assert.Empty(row.Errors!);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task PhysicalNumbersIncludeBlankAndMalformedRows_AndMultilineCheckpointEndsAtRecordBoundary(string newline)
    {
        var csv = Header + newline + newline + " \t " + newline +
            $"ID-1,\"Alice{newline}Smith\",a@example.com,,true" + newline +
            "short,row" + newline + ValidRow + newline + newline;
        var rows = await ReadAll(csv);
        Assert.Equal(new[] { 2, 3, 4, 6, 7, 8 }, rows.Select(row => row.RowNumber));
        Assert.Equal(new[] { 2, 3, 5, 6, 7, 8 }, rows.Select(row => row.CheckpointRowNumber));
        Assert.True(rows[0].IsEmpty);
        Assert.True(rows[1].IsEmpty);
        Assert.True(rows[5].IsEmpty);
        Assert.False(rows[2].IsEmpty);
        Assert.Equal($"Alice{newline}Smith", rows[2].Values["name"]);
        Assert.Equal("column_count_mismatch", Assert.Single(rows[3].Errors!).Code);
        Assert.Empty(rows[4].Errors!);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("")]
    public async Task TrailingNewlineDoesNotInventAnExtraRow(string ending)
    {
        var row = Assert.Single(await ReadAll(Header + "\n" + ValidRow + ending));
        Assert.Equal(2, row.RowNumber);
    }

    [Fact]
    public async Task ExplicitEmptyCellsAndQuotedWhitespace_AreDataNotBlankRows()
    {
        var rows = await ReadAll(Header + "\n,,,,\n\"\"\n\"  \"\n");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsEmpty));
        Assert.Empty(rows[0].Errors!);
        var processor = new CustomerFileRowProcessor(Guid.NewGuid(), Schema());
        var result = processor.Process(rows[0]);
        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal("required", error.Code));
    }

    [Theory]
    [InlineData("short,row", 2)]
    [InlineData("ID-1,Alice,a@b.com,,true,extra", 6)]
    public async Task ColumnCountMismatch_IsARowError_PreservesAllRawCellsAndContinues(string invalid, int cellCount)
    {
        var rows = await ReadAll(Header + "\n" + invalid + "\n" + ValidRow);
        var error = Assert.Single(rows[0].Errors!);
        Assert.Equal("$row", error.Field);
        Assert.Equal("column_count_mismatch", error.Code);
        using var raw = JsonDocument.Parse(rows[0].RawData);
        Assert.Equal(cellCount, raw.RootElement.GetArrayLength());
        Assert.Equal(3, rows[1].RowNumber);
        Assert.Empty(rows[1].Errors!);
        var processor = new CustomerFileRowProcessor(Guid.NewGuid(), Schema());
        Assert.False(processor.Process(rows[0]).IsValid);
    }

    [Theory]
    [InlineData("", "empty_file")]
    [InlineData("\n", "empty_csv_header")]
    [InlineData("externalId,name,email,registeredAt,", "empty_csv_header")]
    [InlineData("externalId,name,email,registeredAt,name", "duplicate_csv_header")]
    [InlineData("externalId,name,email,registeredAt,NAME", "duplicate_csv_header")]
    [InlineData("externalId,name,email,registeredAt,secretValue", "unknown_csv_header")]
    [InlineData("externalId,name,email,isActive", "missing_csv_header")]
    [InlineData("externalId,name,email,registeredAt,IsActive", "unknown_csv_header")]
    [InlineData("externalId, name,email,registeredAt,isActive", "unknown_csv_header")]
    public async Task InvalidHeaders_AreControlledFileRejections(string csv, string code)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(csv));
        Assert.Equal(code, exception.Code);
        Assert.Equal(1, exception.RowNumber);
        Assert.DoesNotContain("secretValue", exception.ToString());
    }

    [Fact]
    public async Task HeaderOnly_IsValidEmptyImport_AndOptionalFieldCanBeOmittedFromSnapshot()
    {
        Assert.Empty(await ReadAll(Header));
        var schema = new DataSourceSchema(1, ["externalId"], Schema().Fields.Where(field => field.Name != "registeredAt").ToArray());
        var row = Assert.Single(await ReadAll("externalId,name,email,isActive\nID,Alice,a@b.com,true", schema: schema));
        var result = new CustomerRowNormalizer(schema).Normalize(Guid.NewGuid(), row);
        Assert.True(result.IsValid);
        Assert.Null(result.Customer!.RegisteredAt);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    public async Task InvalidDelimiter_IsControlledBeforeReading(string delimiter)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(Header, schema: Schema(delimiter)));
        Assert.Equal("invalid_csv_delimiter", exception.Code);
    }

    [Theory]
    [InlineData("ID-1,\"private email,a@b.com,,true")]
    [InlineData("ID-1,A\"private email,a@b.com,,true")]
    [InlineData("ID-1,\"private email\"x,a@b.com,,true")]
    public async Task InvalidQuoting_IsSafeFileRejection(string malformed)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(Header + "\n" + malformed));
        Assert.Equal("invalid_csv_syntax", exception.Code);
        Assert.Equal(2, exception.RowNumber);
        Assert.DoesNotContain("private email", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonUtf8Input_IsRejectedWithoutLeakingBytes(bool utf16)
    {
        byte[] bytes = utf16
            ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Header)]
            : [.. Encoding.UTF8.GetBytes(Header + "\n"), 0xff, 0xfe];
        await using var stream = new MemoryStream(bytes);
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(stream));
        Assert.Equal("invalid_csv_encoding", exception.Code);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task RowLimitCountsInvalidDataButNotBlanks_AndKeepsPhysicalErrorNumber()
    {
        var options = new ImportProcessingOptions { MaxRows = 2 };
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header + "\n\n" + ValidRow + "\nshort\n\n" + ValidRow));
        await using var enumerator = Parser(options).ParseAsync(stream, Schema()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(enumerator.Current.IsEmpty);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("column_count_mismatch", Assert.Single(enumerator.Current.Errors!).Code);
        Assert.True(await enumerator.MoveNextAsync());
        var exception = await Assert.ThrowsAsync<ImportFileException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal("row_limit_exceeded", exception.Code);
        Assert.Equal(6, exception.RowNumber);
    }

    [Theory]
    [InlineData("id,name\na,b", "column_limit_exceeded", 1)]
    [InlineData("id\na,b", "column_limit_exceeded", 2)]
    public async Task ColumnLimitAppliesToHeaderAndData(string csv, string code, int row)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(csv,
            new ImportProcessingOptions { MaxColumns = 1 }, SimpleSchema()));
        Assert.Equal(code, exception.Code);
        Assert.Equal(row, exception.RowNumber);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("\"abc\"")]
    [InlineData("\"\"\"\"\"\"\"\"")]
    public async Task CellLengthBoundaryMeasuresDecodedCell_NotQuoteEscaping(string cell)
    {
        var row = Assert.Single(await ReadAll("id\n" + cell, new ImportProcessingOptions { MaxCellLength = 3 }, SimpleSchema()));
        Assert.Equal(3, row.Values["id"]!.Length);
    }

    [Theory]
    [InlineData("id\nabcd", 2)]
    [InlineData("id\n\"abcd\"", 2)]
    [InlineData("abcd\nx", 1)]
    public async Task CellLimitAppliesToHeaderAndData(string csv, int row)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(csv,
            new ImportProcessingOptions { MaxCellLength = 3 }, SimpleSchema()));
        Assert.Equal("cell_limit_exceeded", exception.Code);
        Assert.Equal(row, exception.RowNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedRecordIsBoundedBeforeReadingWholeInput(bool manyColumns)
    {
        var contents = "id\n" + (manyColumns ? string.Concat(Enumerable.Repeat("a,", 100_000)) : "\"" + new string('a', 100_000));
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes(contents), chunkSize: 7);
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => ReadAll(stream,
            new ImportProcessingOptions { MaxColumns = 1, MaxCellLength = 3 }, SimpleSchema()));
        Assert.Contains(exception.Code, new[] { "cell_limit_exceeded", "csv_record_too_large" });
        Assert.Equal(2, exception.RowNumber);
        Assert.True(stream.BytesRead < 100, $"Read {stream.BytesRead} bytes instead of rejecting early.");
    }

    [Fact]
    public async Task AsyncOnlyNonSeekableStream_IsLazy_AndEarlyDisposeLeavesItOpen()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "\n" + string.Concat(Enumerable.Repeat(ValidRow + "\n", 50_000)));
        await using var stream = new AsyncOnlyStream(bytes, chunkSize: 13);
        var rows = Parser().ParseAsync(stream, Schema());
        Assert.Equal(0, stream.BytesRead);
        await using (var enumerator = rows.GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(2, enumerator.Current.RowNumber);
            Assert.True(stream.BytesRead < 4096);
        }

        Assert.True(stream.CanRead);
        Assert.False(stream.IsDisposed);
    }

    [Fact]
    public async Task CancellationIsObservedBetweenRowsEvenWhenInputIsBuffered()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header + "\n" + ValidRow + "\n" + ValidRow));
        await using var enumerator = Parser().ParseAsync(stream, Schema(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task CancellationReachesPendingStreamRead()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new BlockingStream();
        await using var enumerator = Parser().ParseAsync(stream, Schema(), cancellation.Token).GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.ReadWasCancelled);
    }

    [Fact]
    public async Task PhysicalNumbersRemainCorrectAcrossTinyReadBoundaries()
    {
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes(Header + "\r\n" +
            "ID-1,\"A\r\nБ\"\"В\",a@b.com,,true\r\n\r\n" + ValidRow), chunkSize: 1);
        var rows = await ReadAll(stream);
        Assert.Equal(new[] { 2, 4, 5 }, rows.Select(row => row.RowNumber));
        Assert.Equal(3, rows[0].CheckpointRowNumber);
        Assert.Equal("A\r\nБ\"В", rows[0].Values["name"]);
    }

    [Fact]
    public async Task RowLimitAllowsExactlyTheConfiguredNumberOfRecords()
    {
        var rows = await ReadAll(Header + "\n" + ValidRow + "\n\n" + ValidRow + "\n",
            new ImportProcessingOptions { MaxRows = 2, MaxColumns = 5, MaxCellLength = 16 });
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(row => !row.IsEmpty));
    }

    [Fact]
    public async Task FullFileCanBeConsumedIncrementallyWithoutBufferingAllRows()
    {
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes(Header + "\n" +
            string.Concat(Enumerable.Repeat(ValidRow + "\n", 50_000))), chunkSize: 1024);
        var count = 0;
        await foreach (var row in Parser().ParseAsync(stream, Schema()))
        {
            Assert.Equal(++count + 1, row.RowNumber);
            Assert.Equal(row.RowNumber, row.CheckpointRowNumber);
        }

        Assert.Equal(50_000, count);
    }

    [Fact]
    public async Task PreCancelledEnumerationDoesNotReadInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes(Header), chunkSize: 1);
        await using var enumerator = Parser().ParseAsync(stream, Schema(), cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task HashAndCommentLookingCellsAreTreatedAsOrdinaryData()
    {
        var rows = await ReadAll("id\n#comment\n=1+1", schema: SimpleSchema());
        Assert.Equal(2, rows.Count);
        Assert.Equal("#comment", rows[0].Values["id"]);
        Assert.Equal("=1+1", rows[1].Values["id"]);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(257, 100)]
    [InlineData(64, 0)]
    [InlineData(64, 65_537)]
    public async Task InvalidParserLimitsAreRejected(int columns, int cellLength)
    {
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => ReadAll(Header,
            new ImportProcessingOptions { MaxColumns = columns, MaxCellLength = cellLength }));
    }

    private static CsvRowParser Parser(ImportProcessingOptions? options = null) => new(Options.Create(options ?? new()));

    private static DataSourceSchema SimpleSchema() => new(1, ["id"], [new("id", "string", true)]);

    private static DataSourceSchema Schema(string delimiter = ",") => new(1, ["externalId"],
    [
        new("externalId", "string", true, 80), new("name", "string", true, 200),
        new("email", "email", true, 254), new("registeredAt", "date", false, format: "yyyy-MM-dd"),
        new("isActive", "boolean", true)
    ], delimiter);

    private static async Task<List<ParsedRow>> ReadAll(string text, ImportProcessingOptions? options = null, DataSourceSchema? schema = null)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await ReadAll(stream, options, schema);
    }

    private static async Task<List<ParsedRow>> ReadAll(Stream stream, ImportProcessingOptions? options = null, DataSourceSchema? schema = null)
    {
        var rows = new List<ParsedRow>();
        await foreach (var row in Parser(options).ParseAsync(stream, schema ?? Schema()))
        {
            rows.Add(row);
        }

        return rows;
    }

    private sealed class AsyncOnlyStream(byte[] bytes, int chunkSize) : Stream
    {
        public int BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O is not allowed.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadWasCancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadWasCancelled = true;
                throw;
            }
        }
    }
}
