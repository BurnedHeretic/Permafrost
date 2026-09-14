namespace Permafrost.Core.Assets;

public sealed class GameAssetIndex
{
    private readonly Dictionary<string, GameAssetEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, GameAssetEntry> _byGuid = new();
    private readonly List<GameAssetEntry> _entries = new();

    public IReadOnlyList<GameAssetEntry> Entries => _entries;
    public int EbxCount => _entries.Count(x => x.Kind == GameAssetKind.Ebx);
    public int ResCount => _entries.Count(x => x.Kind == GameAssetKind.Res);
    public int GuidCount => _byGuid.Count;

    public void Add(GameAssetEntry entry)
    {
        // Patch/bundle duplicates are common. The later manifest entry wins for name lookup,
        // while the complete list is retained for diagnostics.
        _byName[Normalize(entry.Name)] = entry;
        _entries.Add(entry);
        if (entry.FileGuid is { } guid && guid != Guid.Empty)
            _byGuid[guid] = entry;
    }

    public bool TryGetByName(string name, out GameAssetEntry entry) =>
        _byName.TryGetValue(Normalize(name), out entry!);

    public bool TryGetByGuid(Guid guid, out GameAssetEntry entry) => _byGuid.TryGetValue(guid, out entry!);

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

    public IEnumerable<GameAssetEntry> LevelAssets =>
        _byName.Values.Where(x => x.Kind == GameAssetKind.Ebx && x.NormalizedName.StartsWith("levels/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
}
