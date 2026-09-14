using System.Text;
using Permafrost.Core.Assets;

namespace Permafrost.Core.Frostbite;

public static class BundleReader
{
    private const uint BundleMagic = 0x9D798ED5;

    public static async Task<IReadOnlyList<GameAssetEntry>> ReadAssetsAsync(
        FrostbiteInstallLayout layout,
        ManifestBundleDescriptor bundle,
        CancellationToken cancellationToken = default)
    {
        if (bundle.Files.Count == 0)
            throw new InvalidDataException($"Bundle 0x{bundle.Hash:X8} has no aggregation file mappings.");

        var raw = await layout.ReadRawSegmentAsync(bundle.Files[0], null, cancellationToken);

        // A few Frostbite variants can expose a header directly. BF2 normally stores the
        // header as a CAS block stream, but retaining this path costs almost nothing and
        // makes the reader more tolerant.
        if (TryParse(raw, bundle, out var directEntries, out _))
            return directEntries;

        byte[] decoded;
        try
        {
            decoded = CasBlockCodec.Decompress(raw);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Bundle 0x{bundle.Hash:X8} CAS header decompression failed. " +
                $"Mapping={bundle.Files[0]}; raw={DescribeBytes(raw)}; {ex.Message}", ex);
        }

        if (TryParse(decoded, bundle, out var entries, out var parseError))
            return entries;

