using System.Buffers.Binary;
using System.Numerics;
using Permafrost.Core.Frostbite;

namespace Permafrost.Core.Rendering;

/// <summary>
/// Minimal SWBF2 MeshSet resource probe used by the v0.03 renderer milestone.
/// Frosty's MeshSet reader starts with an AxisAlignedBox (two padded Vec3 values = 32 bytes),
/// followed by six 64-bit LOD relocation pointers. We deliberately parse only stable fields here;
/// section/geometry declaration decoding lands behind this API without changing Scene/Viewport code.
/// </summary>
public static class MeshSetProbe
{
    public const uint MeshSetResType = 0x49B156D4;
    private const int MaxLodCount = 6;

    public static bool TryReadBounds(ReadOnlySpan<byte> data, out Vector3 min, out Vector3 max)
    {
        min = default;
        max = default;
        if (data.Length < 32) return false;

        min = new Vector3(
            ReadSingleLE(data.Slice(0, 4)),
            ReadSingleLE(data.Slice(4, 4)),
            ReadSingleLE(data.Slice(8, 4)));
        max = new Vector3(
            ReadSingleLE(data.Slice(16, 4)),
            ReadSingleLE(data.Slice(20, 4)),
            ReadSingleLE(data.Slice(24, 4)));

        return IsFinite(min) && IsFinite(max) &&
               max.X >= min.X && max.Y >= min.Y && max.Z >= min.Z &&
               Math.Abs(max.X - min.X) < 1_000_000 &&
               Math.Abs(max.Y - min.Y) < 1_000_000 &&
               Math.Abs(max.Z - min.Z) < 1_000_000;
    }

    public static IReadOnlyList<long> ReadLodOffsets(ReadOnlySpan<byte> data)
    {
        var result = new List<long>(MaxLodCount);
        var offset = 32;
        for (var i = 0; i < MaxLodCount && offset + 8 <= data.Length; i++, offset += 8)
            result.Add(BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8)));
        return result;
    }

    /// <summary>
    /// MeshSet LOD records contain chunk GUIDs. Until the complete BF2 LOD layout parser is enabled,
    /// correlate 16-byte windows against the manifest's authoritative chunk GUID set. The match is
    /// exact, so false positives are vanishingly unlikely and every returned GUID is known-readable.
    /// </summary>
    public static Guid? FindFirstKnownChunk(ReadOnlySpan<byte> data, IReadOnlyDictionary<Guid, ManifestChunkDescriptor> chunks)
    {
        if (chunks.Count == 0 || data.Length < 16) return null;

        // Frostbite structures are naturally aligned. Search 4-byte boundaries first (fast path),
        // then byte-by-byte only if required for a layout variant.
        for (var stepPass = 0; stepPass < 2; stepPass++)
        {
            var step = stepPass == 0 ? 4 : 1;
            for (var i = 0; i + 16 <= data.Length; i += step)
            {
                var id = new Guid(data.Slice(i, 16));
                if (id != Guid.Empty && chunks.ContainsKey(id))
                    return id;
            }
        }
        return null;
    }

    private static float ReadSingleLE(ReadOnlySpan<byte> bytes) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
