using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DataFlow.Application.Imports;
using DataFlow.Application.Uploads;
using DataFlow.Domain.Enums;
using DataFlow.Domain.ValueObjects;
using DataFlow.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DataFlow.Infrastructure.Parsing;

public sealed class ZipCsvUploadInspector(CsvUploadInspector csv, IOptions<ImportProcessingOptions> options)
{
    public async Task<UploadManifestInspection> InspectAsync(Stream stream, DataSourceSchema preliminarySchema,
        PreviewBudget previewBudget, CancellationToken cancellationToken)
    {
        try
        {
            return await InspectCoreAsync(stream, preliminarySchema, previewBudget, cancellationToken);
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            throw Error("invalid_zip_archive");
        }
    }

    private async Task<UploadManifestInspection> InspectCoreAsync(Stream stream, DataSourceSchema preliminarySchema,
        PreviewBudget previewBudget, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek)
        {
            throw Error("invalid_zip_archive");
        }

        var settings = options.Value;
        if (stream.Length > settings.Zip.MaxArchiveBytes)
        {
            throw Error("file_too_large");
        }

        var rawEntries = await ReadCentralDirectoryAsync(stream, settings, cancellationToken);
        ZipArchive archive;
        try { archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw Error("invalid_zip_archive"); }

        using (archive)
        {
            if (archive.Entries.Count != rawEntries.Count || archive.Entries.Count > settings.Zip.MaxEntries ||
                rawEntries.Count > Math.Min(settings.MaxCsvFilesPerArchive, settings.Zip.MaxCsvFiles) ||
                rawEntries.Count > settings.MaxPartsPerJob)
            {
                throw Error("zip_entry_limit_exceeded");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong declaredTotal = 0;
            for (var index = 0; index < rawEntries.Count; index++)
            {
                var raw = rawEntries[index];
                var entry = archive.Entries[index];
                if (!string.Equals(raw.Name, entry.FullName, StringComparison.Ordinal))
                {
                    throw Error("zip_central_local_mismatch");
                }

                ValidateName(raw.Name, settings.Zip.MaxFileNameLength);
                if (!names.Add(raw.Name.Normalize(NormalizationForm.FormC)))
                {
                    throw Error("duplicate_zip_entry_name");
                }

                await ValidateLocalHeaderAsync(stream, raw, cancellationToken);
                if (raw.Encrypted || raw.LocalEncrypted)
                {
                    throw Error("encrypted_zip_entry_not_supported");
                }

                if (raw.Method is not (0 or 8) || raw.Flags != raw.LocalFlags ||
                    raw.Method != raw.LocalMethod || !raw.NameBytes.SequenceEqual(raw.LocalNameBytes))
                {
                    throw Error("zip_central_local_mismatch");
                }

                if ((long)entry.Length != (long)raw.UncompressedSize || (long)entry.CompressedLength != (long)raw.CompressedSize)
                {
                    throw Error("zip_central_local_mismatch");
                }

                declaredTotal = checked(declaredTotal + raw.UncompressedSize);
                if (raw.UncompressedSize > (ulong)settings.Zip.MaxEntryBytes || declaredTotal > (ulong)settings.Zip.MaxTotalUncompressedBytes)
                {
                    throw Error("zip_uncompressed_limit_exceeded");
                }

            }

            var parts = new List<UploadManifestPart>(rawEntries.Count);
            long actualTotal = 0;
            for (var index = 0; index < rawEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = rawEntries[index];
                try
                {
                    await using var entryStream = archive.Entries[index].Open();
                    await using var bounded = new BoundedZipEntryStream(entryStream, (long)raw.UncompressedSize,
                        settings.Zip.MaxTotalUncompressedBytes - actualTotal);
                    var part = await csv.InspectAsync(bounded, raw.Name, index, preliminarySchema, previewBudget,
                        ImportPartType.ZipCsvEntry, $"zip:central:{index}", cancellationToken);
                    if ((ulong)bounded.Length != raw.UncompressedSize)
                    {
                        throw Error("zip_entry_size_mismatch");
                    }

                    if (bounded.Crc32 != raw.Crc)
                    {
                        throw Error("zip_entry_crc_mismatch");
                    }

                    actualTotal = checked(actualTotal + bounded.Length);
                    parts.Add(part);
                }
                catch (ImportFileException)
                {
                    throw;
                }
                catch (InvalidDataException)
                {
                    throw Error("invalid_zip_archive");
                }
                catch (IOException)
                {
                    throw Error("invalid_zip_archive");
                }
            }

            var metrics = new UploadContainerMetrics(stream.Length, rawEntries.Count, (long)declaredTotal,
                rawEntries.Count == 0 ? 0 : (long)rawEntries.Max(entry => entry.UncompressedSize));
            return new UploadManifestInspection(parts, metrics);
        }
    }

