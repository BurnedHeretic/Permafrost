namespace Permafrost.Core.Assets;

public sealed class GameAssetIndex
{
    private readonly Dictionary<string, GameAssetEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, GameAssetEntry> _byGuid = new();
    private readonly Dictionary<ulong, GameAssetEntry> _resById = new();
    private readonly Dictionary<Guid, GameAssetEntry> _chunksById = new();
    private readonly Dictionary<uint, List<GameAssetEntry>> _byBundle = new();
    private readonly Dictionary<string, HashSet<uint>> _bundleHashesByKindAndName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameAssetEntry> _uniqueByKindAndName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GameAssetEntry> _entries = new();

    public IReadOnlyList<GameAssetEntry> Entries => _entries;
    public IEnumerable<GameAssetEntry> UniqueEntries => _uniqueByKindAndName.Values;
    public int EbxCount => _entries.Count(x => x.Kind == GameAssetKind.Ebx);
    public int ResCount => _entries.Count(x => x.Kind == GameAssetKind.Res);
    public int ChunkCount => _entries.Count(x => x.Kind == GameAssetKind.Chunk);
    public int UniqueEbxCount => _uniqueByKindAndName.Values.Count(x => x.Kind == GameAssetKind.Ebx);
    public int UniqueResCount => _uniqueByKindAndName.Values.Count(x => x.Kind == GameAssetKind.Res);
    public int UniqueChunkCount => _chunksById.Count;
    public int GuidCount => _byGuid.Count;
    public int ResIdCount => _resById.Count;

    public void Add(GameAssetEntry entry)
    {
        var normalized = Normalize(entry.Name);
        if (entry.Kind == GameAssetKind.Ebx || !_byName.ContainsKey(normalized))
            _byName[normalized] = entry;

        var kindName = $"{(int)entry.Kind}:{normalized}";
        _uniqueByKindAndName[kindName] = entry;
        _entries.Add(entry);

        if (!_bundleHashesByKindAndName.TryGetValue(kindName, out var hashes))
        {
            hashes = new HashSet<uint>();
            _bundleHashesByKindAndName[kindName] = hashes;
        }
        hashes.Add(entry.BundleHash);

        if (entry.Kind == GameAssetKind.Res && entry.ResId != 0)
            _resById[entry.ResId] = entry;
        if (entry.Kind == GameAssetKind.Chunk && entry.ChunkId is { } chunkId && chunkId != Guid.Empty)
            _chunksById[chunkId] = entry;

        if (!_byBundle.TryGetValue(entry.BundleHash, out var bundleEntries))
        {
            bundleEntries = new List<GameAssetEntry>();
            _byBundle[entry.BundleHash] = bundleEntries;
        }
        bundleEntries.Add(entry);

        if (entry.FileGuid is { } guid && guid != Guid.Empty)
            _byGuid[guid] = entry;
    }

    public bool TryGetByName(string name, out GameAssetEntry entry) =>
        _byName.TryGetValue(Normalize(name), out entry!);

    public bool TryGetByGuid(Guid guid, out GameAssetEntry entry) => _byGuid.TryGetValue(guid, out entry!);
    public bool TryGetResById(ulong resId, out GameAssetEntry entry) => _resById.TryGetValue(resId, out entry!);
    public bool TryGetChunkById(Guid chunkId, out GameAssetEntry entry) => _chunksById.TryGetValue(chunkId, out entry!);

    public IReadOnlyCollection<uint> GetBundleHashesForAsset(GameAssetEntry entry) =>
        GetBundleHashesForAsset(entry.Name, entry.Kind);

    public IReadOnlyCollection<uint> GetBundleHashesForAsset(string name, GameAssetKind kind = GameAssetKind.Ebx)
    {
        var key = $"{(int)kind}:{Normalize(name)}";
        return _bundleHashesByKindAndName.TryGetValue(key, out var hashes)
            ? hashes
            : Array.Empty<uint>();
    }

    public IEnumerable<GameAssetEntry> GetBundleAssets(uint bundleHash, GameAssetKind? kind = null)
    {
        if (!_byBundle.TryGetValue(bundleHash, out var list))
            return Enumerable.Empty<GameAssetEntry>();
        return kind.HasValue ? list.Where(x => x.Kind == kind.Value) : list;
    }

    public IEnumerable<GameAssetEntry> GetUniqueEbxForBundles(IEnumerable<uint> bundleHashes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in bundleHashes.Distinct())
        {
            if (!_byBundle.TryGetValue(hash, out var list)) continue;
            foreach (var entry in list)
            {
                if (entry.Kind != GameAssetKind.Ebx) continue;
                if (seen.Add(entry.NormalizedName))
                    yield return entry;
            }
        }
    }

    public void RegisterGuid(GameAssetEntry entry, Guid guid)
    {
        if (guid == Guid.Empty) return;
        entry.FileGuid = guid;
        _byGuid[guid] = entry;
    }

    public IEnumerable<GameAssetEntry> Search(string? text, GameAssetKind? kind = null)
    {
        IEnumerable<GameAssetEntry> query = _byName.Values;
        if (kind.HasValue) query = query.Where(x => x.Kind == kind.Value);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var needle = text.Trim();
            query = query.Where(x => x.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        return query.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<GameAssetEntry> SearchUnique(string? text, GameAssetKind? kind = null)
    {
        IEnumerable<GameAssetEntry> query = _uniqueByKindAndName.Values;
        if (kind.HasValue) query = query.Where(x => x.Kind == kind.Value);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var needle = text.Trim();
            query = query.Where(x => x.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        return query.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<GameAssetEntry> LevelAssets =>
        _byName.Values.Where(x =>
                x.Kind == GameAssetKind.Ebx &&
                (x.NormalizedName.StartsWith("levels/", StringComparison.OrdinalIgnoreCase) ||
                 x.NormalizedName.Contains("/levels/", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<GameAssetEntry> SpaceLevelAssets =>
        LevelAssets.Where(x =>
            x.NormalizedName.StartsWith("levels/space/", StringComparison.OrdinalIgnoreCase) ||
            x.NormalizedName.Contains("/levels/space/", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}
