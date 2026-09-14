using Permafrost.Core.Frostbite;

namespace Permafrost.Core.Assets;

public enum GameAssetKind
{
    Ebx,
    Res,
    Chunk
}

public sealed class GameAssetEntry
{
    public required string Name { get; init; }
    public required GameAssetKind Kind { get; init; }
    public uint OriginalSize { get; init; }
    public required ManifestFileRef Storage { get; init; }
    public uint BundleHash { get; init; }
    public uint ResType { get; init; }
    public ulong ResId { get; init; }
    public byte[]? ResMeta { get; init; }
    public Guid? FileGuid { get; set; }

    public string NormalizedName => Name.Replace('\\', '/').TrimStart('/');
    public string ShortName
    {
        get
        {
            var n = NormalizedName.TrimEnd('/');
            var i = n.LastIndexOf('/');
            return i >= 0 ? n[(i + 1)..] : n;
        }
    }

    public override string ToString() => Name;
}