    private static async Task<List<RawZipEntry>> ReadCentralDirectoryAsync(Stream stream, ImportProcessingOptions settings,
        CancellationToken cancellationToken)
    {
        if (stream.Length < 22)
        {
            throw Error("invalid_zip_archive");
        }

        var tailLength = (int)Math.Min(stream.Length, 65_557);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, cancellationToken);
        var eocd = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (ReadUInt32(tail, index) == 0x06054b50)
            {
                eocd = index;
                break;
            }
        }

        if (eocd < 0 || ReadUInt16(tail, eocd + 4) != 0 || ReadUInt16(tail, eocd + 6) != 0)
        {
            throw Error("unsupported_zip_structure");
        }

        ulong count = ReadUInt16(tail, eocd + 10);
        ulong centralSize = ReadUInt32(tail, eocd + 12);
        ulong centralOffset = ReadUInt32(tail, eocd + 16);
        var diskEntries = ReadUInt16(tail, eocd + 8);
        var eocdOffset = (ulong)(stream.Length - tailLength + eocd);
        if (diskEntries != count || ReadUInt16(tail, eocd + 20) + 22 + eocd != tail.Length)
        {
            throw Error("invalid_zip_archive");
        }

        var zip64 = count == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue ||
            diskEntries == ushort.MaxValue;
        ulong structuralEnd = eocdOffset;
        if (zip64)
        {
            if (eocd < 20 || ReadUInt32(tail, eocd - 20) != 0x07064b50 ||
                ReadUInt32(tail, eocd - 16) != 0 || ReadUInt32(tail, eocd - 4) != 1)
            {
                throw Error("unsupported_zip_structure");
            }

            var zip64Offset = ReadUInt64(tail, eocd - 12);
            if (zip64Offset > (ulong)(stream.Length - 56))
            {
                throw Error("invalid_zip_archive");
            }

            stream.Position = (long)zip64Offset;
            var zip64Header = new byte[56];
            await stream.ReadExactlyAsync(zip64Header, cancellationToken);
            var recordSize = ReadUInt64(zip64Header, 4);
            if (ReadUInt32(zip64Header, 0) != 0x06064b50 || ReadUInt32(zip64Header, 16) != 0 ||
                ReadUInt32(zip64Header, 20) != 0 || ReadUInt64(zip64Header, 24) != ReadUInt64(zip64Header, 32) ||
                recordSize < 44 || zip64Offset + 12 + recordSize > eocdOffset - 20)
            {
                throw Error("unsupported_zip_structure");
            }

            count = ReadUInt64(zip64Header, 32);
            centralSize = ReadUInt64(zip64Header, 40);
            centralOffset = ReadUInt64(zip64Header, 48);
            structuralEnd = zip64Offset;
        }

        if (centralOffset > (ulong)stream.Length || centralSize > (ulong)stream.Length - centralOffset ||
            centralOffset + centralSize != structuralEnd)
        {
            throw Error("invalid_zip_archive");
        }

        if (count > (ulong)settings.Zip.MaxEntries || centralOffset >= (ulong)stream.Length)
        {
            throw Error("zip_entry_limit_exceeded");
        }

        stream.Position = (long)centralOffset;
        var entries = new List<RawZipEntry>((int)count);
        for (ulong index = 0; index < count; index++)
        {
            var header = new byte[46];
            await stream.ReadExactlyAsync(header, cancellationToken);
            if (ReadUInt32(header, 0) != 0x02014b50)
            {
                throw Error("invalid_zip_archive");
            }

            var flags = ReadUInt16(header, 8);
            var method = ReadUInt16(header, 10);
            var crc = ReadUInt32(header, 16);
            var compressed = ReadUInt32(header, 20);
            var uncompressed = ReadUInt32(header, 24);
            var nameLength = ReadUInt16(header, 28);
            var extraLength = ReadUInt16(header, 30);
            var commentLength = ReadUInt16(header, 32);
            if (ReadUInt16(header, 34) != 0 || nameLength == 0)
            {
                throw Error("unsupported_zip_structure");
            }

            var localOffset = ReadUInt32(header, 42);
            var variableLength = checked(nameLength + extraLength + commentLength);
            if (variableLength > settings.Zip.MaxFileNameLength + 65_535 + 65_535)
            {
                throw Error("invalid_zip_archive");
            }

            var variable = new byte[variableLength];
            await stream.ReadExactlyAsync(variable, cancellationToken);
            var nameBytes = variable.AsSpan(0, nameLength).ToArray();
            string name;
            try { name = new UTF8Encoding(false, true).GetString(nameBytes); }
            catch (DecoderFallbackException) { throw Error("invalid_zip_entry_name"); }
            var extra = variable.AsSpan(nameLength, extraLength);
            ulong compressed64 = compressed;
            ulong uncompressed64 = uncompressed;
            ulong offset64 = localOffset;
            ParseZip64Extra(extra, uncompressed == uint.MaxValue, compressed == uint.MaxValue, localOffset == uint.MaxValue,
                ref uncompressed64, ref compressed64, ref offset64);
            entries.Add(new RawZipEntry(name, nameBytes, flags, method, crc, compressed64, uncompressed64, offset64,
                uncompressed == uint.MaxValue || compressed == uint.MaxValue));
        }

        if ((ulong)stream.Position != centralOffset + centralSize)
        {
            throw Error("invalid_zip_archive");
        }

        foreach (var entry in entries)
        {
            entry.CentralDirectoryOffset = centralOffset;
        }

        return entries;
    }

    private static void ParseZip64Extra(ReadOnlySpan<byte> extra, bool needSize, bool needCompressed, bool needOffset,
        ref ulong size, ref ulong compressed, ref ulong offset)
    {
        var found = false;
        while (extra.Length >= 4)
        {
            var type = ReadUInt16(extra, 0);
            var length = ReadUInt16(extra, 2);
            extra = extra[4..];
            if (length > extra.Length)
            {
                throw Error("invalid_zip_archive");
            }

            var value = extra[..length];
            extra = extra[length..];
            if (type != 1)
            {
                continue;
            }

            found = true;
            var position = 0;
            if (needSize) { size = ReadZip64UInt64(value, ref position); }
            if (needCompressed) { compressed = ReadZip64UInt64(value, ref position); }
            if (needOffset) { offset = ReadZip64UInt64(value, ref position); }
        }

        if ((needSize || needCompressed || needOffset) && !found)
        {
            throw Error("invalid_zip_archive");
        }
    }

    private static ulong ReadZip64UInt64(ReadOnlySpan<byte> value, ref int position)
    {
        if (position + 8 > value.Length)
        {
            throw Error("invalid_zip_archive");
        }

        var result = BinaryPrimitives.ReadUInt64LittleEndian(value[position..]);
        position += 8;
        return result;
    }

    private static async Task ValidateLocalHeaderAsync(Stream stream, RawZipEntry entry, CancellationToken cancellationToken)
    {
        if (entry.LocalOffset > (ulong)(stream.Length - 30) || entry.LocalOffset >= entry.CentralDirectoryOffset)
        {
            throw Error("invalid_zip_archive");
        }

        stream.Position = (long)entry.LocalOffset;
        var header = new byte[30];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (ReadUInt32(header, 0) != 0x04034b50)
        {
            throw Error("zip_central_local_mismatch");
        }

        entry.LocalFlags = ReadUInt16(header, 6);
        entry.LocalMethod = ReadUInt16(header, 8);
        entry.LocalEncrypted = (entry.LocalFlags & ((1 << 0) | (1 << 6) | (1 << 13))) != 0;
        var nameLength = ReadUInt16(header, 26);
        var extraLength = ReadUInt16(header, 28);
        var variableLength = checked(nameLength + extraLength);
        var dataStart = entry.LocalOffset + 30UL + (ulong)variableLength;
        if (variableLength > 65_535 + 1_024 || dataStart > entry.CentralDirectoryOffset ||
            entry.CompressedSize > entry.CentralDirectoryOffset - dataStart)
        {
            throw Error("invalid_zip_archive");
        }

        var variable = new byte[variableLength];
        await stream.ReadExactlyAsync(variable, cancellationToken);
        entry.LocalNameBytes = variable.AsSpan(0, nameLength).ToArray();
        ulong localCompressed = ReadUInt32(header, 18);
        ulong localUncompressed = ReadUInt32(header, 22);
        ulong unusedOffset = 0;
        ParseZip64Extra(variable.AsSpan(nameLength, extraLength), localUncompressed == uint.MaxValue,
            localCompressed == uint.MaxValue, false, ref localUncompressed, ref localCompressed, ref unusedOffset);
        var descriptor = (entry.Flags & 8) != 0;
        if (!descriptor && (ReadUInt32(header, 14) != entry.Crc || localCompressed != entry.CompressedSize ||
                            localUncompressed != entry.UncompressedSize))
        {
            throw Error("zip_central_local_mismatch");
        }

        if (descriptor && (localCompressed != 0 && localCompressed != entry.CompressedSize ||
                           localUncompressed != 0 && localUncompressed != entry.UncompressedSize))
        {
            throw Error("zip_central_local_mismatch");
        }

        if (descriptor)
        {
            await ValidateDataDescriptorAsync(stream, entry, dataStart, cancellationToken);
        }
    }

    private static async Task ValidateDataDescriptorAsync(Stream stream, RawZipEntry entry, ulong dataStart,
        CancellationToken cancellationToken)
    {
        var descriptorOffset = dataStart + entry.CompressedSize;
        if (descriptorOffset > entry.CentralDirectoryOffset || entry.CentralDirectoryOffset - descriptorOffset < 12)
        {
            throw Error("invalid_zip_archive");
        }

        stream.Position = (long)descriptorOffset;
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var position = ReadUInt32(prefix, 0) == 0x08074b50 ? 4 : 0;
        var descriptorSize = position + 4 + (entry.Zip64 ? 16 : 8);
        if ((ulong)descriptorSize > entry.CentralDirectoryOffset - descriptorOffset)
        {
            throw Error("invalid_zip_archive");
        }

        var descriptor = new byte[descriptorSize];
        prefix.CopyTo(descriptor, 0);
        await stream.ReadExactlyAsync(descriptor.AsMemory(4), cancellationToken);
        if (ReadUInt32(descriptor, position) != entry.Crc)
        {
            throw Error("zip_central_local_mismatch");
        }

        position += 4;
        var compressed = entry.Zip64 ? ReadUInt64(descriptor, position) : ReadUInt32(descriptor, position);
        position += entry.Zip64 ? 8 : 4;
        var uncompressed = entry.Zip64 ? ReadUInt64(descriptor, position) : ReadUInt32(descriptor, position);
        if (compressed != entry.CompressedSize || uncompressed != entry.UncompressedSize)
        {
            throw Error("zip_central_local_mismatch");
        }
    }

    private static void ValidateName(string name, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(name) > maxBytes || name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(char.IsControl) || name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name) ||
            name is "." or ".." || !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("invalid_zip_entry_name");
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static ImportFileException Error(string code) => new(code, "ZIP CSV inspection failed.", 0);

    private sealed class RawZipEntry(string name, byte[] nameBytes, ushort flags, ushort method, uint crc,
        ulong compressedSize, ulong uncompressedSize, ulong localOffset, bool zip64)
    {
        public string Name { get; } = name;
        public byte[] NameBytes { get; } = nameBytes;
        public ushort Flags { get; } = flags;
        public ushort Method { get; } = method;
        public uint Crc { get; } = crc;
        public ulong CompressedSize { get; } = compressedSize;
        public ulong UncompressedSize { get; } = uncompressedSize;
        public ulong LocalOffset { get; } = localOffset;
        public bool Zip64 { get; } = zip64;
        public ulong CentralDirectoryOffset { get; set; }
        public bool Encrypted => (Flags & ((1 << 0) | (1 << 6) | (1 << 13))) != 0;
        public ushort LocalFlags { get; set; }
        public ushort LocalMethod { get; set; }
        public bool LocalEncrypted { get; set; }
        public byte[] LocalNameBytes { get; set; } = [];
    }

    private sealed class BoundedZipEntryStream(Stream inner, long maxEntryBytes, long remainingTotalBytes) : Stream
    {
        private long _length;
        private uint _crc = uint.MaxValue;
        public override long Length => _length;
        public uint Crc32 => ~_crc;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = Math.Min(buffer.Length, Math.Min(maxEntryBytes, remainingTotalBytes) - _length + 1);
            if (allowed <= 0)
            {
                throw Error("zip_uncompressed_limit_exceeded");
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, allowed)], cancellationToken);
            if (read > 0)
            {
                var span = buffer.Span;
                for (var i = 0; i < read; i++)
                {
                    _crc ^= span[i];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        _crc = (_crc >> 1) ^ (0xEDB88320U & (uint)-(int)(_crc & 1));
                    }
                }

                _length += read;
                if (_length > maxEntryBytes || _length > remainingTotalBytes)
                {
                    throw Error("zip_uncompressed_limit_exceeded");
                }
            }

            return read;
        }
    }
}
