using System.Numerics;
using Permafrost.Core.Assets;

namespace Permafrost.Core.Rendering;

public sealed class NativeMeshInfo
{
    public GameAssetEntry? MeshAsset { get; init; }
    public GameAssetEntry? MeshSetResource { get; init; }
    public ulong MeshSetResId { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public bool BoundsValid { get; init; }
    public Guid? LodChunkId { get; init; }
    public int LodChunkBytes { get; init; }
    public int ParsedLodCount { get; init; }
    public NativeMeshGeometry? Geometry { get; init; }
    public string Status { get; init; } = string.Empty;

    public bool HasBounds =>
        BoundsValid &&
        IsFinite(BoundsMin.X) && IsFinite(BoundsMin.Y) && IsFinite(BoundsMin.Z) &&
        IsFinite(BoundsMax.X) && IsFinite(BoundsMax.Y) && IsFinite(BoundsMax.Z) &&
        BoundsMax.X >= BoundsMin.X && BoundsMax.Y >= BoundsMin.Y && BoundsMax.Z >= BoundsMin.Z;

    public bool HasGeometry => Geometry is { VertexCount: > 0, TriangleCount: > 0 };

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

public sealed record NativeMeshResolutionSummary(
    int PlacementCandidates,
    int BlueprintResolved,
    int MeshAssetsResolved,
    int MeshSetsResolved,
    int BoundsResolved,
    int ChunksLinked,
    int GeometryResolved,
    int InlineGeometryResolved,
    long TrianglesDecoded,
    int Failed)
{
    public string ChainDetails =>
        $"{PlacementCandidates:N0} candidates • {BlueprintResolved:N0} blueprints • {MeshAssetsResolved:N0} mesh assets • " +
        $"{MeshSetsResolved:N0} MeshSet RES • {ChunksLinked:N0} chunks • {GeometryResolved:N0} native meshes • {Failed:N0} unresolved";

    public override string ToString() =>
        $"{BoundsResolved:N0} MeshSets • {GeometryResolved:N0} native meshes • {TrianglesDecoded:N0} tris • {Failed:N0} unresolved";
}
