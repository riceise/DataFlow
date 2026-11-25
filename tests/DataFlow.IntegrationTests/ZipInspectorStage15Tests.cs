using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DataFlow.Application.Imports;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Parsing;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class ZipInspectorStage15Tests
{
    [Theory]
    [InlineData("../x.csv")]
    [InlineData("nested/x.csv")]
    [InlineData("x.txt")]
    [InlineData("x.csv ")]
    [InlineData("x.csv.")]
    public async Task RootNamePolicy_RejectsUnsafeAndUnsupportedNames(string name)
    {
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(Zip((name, "code\nx\n"))));
        Assert.Equal("invalid_zip_entry_name", exception.Code);
    }

    [Fact]
    public async Task DuplicateNames_UseNfcAndOrdinalIgnoreCase()
    {
        foreach (var entries in new[]
                 {
                     new[] { ("A.csv", "code\na\n"), ("a.CSV", "code\nb\n") },
                     new[] { ("e\u0301.csv", "code\na\n"), ("é.csv", "code\nb\n") }
                 })
        {
            var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(Zip(entries)));
            Assert.Equal("duplicate_zip_entry_name", exception.Code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(13)]
    public async Task EncryptionFlags_AreRejectedFromRawHeadersBeforeOpening(int bit)
    {
        var bytes = Zip(("a.csv", "code\nx\n"));
        SetFlag(bytes, bit);
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(bytes));
        Assert.Equal("encrypted_zip_entry_not_supported", exception.Code);
    }

    [Fact]
    public async Task CentralLocalMethodMismatch_IsRejected()
    {
        var bytes = Zip(("a.csv", "code\nx\n"));
        var local = FindSignature(bytes, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8, 2), 0);
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(bytes));
        Assert.Equal("zip_central_local_mismatch", exception.Code);
    }

    [Fact]
    public async Task TruncatedArchive_IsSafeBadRequestError()
    {
        var bytes = Zip(("a.csv", "code\nx\n"));
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(bytes[..(bytes.Length / 2)]));
        Assert.True(exception.Code is "invalid_zip_archive" or "unsupported_zip_structure");
    }

    [Fact]
    public async Task Zip64LocalSizesAndDescriptor_AreValidated()
    {
        var bytes = Zip64Stored("code\nx\n");
        var inspection = await Inspect(bytes);
        var part = Assert.Single(inspection.Parts);
        Assert.Equal("a.csv", part.SourceName);
        Assert.Equal(new[] { "code" }, part.Headers);

        var corrupted = Zip64Stored("code\nx\n");
        corrupted[^24] ^= 1;
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(corrupted));
        Assert.True(exception.Code is "zip_central_local_mismatch" or "invalid_zip_archive");
    }

    private static Task<DataFlow.Application.Uploads.UploadManifestInspection> Inspect(byte[] bytes)
    {
        var options = Options.Create(new ImportProcessingOptions());
        var inspector = new ZipCsvUploadInspector(new CsvUploadInspector(options), options);
        var schema = new DataSourceSchema(1, ["code"], [new DataSourceFieldDefinition("code", "string", true)]);
        return inspector.InspectAsync(new MemoryStream(bytes, writable: false), schema, new PreviewBudget(1024 * 1024),
            CancellationToken.None);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }

        return memory.ToArray();
    }

    private static void SetFlag(byte[] bytes, int bit)
    {
        var value = (ushort)(1 << bit);
        foreach (var (signature, offset) in new[] { (0x04034b50U, 6), (0x02014b50U, 8) })
        {
            var position = FindSignature(bytes, signature);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + offset, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(position + offset, 2), (ushort)(flags | value));
        }
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature)
            {
                return index;
            }
        }

        throw new InvalidOperationException("ZIP signature was not found.");
    }

    private static byte[] Zip64Stored(string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var name = "a.csv"u8.ToArray();
        var crc = Crc32(data);
        using var stream = new MemoryStream();
        void UInt16(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes); }
        void UInt32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
        void UInt64(ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); stream.Write(bytes); }
        void Extra()
        {
            UInt16(1); UInt16(16); UInt64((ulong)data.Length); UInt64((ulong)data.Length);
        }

        UInt32(0x04034b50); UInt16(45); UInt16(8); UInt16(0); UInt16(0); UInt16(0); UInt32(0);
        UInt32(uint.MaxValue); UInt32(uint.MaxValue); UInt16((ushort)name.Length); UInt16(20);
        stream.Write(name); Extra(); stream.Write(data);
        UInt32(0x08074b50); UInt32(crc); UInt64((ulong)data.Length); UInt64((ulong)data.Length);
        var central = stream.Position;
        UInt32(0x02014b50); UInt16(45); UInt16(45); UInt16(8); UInt16(0); UInt16(0); UInt16(0); UInt32(crc);
        UInt32(uint.MaxValue); UInt32(uint.MaxValue); UInt16((ushort)name.Length); UInt16(20); UInt16(0);
        UInt16(0); UInt16(0); UInt32(0); UInt32(0); stream.Write(name); Extra();
        var centralSize = stream.Position - central;
        UInt32(0x06054b50); UInt16(0); UInt16(0); UInt16(1); UInt16(1); UInt32((uint)centralSize);
        UInt32((uint)central); UInt16(0);
        return stream.ToArray();
    }

    [Fact]
    public async Task StoredEntry_WithMismatchedCrc_IsRejected()
    {
        var data = Encoding.UTF8.GetBytes("code\nx\n");
        var staleCrc = Crc32(Encoding.UTF8.GetBytes("code\ny\n"));
        var bytes = StoredZip(data, staleCrc);
        var exception = await Assert.ThrowsAsync<ImportFileException>(() => Inspect(bytes));
        Assert.Equal("zip_entry_crc_mismatch", exception.Code);
    }

    [Fact]
    public async Task StoredEntry_WithValidCrc_IsAccepted()
    {
        var data = Encoding.UTF8.GetBytes("code\nx\n");
        var bytes = StoredZip(data, Crc32(data));
        var inspection = await Inspect(bytes);
        Assert.Single(inspection.Parts);
    }

    private static byte[] StoredZip(byte[] data, uint crc)
    {
        using var stream = new MemoryStream();
        void UInt16(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes); }
        void UInt32(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
        UInt32(0x04034b50); UInt16(20); UInt16(0); UInt16(0); UInt16(0); UInt16(0);
        UInt32(crc); UInt32((uint)data.Length); UInt32((uint)data.Length);
        var name = "a.csv"u8.ToArray();
        UInt16((ushort)name.Length); UInt16(0);
        stream.Write(name); stream.Write(data);
        var central = stream.Position;
        UInt32(0x02014b50); UInt16(20); UInt16(20); UInt16(0); UInt16(0); UInt16(0); UInt16(0);
        UInt32(crc); UInt32((uint)data.Length); UInt32((uint)data.Length);
        UInt16((ushort)name.Length); UInt16(0); UInt16(0); UInt16(0); UInt16(0); UInt32(0); UInt32(0);
        stream.Write(name);
        var centralSize = stream.Position - central;
        UInt32(0x06054b50); UInt16(0); UInt16(0); UInt16(1); UInt16(1);
        UInt32((uint)centralSize); UInt32((uint)central); UInt16(0);
        return stream.ToArray();
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320U & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}
