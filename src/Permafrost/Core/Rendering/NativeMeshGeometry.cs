using System.Numerics;

namespace Permafrost.Core.Rendering;

/// <summary>
/// CPU-side triangle preview geometry decoded from a Battlefront II MeshSet LOD.
/// Positions remain in the MeshSet's local Frostbite space; SceneViewport applies the
/// placement LinearTransform exactly as it already does for bounds/proxy rendering.
/// </summary>
public sealed class NativeMeshGeometry
{
    public required Vector3[] Positions { get; init; }
    public required int[] TriangleIndices { get; init; }
    public int LodIndex { get; init; }
    public int SectionsDecoded { get; init; }
    public int SectionsSkipped { get; init; }
    public Guid? ChunkId { get; init; }
    public bool UsesInlineData { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => TriangleIndices.Length / 3;
    public string DataSource => UsesInlineData ? "inline RES" : ChunkId?.ToString() ?? "unknown";
}
