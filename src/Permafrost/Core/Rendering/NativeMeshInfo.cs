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

/// <summary>
/// Native renderer diagnostics. 0.04.3 exposes both the object graph and the EBX import-table fallback,
/// because a resolved SpatialPrefabBlueprint is not itself a mesh: its Objects graph normally
/// leads through one or more mesh entity objects and external Mesh asset pointers before the
/// MeshSetResource RID is reached.
/// </summary>
public sealed record NativeMeshResolutionSummary(
    int PlacementCandidates,
    int BlueprintResolved,
    int DocumentsOpened,
    int ClassGuidMatches,
    int ClassGuidMisses,
    int TargetObjectsVisited,
    int MeshEntityObjects,
    int ExternalRefsSeen,
    int ExternalRefsResolved,
    int ImportFallbackRefs,
    int MeshPointersFollowed,
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
        $"{PlacementCandidates:N0} candidates • {BlueprintResolved:N0} blueprints • {DocumentsOpened:N0} EBXs • " +
        $"{TargetObjectsVisited:N0} objects • {MeshEntityObjects:N0} mesh entities • {ExternalRefsResolved:N0}/{ExternalRefsSeen:N0} refs • " +
        $"{ImportFallbackRefs:N0} import fallbacks • {MeshAssetsResolved:N0} mesh assets • {MeshSetsResolved:N0} MeshSet RES • " +
        $"{ChunksLinked:N0} chunks • {GeometryResolved:N0} native meshes • {Failed:N0} unresolved";

    public string GuidDetails =>
        $"ClassGuid matches {ClassGuidMatches:N0} • misses {ClassGuidMisses:N0}";

    public override string ToString() =>
        $"{BoundsResolved:N0} MeshSets • {GeometryResolved:N0} native meshes • {TrianglesDecoded:N0} tris • {Failed:N0} unresolved";
}