        throw new InvalidDataException(
            $"Bundle 0x{bundle.Hash:X8} decompressed, but its bundle header could not be parsed. " +
            $"Mapping={bundle.Files[0]}; decoded={DescribeBytes(decoded)}; parser={parseError}");
    }

    private static bool TryParse(
        byte[] data,
        ManifestBundleDescriptor bundle,
        out IReadOnlyList<GameAssetEntry> entries,
        out string error)
    {
        entries = Array.Empty<GameAssetEntry>();
        error = string.Empty;

        try
        {
            if (data.Length < 36)
            {
                error = $"header is only {data.Length:N0} bytes";
                return false;
            }

            using var ms = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

            // IMPORTANT: Frostbite bundle metadata is BIG-ENDIAN. Tokio's
            // AsyncReadExt::read_u32 used by Glacier is BE by default. v0.02.9 used
            // BinaryReader.ReadUInt32 (LE), so every valid 0x9D798ED5 magic was read as
            // 0xD58E799D and all 4,777 BF2 bundle headers were rejected.
            _ = reader.ReadUInt32BE(); // data offset + 4 in Frostbite; not needed for indexing
            var magic = reader.ReadUInt32BE();
            if (magic != BundleMagic)
            {
                error = $"magic=0x{magic:X8}, expected=0x{BundleMagic:X8}";
                return false;
            }

            var totalCount = reader.ReadUInt32BE();
            var ebxCount = reader.ReadUInt32BE();
            var resCount = reader.ReadUInt32BE();
            var chunkCount = reader.ReadUInt32BE();
            if (totalCount != ebxCount + resCount + chunkCount)
            {
                error = $"asset counts are inconsistent: total={totalCount}, EBX={ebxCount}, RES={resCount}, chunks={chunkCount}";
                return false;
            }

            var stringsOffset = reader.ReadUInt32BE();
            _ = reader.ReadUInt32BE(); // meta offset
            _ = reader.ReadUInt32BE(); // data size

            var shaBytes = checked((long)totalCount * 20L);
            if (reader.BaseStream.Position + shaBytes > reader.BaseStream.Length)
            {
                error = "SHA1 table extends beyond the decompressed bundle header";
                return false;
            }
            reader.BaseStream.Position += shaBytes;

            var requiredFiles = 1UL + totalCount;
            if ((ulong)bundle.Files.Count < requiredFiles)
            {
                error = $"aggregation supplies {bundle.Files.Count} file mapping(s), but the header requires at least {requiredFiles}";
                return false;
            }

            var result = new List<GameAssetEntry>(checked((int)(ebxCount + resCount)));
            var resEntries = new List<GameAssetEntry>(checked((int)resCount));
            var fileIndex = 1;

            for (var i = 0U; i < ebxCount; i++)
            {
                if (reader.BaseStream.Position + 8 > reader.BaseStream.Length)
                {
                    error = $"EBX record {i} extends beyond the decompressed bundle header";
                    return false;
                }

                var nameOffset = reader.ReadUInt32BE();
                var originalSize = reader.ReadUInt32BE();
                var name = ReadBundleString(reader, stringsOffset, nameOffset);
                result.Add(new GameAssetEntry
                {
                    Name = name,
                    Kind = GameAssetKind.Ebx,
                    OriginalSize = originalSize,
                    Storage = bundle.Files[fileIndex++],
                    BundleHash = bundle.Hash
                });
            }

            for (var i = 0U; i < resCount; i++)
            {
                if (reader.BaseStream.Position + 8 > reader.BaseStream.Length)
                {
                    error = $"RES record {i} extends beyond the decompressed bundle header";
                    return false;
                }

                var nameOffset = reader.ReadUInt32BE();
                var originalSize = reader.ReadUInt32BE();
                var name = ReadBundleString(reader, stringsOffset, nameOffset);
                var entry = new GameAssetEntry
                {
                    Name = name,
                    Kind = GameAssetKind.Res,
                    OriginalSize = originalSize,
                    Storage = bundle.Files[fileIndex++],
                    BundleHash = bundle.Hash
                };
                result.Add(entry);
                resEntries.Add(entry);
            }

            // RES metadata is also big-endian, matching Frostbite/Glacier.
            var resTypes = new uint[resEntries.Count];
            for (var i = 0; i < resTypes.Length; i++)
            {
                if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
                {
                    error = "RES type table extends beyond the decompressed bundle header";
                    return false;
                }
                resTypes[i] = reader.ReadUInt32BE();
            }

            var metas = new byte[resEntries.Count][];
            for (var i = 0; i < metas.Length; i++)
            {
                if (reader.BaseStream.Position + 16 > reader.BaseStream.Length)
                {
                    error = "RES metadata table extends beyond the decompressed bundle header";
                    return false;
                }
                metas[i] = reader.ReadBytes(16);
            }

            var ids = new ulong[resEntries.Count];
            for (var i = 0; i < ids.Length; i++)
            {
                if (reader.BaseStream.Position + 8 > reader.BaseStream.Length)
                {
                    error = "RES id table extends beyond the decompressed bundle header";
                    return false;
                }
                ids[i] = reader.ReadUInt64BE();
            }

            for (var i = 0; i < resEntries.Count; i++)
            {
                var old = resEntries[i];
                var enriched = new GameAssetEntry
                {
                    Name = old.Name,
                    Kind = old.Kind,
                    OriginalSize = old.OriginalSize,
                    Storage = old.Storage,
                    BundleHash = old.BundleHash,
                    ResType = resTypes[i],
                    ResMeta = metas[i],
                    ResId = ids[i]
                };
                var index = result.IndexOf(old);
                result[index] = enriched;
            }

            entries = result;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            entries = Array.Empty<GameAssetEntry>();
            return false;
        }
    }

    private static string ReadBundleString(BinaryReader reader, uint stringsOffset, uint nameOffset)
    {
        var old = reader.BaseStream.Position;
        var position = checked(4L + stringsOffset + nameOffset);
        if (position < 0 || position >= reader.BaseStream.Length)
            throw new InvalidDataException(
                $"Bundle string offset is outside the bundle header (strings=0x{stringsOffset:X}, name=0x{nameOffset:X}, len=0x{reader.BaseStream.Length:X}).");

        reader.BaseStream.Position = position;
        var value = reader.ReadNullTerminatedUtf8();
        reader.BaseStream.Position = old;
        return value;
    }

    private static string DescribeBytes(byte[] data)
    {
        var count = Math.Min(data.Length, 24);
        var head = count == 0 ? "<empty>" : Convert.ToHexString(data.AsSpan(0, count));
        return $"{data.Length:N0} bytes, first{count}=0x{head}";
    }
}
