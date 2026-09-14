using System.Buffers.Binary;
using System.Net.Http;
using ZstdSharp;

namespace Permafrost.Core.Frostbite;

/// <summary>
/// Battlefront II uses a bundle aggregation mapping that cannot be reconstructed from the
/// public manifest's StartIndex/Count fields alone. This loader caches the compatibility
/// table locally and validates it against the manifest bundle hashes before it is trusted.
/// The table is data only; it is never executed.
/// </summary>
public static class BundleAggregationMap
{
    private const uint Magic = 0x77778888;
    private const string DefaultUrl =
        "https://raw.githubusercontent.com/ArmchairDevelopers/Glacier/main/crates/glacier_fs/src/fb/VanillaBundleAggregation.kb";

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Permafrost",
        "Cache",
        "VanillaBundleAggregation.kb");

    private static string LegacyCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrostbiteLevelEditor",
        "Cache",
        "VanillaBundleAggregation.kb");

    public static async Task<BundleAggregationData> LoadOrAcquireAsync(
        IReadOnlyList<uint> manifestBundleHashes,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);

        // One-time migration from the pre-Permafrost name so existing testers do not
        // need to download the same compatibility file again.
        if (!File.Exists(CachePath) && File.Exists(LegacyCachePath))
        {
            try
            {
                File.Copy(LegacyCachePath, CachePath, overwrite: false);
                progress?.Report("Migrated legacy bundle aggregation cache into Permafrost.");
            }
            catch
            {
                // Harmless: the normal acquisition path below can still download it.
            }
        }

        if (File.Exists(CachePath))
        {
            progress?.Report("Loading cached BF2 bundle aggregation map...");
            try
            {
                var cached = await File.ReadAllBytesAsync(CachePath, cancellationToken);
                return ParseAndValidate(cached, manifestBundleHashes);
            }
            catch (Exception ex)
            {
                progress?.Report($"Cached aggregation map was invalid ({ex.Message}); refreshing it...");
                try
                {
                    var badPath = CachePath + ".invalid";
                    if (File.Exists(badPath)) File.Delete(badPath);
                    File.Move(CachePath, badPath);
                }
                catch { }
            }
        }

        progress?.Report("Downloading BF2 bundle aggregation compatibility map (first run only)...");
        byte[] bytes;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await client.GetAsync(DefaultUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "Battlefront II requires a bundle aggregation compatibility map before the installed bundle headers can be indexed. " +
                "The editor could not download that data automatically.\n\n" +
                $"Cache path: {CachePath}\nSource: {DefaultUrl}\n\n" +
                $"Download error: {ex.Message}", ex);
        }

        // Persist the raw download before validation. This is intentional: if a future
        // compatibility-format change breaks parsing, the user still has the exact bytes
        // that were downloaded and can attach them for diagnostics.
        await File.WriteAllBytesAsync(CachePath, bytes, cancellationToken);

        try
        {
            return ParseAndValidate(bytes, manifestBundleHashes);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Downloaded BF2 bundle aggregation map could not be parsed. " +
                $"The raw file has been retained at: {CachePath}\n" +
                $"Downloaded={bytes.Length:N0} bytes; {DescribeHeader(bytes)}\n" +
                $"Parser error: {ex.Message}", ex);
        }
    }

    public static BundleAggregationData ParseAndValidate(
        ReadOnlySpan<byte> packed,
        IReadOnlyList<uint> manifestBundleHashes)
    {
        if (packed.Length < 8)
            throw new InvalidDataException("Bundle aggregation map is truncated.");

        var originalSize = BinaryPrimitives.ReadInt32BigEndian(packed[..4]);
        if (originalSize <= 0 || originalSize > 512 * 1024 * 1024)
            throw new InvalidDataException($"Bundle aggregation map declares an invalid decoded size: {originalSize:N0} bytes. {DescribeHeader(packed)}");

        // Standard Zstandard frames begin with 28 B5 2F FD in byte order.
        // Do not hard-fail if it differs because Glacier's async decoder may accept
        // variants that another library does not, but include the bytes in any error.
        var payloadMagic = packed.Length >= 8
            ? Convert.ToHexString(packed.Slice(4, 4))
            : "<truncated>";

        // The Glacier compatibility blob is a fixed-size 12 MiB container. The actual
        // Zstandard frame ends earlier and the rest of the container is zero padding.
        // Passing the padding to ZstdSharp makes it try to interpret the zeros as another
        // frame, producing "Unknown frame descriptor" / "Src size is incorrect".
        // Parse the Zstd frame boundaries first and feed only the real frame to the decoder.
        var payload = packed[4..];
        var frameLength = FindZstdFrameLength(payload);
        var paddingLength = payload.Length - frameLength;
        if (paddingLength < 0)
            throw new InvalidDataException("Bundle aggregation Zstd frame extends beyond the packed container.");

        for (var i = frameLength; i < payload.Length; i++)
        {
            if (payload[i] != 0)
                throw new InvalidDataException(
                    $"Bundle aggregation container has non-zero bytes after the Zstd frame at payload offset 0x{i:X}.");
        }

        var decoded = new byte[originalSize];
        try
        {
            using var source = new MemoryStream(payload[..frameLength].ToArray(), writable: false);
            using var zstd = new DecompressionStream(source, checkEndOfStream: true, leaveOpen: false);

            var written = 0;
            while (written < decoded.Length)
            {
                var read = zstd.Read(decoded, written, decoded.Length - written);
                if (read == 0)
                    break;
                written += read;
            }

            if (written != decoded.Length)
                throw new InvalidDataException(
                    $"Bundle aggregation map decoded to {written:N0} bytes; expected {decoded.Length:N0}. " +
                    $"Frame={frameLength:N0} bytes, padding={paddingLength:N0} bytes.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"Bundle aggregation Zstd decode failed. DeclaredDecoded={originalSize:N0}; " +
                $"Packed={packed.Length:N0}; frame={frameLength:N0}; padding={paddingLength:N0}; " +
                $"payload magic={payloadMagic}; {DescribeHeader(packed)}; decoder={ex.Message}", ex);
        }

        using var ms = new MemoryStream(decoded, writable: false);
        using var reader = new BinaryReader(ms);

        var magic = reader.ReadUInt32BE();
        if (magic != Magic)
            throw new InvalidDataException($"Bundle aggregation map has invalid magic 0x{magic:X8}.");

        var catalogCount = checked((int)reader.ReadUInt32BE());
        if (catalogCount < 0 || catalogCount > 4096)
            throw new InvalidDataException($"Bundle aggregation map has invalid catalog count {catalogCount}.");

        var catalogs = new List<string>(catalogCount);
        for (var i = 0; i < catalogCount; i++)
            catalogs.Add(reader.ReadNullTerminatedUtf8().TrimEnd('\0'));

        var bundleCount = checked((int)reader.ReadUInt32BE());
        if (bundleCount != manifestBundleHashes.Count)
            throw new InvalidDataException(
                $"Bundle aggregation map contains {bundleCount:N0} bundles but this Battlefront II manifest contains " +
                $"{manifestBundleHashes.Count:N0}. The compatibility data does not match this installed game build.");

        var bundles = new List<BundleAggregationBundle>(bundleCount);
        for (var i = 0; i < bundleCount; i++)
        {
            EnsureRemaining(reader, 20);
            var hash = reader.ReadUInt32BE();
            var ebxCount = reader.ReadUInt32BE();
            var resCount = reader.ReadUInt32BE();
            var chunkCount = reader.ReadUInt32BE();
            var fileCount = checked((int)reader.ReadUInt32BE());

            var expectedHash = manifestBundleHashes[i];
            if (hash != expectedHash)
                throw new InvalidDataException(
                    $"Bundle aggregation map diverged at bundle {i:N0}: table=0x{hash:X8}, manifest=0x{expectedHash:X8}.");
            if (fileCount < 0 || fileCount > 1_000_000)
                throw new InvalidDataException($"Bundle 0x{hash:X8} has invalid aggregation file count {fileCount}.");

            var files = new List<ManifestFileRef>(fileCount);
            for (var f = 0; f < fileCount; f++)
            {
                EnsureRemaining(reader, 24);
                var fileRef64 = reader.ReadInt64BE();
                var offset64 = reader.ReadInt64BE();
                var size64 = reader.ReadInt64BE();
                if (offset64 < 0 || offset64 > uint.MaxValue || size64 < 0)
                    throw new InvalidDataException($"Bundle 0x{hash:X8} contains an out-of-range manifest file mapping.");
                files.Add(new ManifestFileRef(unchecked((uint)fileRef64), (uint)offset64, (ulong)size64));
            }

            // The bundle header itself is file[0], followed by EBX, RES and chunk payload files.
            // Keep the counts for diagnostics and future chunk support.
            bundles.Add(new BundleAggregationBundle(hash, ebxCount, resCount, chunkCount, files));
        }

        return new BundleAggregationData(catalogs, bundles);
    }


    private static int FindZstdFrameLength(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || !frame[..4].SequenceEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }))
            throw new InvalidDataException(
                $"Bundle aggregation payload does not begin with a standard Zstd frame (magic={Convert.ToHexString(frame[..Math.Min(4, frame.Length)])}).");

        var position = 4;
        var descriptor = frame[position++];
        var frameContentSizeFlag = (descriptor >> 6) & 0x03;
        var singleSegment = (descriptor & 0x20) != 0;
        var checksum = (descriptor & 0x04) != 0;
        var dictionaryIdFlag = descriptor & 0x03;

        // Bit 3 is reserved by the Zstandard frame format.
        if ((descriptor & 0x08) != 0)
            throw new InvalidDataException($"Unsupported Zstd frame descriptor 0x{descriptor:X2}.");

        if (!singleSegment)
            RequireFrameBytes(frame, position, 1, "window descriptor");
        if (!singleSegment)
            position += 1;

        var dictionaryIdSize = dictionaryIdFlag switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 4,
            _ => 0
        };
        RequireFrameBytes(frame, position, dictionaryIdSize, "dictionary id");
        position += dictionaryIdSize;

        var frameContentSizeBytes = frameContentSizeFlag switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 0
        };
        RequireFrameBytes(frame, position, frameContentSizeBytes, "frame content size");
        position += frameContentSizeBytes;

        while (true)
        {
            RequireFrameBytes(frame, position, 3, "block header");
            var header = frame[position] | (frame[position + 1] << 8) | (frame[position + 2] << 16);
            position += 3;

            var lastBlock = (header & 1) != 0;
            var blockType = (header >> 1) & 0x03;
            var blockSize = header >> 3;
            var storedSize = blockType switch
            {
                0 => blockSize, // raw
                1 => 1,         // RLE stores one repeated byte
                2 => blockSize, // compressed
                _ => throw new InvalidDataException("Bundle aggregation Zstd frame contains a reserved block type.")
            };

            RequireFrameBytes(frame, position, storedSize, "block payload");
            position += storedSize;
            if (lastBlock)
                break;
        }

        if (checksum)
        {
            RequireFrameBytes(frame, position, 4, "content checksum");
            position += 4;
        }

        return position;
    }

    private static void RequireFrameBytes(ReadOnlySpan<byte> frame, int position, int count, string section)
    {
        if (count < 0 || position < 0 || position > frame.Length - count)
            throw new EndOfStreamException($"Bundle aggregation Zstd frame ended while reading {section}.");
    }

    private static string DescribeHeader(ReadOnlySpan<byte> bytes)
    {
        var count = Math.Min(bytes.Length, 32);
        var first = count == 0 ? "<empty>" : Convert.ToHexString(bytes[..count]);
        var afterPrefix = bytes.Length > 4
            ? Convert.ToHexString(bytes.Slice(4, Math.Min(16, bytes.Length - 4)))
            : "<none>";
        return $"first{count}=0x{first}; afterPrefix=0x{afterPrefix}";
    }

    private static void EnsureRemaining(BinaryReader reader, int count)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < count)
            throw new EndOfStreamException("Bundle aggregation map ended unexpectedly.");
    }
}

public sealed record BundleAggregationBundle(
    uint Hash,
    uint EbxCount,
    uint ResCount,
    uint ChunkCount,
    IReadOnlyList<ManifestFileRef> Files);

public sealed record BundleAggregationData(
    IReadOnlyList<string> Catalogs,
    IReadOnlyList<BundleAggregationBundle> Bundles);
