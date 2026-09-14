namespace Permafrost.Core.Frostbite;

public sealed record ManifestFileRef(uint FileRef, uint Offset, ulong Size, bool IsChunk = false)
{
    public override string ToString() => $"0x{FileRef:X8} @ 0x{Offset:X8} ({Size:N0} bytes)";
}

public sealed class ManifestBundleDescriptor
{
    public uint Hash { get; init; }
    public uint StartIndex { get; init; }
    public uint Count { get; init; }
    public uint Unknown1 { get; init; }
    public uint Unknown2 { get; init; }
    public List<ManifestFileRef> Files { get; } = new();
}

public sealed class FrostbiteManifest
{
    public List<ManifestFileRef> Files { get; } = new();
    public List<ManifestBundleDescriptor> Bundles { get; } = new();
    public int ChunkCount { get; internal set; }
    public bool UsesBundleAggregationMap { get; internal set; }
    public string BundleAggregationSource { get; internal set; } = "Manifest StartIndex/Count";
    public string? BundleAggregationError { get; internal set; }
}

public sealed class FrostbiteInstallLayout
{
    public required string InstallRoot { get; init; }
    public required DbValue LayoutRoot { get; init; }
    public List<string> Catalogs { get; } = new();
    public Dictionary<string, FrostbiteCatalog> CatalogMetadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public required FrostbiteManifest Manifest { get; init; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _casPathCache = new();

    public string ResolveCasPath(uint manifestRef)
    {
        if (_casPathCache.TryGetValue(manifestRef, out var cached) && File.Exists(cached))
            return cached;

        var rawCatalog = manifestRef >> 12;
        if (rawCatalog == 0)
            throw new InvalidDataException($"Manifest file ref 0x{manifestRef:X8} has an invalid zero catalog index.");

        var catalogIndex = checked((int)(rawCatalog - 1));
        var patch = (manifestRef & 0x100) != 0;
        var casIndex = (manifestRef & 0xFF) + 1;
        if (catalogIndex < 0 || catalogIndex >= Catalogs.Count)
            throw new InvalidDataException(
                $"Manifest file ref 0x{manifestRef:X8} uses catalog index {catalogIndex}, " +
                $"but only {Catalogs.Count} catalog(s) were discovered.");

        var catalog = Catalogs[catalogIndex].TrimEnd('\0').Replace('/', Path.DirectorySeparatorChar);
        var fileName = $"cas_{casIndex:00}.cas";

        // Normal Frostbite location, honoring the manifest patch bit first.
        var preferredRoot = patch ? "Patch" : "Data";
        var preferred = Path.Combine(InstallRoot, preferredRoot, catalog, fileName);
        if (File.Exists(preferred))
        {
            _casPathCache[manifestRef] = preferred;
            return preferred;
        }

        // A few BF2/EA App layouts retain a valid file in the opposite tree.
        var alternateRoot = patch ? "Data" : "Patch";
        var alternate = Path.Combine(InstallRoot, alternateRoot, catalog, fileName);
        if (File.Exists(alternate))
        {
            _casPathCache[manifestRef] = alternate;
            return alternate;
        }

        // Last-resort compatibility lookup. Keep this narrow: search only Win32 and
        // prefer a path whose suffix matches the catalog from layout.toc.
        foreach (var rootName in new[] { preferredRoot, alternateRoot })
        {
            var win32 = Path.Combine(InstallRoot, rootName, "Win32");
            if (!Directory.Exists(win32))
                continue;

            var matches = Directory.EnumerateFiles(win32, fileName, SearchOption.AllDirectories).ToArray();
            var expectedSuffix = catalog.TrimStart(Path.DirectorySeparatorChar);
            var suffixMatch = matches.FirstOrDefault(path =>
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                return dir.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase);
            });
            if (suffixMatch != null)
            {
                _casPathCache[manifestRef] = suffixMatch;
                return suffixMatch;
            }
            if (matches.Length == 1)
            {
                _casPathCache[manifestRef] = matches[0];
                return matches[0];
            }
        }

        throw new FileNotFoundException(
            $"Could not resolve CAS file for manifest ref 0x{manifestRef:X8}. " +
            $"Catalog={Catalogs[catalogIndex]}, CAS={casIndex}, Patch={patch}.\n" +
            $"Checked: {preferred}\nAlternate: {alternate}");
    }

    public async Task<byte[]> ReadRawSegmentAsync(ManifestFileRef file, int? maximumBytes = null, CancellationToken cancellationToken = default)
    {
        var path = ResolveCasPath(file.FileRef);
        var count64 = maximumBytes.HasValue ? Math.Min(file.Size, (ulong)maximumBytes.Value) : file.Size;
        if (count64 > int.MaxValue) throw new InvalidDataException("Manifest segment is too large for the current reader.");
        var count = checked((int)count64);
        var buffer = new byte[count];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, useAsync: true);
        stream.Position = file.Offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (n == 0) throw new EndOfStreamException($"CAS file ended while reading {file}.");
            read += n;
        }
        return buffer;
    }

    public async Task<byte[]> ReadDecompressedAsync(ManifestFileRef file, int? neededSize = null, CancellationToken cancellationToken = default)
    {
        var raw = await ReadRawSegmentAsync(file, null, cancellationToken);
        return CasBlockCodec.Decompress(raw, neededSize);
    }
}
