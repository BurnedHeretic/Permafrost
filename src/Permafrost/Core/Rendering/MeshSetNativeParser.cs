using System.Buffers.Binary;
using System.Numerics;

namespace Permafrost.Core.Rendering;

/// <summary>
/// SWBF2-specific MeshSet metadata and fallback-geometry decoder.
/// Layout follows the StarWarsBattlefrontII branches in Frosty's MeshSet resource reader:
/// six LOD pointers, five subset categories, two geometry declarations per section,
/// 16 elements + 16 streams per declaration, and chunk vertex data followed by indices.
/// </summary>
public static class MeshSetNativeParser
{
    private const int MaxLodCount = 6;
    private const int MaxElements = 16;
    private const int MaxStreams = 16;
    private const int Bf2SectionSize = 352;
    private const int GeometryDeclSize = 100;
    private const int GeometryDecl0Offset = 84;
    private const int LodFixedSize = 176;
    private const int MaxPreviewTriangles = 150_000;

    public static bool TryParse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> resMeta, out NativeMeshSetLayout layout, out string error)
    {
        layout = new NativeMeshSetLayout();
        error = string.Empty;

        try
        {
            if (!MeshSetProbe.TryReadBounds(data, out var min, out var max))
                throw new InvalidDataException("MeshSet bounding box header is invalid.");
            if (data.Length < 148)
                throw new InvalidDataException("MeshSet header is truncated before the BF2 LOD count.");

            layout.BoundsMin = min;
            layout.BoundsMax = max;

            var lodOffsets = new long[MaxLodCount];
            for (var i = 0; i < MaxLodCount; i++)
                lodOffsets[i] = ReadInt64(data, 32 + i * 8);

            // SWBF2: bounds(32) + 6 LOD ptrs(48) + unknown ptr(8) + 2 string ptrs(16)
            // + name hash/type(8) + 12 ushort fade factors(24) + flags(4)
            // + draw-order bytes/short(4) = LOD count at 0x90 / 144.
            var lodCount = ReadUInt16(data, 144);
            if (lodCount == 0 || lodCount > MaxLodCount)
                throw new InvalidDataException($"MeshSet reports an invalid LOD count of {lodCount}.");

            for (var i = 0; i < lodCount; i++)
            {
                var offset = lodOffsets[i];
                if (offset <= 0 || offset > int.MaxValue || offset + LodFixedSize > data.Length)
                    throw new InvalidDataException($"LOD {i} pointer 0x{offset:X} is outside the MeshSet RES ({data.Length:N0} bytes).");
                layout.Lods.Add(ParseLod(data, checked((int)offset), i));
            }

            AssignInlineDataOffsets(layout, data.Length, resMeta);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            layout = new NativeMeshSetLayout();
            return false;
        }
    }

    public static NativeMeshGeometry? TryDecodePreview(
        NativeMeshSetLayout layout,
        ReadOnlySpan<byte> sourceData,
        int lodIndex,
        Guid? chunkId,
        bool usesInlineData,
        out string status)
    {
        status = string.Empty;
        if (lodIndex < 0 || lodIndex >= layout.Lods.Count)
        {
            status = "Requested LOD is outside the parsed MeshSet.";
            return null;
        }

        var lod = layout.Lods[lodIndex];
        var required = checked((long)lod.VertexBufferSize + lod.IndexBufferSize);
        if (required <= 0 || required > sourceData.Length)
        {
            status = $"LOD {lodIndex} geometry buffer is truncated: need {required:N0}, have {sourceData.Length:N0} bytes.";
            return null;
        }

        // Frosty's MeshSet renderer asks the game's RenderFormat enum whether indices are R16 or
        // R32. The standalone editor does not load the generated SDK enum yet, so do not guess one
        // value when both layouts fit the byte count: try both and retain the valid decode with the
        // most triangles. This removes a common reason for perfectly valid MeshSets falling back to
        // proxy bounds.
        NativeMeshGeometry? best = null;
        string bestStatus = string.Empty;
        foreach (var indexUnit in GetIndexUnitCandidates(lod))
        {
            var candidate = TryDecodePreviewWithIndexUnit(
                layout, sourceData, lodIndex, chunkId, usesInlineData, indexUnit, out var candidateStatus);
            if (candidate != null && (best == null || candidate.TriangleCount > best.TriangleCount))
            {
                best = candidate;
                bestStatus = candidateStatus + $" Index buffer: {indexUnit * 8}-bit.";
            }
            else if (best == null && !string.IsNullOrWhiteSpace(candidateStatus))
            {
                bestStatus = candidateStatus;
            }
        }

        status = bestStatus;
        return best;
    }

    private static NativeMeshGeometry? TryDecodePreviewWithIndexUnit(
        NativeMeshSetLayout layout,
        ReadOnlySpan<byte> sourceData,
        int lodIndex,
        Guid? chunkId,
        bool usesInlineData,
        int indexUnit,
        out string status)
    {
        status = string.Empty;
        var lod = layout.Lods[lodIndex];
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var decoded = 0;
        var skipped = 0;
        var triangleBudget = MaxPreviewTriangles;

        var candidateSections = lod.Sections.Where(s => s.Renderable && s.VertexCount > 0 && s.PrimitiveCount > 0).ToArray();
        if (candidateSections.Length == 0)
            candidateSections = lod.Sections.Where(s => s.VertexCount > 0 && s.PrimitiveCount > 0).ToArray();

        foreach (var section in candidateSections)
        {
            if (triangleBudget <= 0)
            {
                skipped++;
                continue;
            }

            if (!TryDecodeSectionPositions(sourceData, lod, section, out var sectionPositions))
            {
                skipped++;
                continue;
            }

            if (!TryDecodeSectionIndices(sourceData, lod, section, indexUnit, triangleBudget, out var sectionIndices))
            {
                skipped++;
                continue;
            }

            if (sectionIndices.Count < 3)
            {
                skipped++;
                continue;
            }

            var baseVertex = positions.Count;
            positions.AddRange(sectionPositions);
            foreach (var index in sectionIndices)
                indices.Add(baseVertex + index);

            triangleBudget -= sectionIndices.Count / 3;
            decoded++;
        }

        if (positions.Count == 0 || indices.Count == 0)
        {
            status = $"LOD {lodIndex} metadata parsed, but no supported triangle sections could be decoded.";
            return null;
        }

        status = triangleBudget <= 0
            ? $"Native geometry decoded from LOD {lodIndex}; preview capped at {MaxPreviewTriangles:N0} triangles."
            : $"Native geometry decoded from LOD {lodIndex}: {positions.Count:N0} vertices / {indices.Count / 3:N0} triangles.";

        return new NativeMeshGeometry
        {
            Positions = positions.ToArray(),
            TriangleIndices = indices.ToArray(),
            LodIndex = lodIndex,
            SectionsDecoded = decoded,
            SectionsSkipped = skipped,
            ChunkId = chunkId,
            UsesInlineData = usesInlineData
        };
    }

    private static IEnumerable<int> GetIndexUnitCandidates(NativeMeshLodLayout lod)
    {
        // Preserve the likely Frostbite format first, then verify the alternative.
        var preferred = DetermineIndexUnitSize(lod);
        yield return preferred;
        yield return preferred == 2 ? 4 : 2;
    }

    public static IEnumerable<int> GetPreviewLodOrder(NativeMeshSetLayout layout)
    {
        // Prefer the highest-detail LOD when it is a sensible size. For very large architecture,
        // walk downward until the estimated triangle count is suitable for WPF's editor viewport.
        var good = layout.Lods
            .Select((lod, index) => (lod, index, tris: EstimateTriangles(lod)))
            .Where(x => x.lod.HasGeometrySource && x.tris > 0 && x.tris <= MaxPreviewTriangles)
            .OrderBy(x => x.index)
            .Select(x => x.index)
            .ToList();

        foreach (var i in good)
            yield return i;

        foreach (var i in layout.Lods
                     .Select((lod, index) => (lod, index))
                     .Where(x => x.lod.HasGeometrySource && !good.Contains(x.index))
                     .OrderBy(x => x.index)
                     .Select(x => x.index))
            yield return i;
    }

    private static NativeMeshLodLayout ParseLod(ReadOnlySpan<byte> data, int offset, int lodIndex)
    {
        var lod = new NativeMeshLodLayout
        {
            LodIndex = lodIndex,
            MeshType = ReadUInt32(data, offset),
            SectionCount = ReadInt32(data, offset + 8),
            SectionOffset = ReadInt64(data, offset + 12),
            Flags = ReadUInt32(data, offset + 80),
            IndexFormat = ReadInt32(data, offset + 84),
            IndexBufferSize = ReadUInt32(data, offset + 88),
            VertexBufferSize = ReadUInt32(data, offset + 92),
            AdjacencyBufferSize = ReadInt32(data, offset + 96),
            ChunkId = ReadGuid(data, offset + 100),
            InlineDataField = ReadUInt32(data, offset + 116)
        };

        if (lod.SectionCount < 0 || lod.SectionCount > 16384)
            throw new InvalidDataException($"LOD {lodIndex} has implausible section count {lod.SectionCount}.");
        if (lod.SectionOffset < 0 || lod.SectionOffset > int.MaxValue)
            throw new InvalidDataException($"LOD {lodIndex} section pointer is invalid.");

        var renderable = new HashSet<int>();
        var anyCategoryEntries = false;
        var categoryPos = offset + 20;
        for (var category = 0; category < 5; category++, categoryPos += 12)
        {
            var count = ReadInt32(data, categoryPos);
            var ptr = ReadInt64(data, categoryPos + 4);
            if (count < 0 || count > 4096)
                throw new InvalidDataException($"LOD {lodIndex} category {category} has invalid count {count}.");
            if (count == 0) continue;
            anyCategoryEntries = true;
            if (ptr < 0 || ptr > int.MaxValue || ptr + count > data.Length)
                continue;
            if (category <= 2)
            {
                for (var i = 0; i < count; i++)
                    renderable.Add(data[checked((int)ptr) + i]);
            }
        }

        if (lod.SectionCount > 0)
        {
            var sectionOffset = checked((int)lod.SectionOffset);
            var required = (long)sectionOffset + (long)lod.SectionCount * Bf2SectionSize;
            if (sectionOffset < 0 || required > data.Length)
                throw new InvalidDataException($"LOD {lodIndex} section table extends outside the MeshSet RES.");

            for (var i = 0; i < lod.SectionCount; i++)
            {
                var section = ParseSection(data, sectionOffset + i * Bf2SectionSize, i);
                section.Renderable = !anyCategoryEntries || renderable.Contains(i);
                lod.Sections.Add(section);
            }
        }

        return lod;
    }

    private static NativeMeshSectionLayout ParseSection(ReadOnlySpan<byte> data, int offset, int sectionIndex)
    {
        if ((long)offset + Bf2SectionSize > data.Length)
            throw new InvalidDataException($"Section {sectionIndex} is truncated.");

        var section = new NativeMeshSectionLayout
        {
            SectionIndex = sectionIndex,
            PrimitiveCount = ReadUInt32(data, offset + 32),
            StartIndex = ReadUInt32(data, offset + 36),
            VertexOffset = ReadUInt32(data, offset + 40),
            VertexCount = ReadUInt32(data, offset + 44),
            VertexStrideLegacy = data[offset + 48],
            PrimitiveType = data[offset + 49]
        };

        var decl = offset + GeometryDecl0Offset;
        var elements = new NativeVertexElement[MaxElements];
        for (var i = 0; i < MaxElements; i++)
        {
            var e = decl + i * 4;
            elements[i] = new NativeVertexElement(data[e], data[e + 1], data[e + 2], data[e + 3]);
        }

        var streams = new NativeVertexStream[MaxStreams];
        var streamBase = decl + MaxElements * 4;
        for (var i = 0; i < MaxStreams; i++)
        {
            var s = streamBase + i * 2;
            streams[i] = new NativeVertexStream(data[s], data[s + 1]);
        }

        var countBase = decl + MaxElements * 4 + MaxStreams * 2;
        section.Geometry = new NativeGeometryDeclaration
        {
            Elements = elements,
            Streams = streams,
            ElementCount = Math.Min(data[countBase], (byte)MaxElements),
            StreamCount = Math.Min(data[countBase + 1], (byte)MaxStreams)
        };
        return section;
    }

    private static void AssignInlineDataOffsets(NativeMeshSetLayout layout, int dataLength, ReadOnlySpan<byte> resMeta)
    {
        if (resMeta.Length < 8) return;
        var inlineStart = BinaryPrimitives.ReadUInt32LittleEndian(resMeta[..4]);
        var inlineSize = BinaryPrimitives.ReadUInt32LittleEndian(resMeta.Slice(4, 4));
        if (inlineStart == 0 || inlineSize == 0 || inlineStart >= dataLength) return;

        long cursor = inlineStart;
        var end = Math.Min((long)dataLength, (long)inlineStart + inlineSize);
        foreach (var lod in layout.Lods)
        {
            if (lod.ChunkId != Guid.Empty) continue;
            var bytes = checked((long)lod.VertexBufferSize + lod.IndexBufferSize);
            if (bytes <= 0 || cursor + bytes > end) continue;
            lod.InlineDataOffset = checked((int)cursor);
            lod.InlineDataLength = checked((int)bytes);
            cursor = Align16(cursor + bytes);
        }
    }

    private static bool TryDecodeSectionPositions(
        ReadOnlySpan<byte> source,
        NativeMeshLodLayout lod,
        NativeMeshSectionLayout section,
        out List<Vector3> positions)
    {
        positions = new List<Vector3>();
        if (section.VertexCount == 0 || section.VertexCount > 2_000_000)
            return false;

        var geometry = section.Geometry;
        NativeVertexElement? positionElement = null;
        for (var i = 0; i < geometry.ElementCount; i++)
        {
            var e = geometry.Elements[i];
            if (e.Usage == 0x01) // VertexElementUsage.Pos
            {
                positionElement = e;
                break;
            }
        }
        if (!positionElement.HasValue) return false;

        var element = positionElement.Value;
        if (element.StreamIndex >= geometry.StreamCount) return false;
        var stream = geometry.Streams[element.StreamIndex];
        if (stream.VertexStride == 0) return false;

        long streamOffset = section.VertexOffset;
        for (var i = 0; i < element.StreamIndex; i++)
            streamOffset += (long)geometry.Streams[i].VertexStride * section.VertexCount;

        var requiredElementBytes = PositionFormatSize(element.Format);
        if (requiredElementBytes == 0 || element.Offset + requiredElementBytes > stream.VertexStride)
            return false;

        var required = streamOffset + (long)(section.VertexCount - 1) * stream.VertexStride + element.Offset + requiredElementBytes;
        if (streamOffset < 0 || required > lod.VertexBufferSize || required > source.Length)
            return false;

        positions.Capacity = checked((int)section.VertexCount);
        for (var v = 0; v < section.VertexCount; v++)
        {
            var at = checked((int)(streamOffset + (long)v * stream.VertexStride + element.Offset));
            if (!TryReadPosition(source, at, element.Format, out var position))
                return false;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return false;
            positions.Add(position);
        }
        return true;
    }

    private static bool TryDecodeSectionIndices(
        ReadOnlySpan<byte> source,
        NativeMeshLodLayout lod,
        NativeMeshSectionLayout section,
        int indexUnit,
        int triangleBudget,
        out List<int> triangles)
    {
        triangles = new List<int>();
        if (indexUnit is not (2 or 4)) return false;

        long requestedIndexCount = section.PrimitiveType switch
        {
            0x03 => (long)section.PrimitiveCount * 3, // TriangleList
            0x05 => (long)section.PrimitiveCount + 2, // TriangleStrip
            _ => 0
        };
        if (requestedIndexCount <= 0) return false;

        var indexBufferStart = (long)lod.VertexBufferSize;
        var start = indexBufferStart + (long)section.StartIndex * indexUnit;
        var bytes = requestedIndexCount * indexUnit;
        if (start < indexBufferStart || start + bytes > indexBufferStart + lod.IndexBufferSize || start + bytes > source.Length)
            return false;

        var maxPrimitiveCount = section.PrimitiveCount > (uint)int.MaxValue ? int.MaxValue : (int)section.PrimitiveCount;
        var maxTriangles = Math.Min(triangleBudget, maxPrimitiveCount);
        if (section.PrimitiveType == 0x03)
        {
            triangles.Capacity = maxTriangles * 3;
            for (var t = 0; t < maxTriangles; t++)
            {
                var a = ReadIndexAt(source, checked((int)(start + t * 3L * indexUnit)), indexUnit);
                var b = ReadIndexAt(source, checked((int)(start + (t * 3L + 1) * indexUnit)), indexUnit);
                var c = ReadIndexAt(source, checked((int)(start + (t * 3L + 2) * indexUnit)), indexUnit);
                if ((uint)a >= section.VertexCount || (uint)b >= section.VertexCount || (uint)c >= section.VertexCount)
                    return false;
                if (a == b || b == c || a == c) continue;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
        }
        else
        {
            triangles.Capacity = maxTriangles * 3;
            for (var t = 0; t < maxTriangles; t++)
            {
                var a = ReadIndexAt(source, checked((int)(start + (long)t * indexUnit)), indexUnit);
                var b = ReadIndexAt(source, checked((int)(start + (long)(t + 1) * indexUnit)), indexUnit);
                var c = ReadIndexAt(source, checked((int)(start + (long)(t + 2) * indexUnit)), indexUnit);
                if ((uint)a >= section.VertexCount || (uint)b >= section.VertexCount || (uint)c >= section.VertexCount)
                    return false;
                if (a == b || b == c || a == c) continue;
                if ((t & 1) != 0) (a, b) = (b, a);
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
            }
        }
        return true;
    }

    private static int ReadIndexAt(ReadOnlySpan<byte> source, int offset, int indexUnit) =>
        indexUnit == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2))
            : unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4)));

    private static int DetermineIndexUnitSize(NativeMeshLodLayout lod)
    {
        // Frostbite's RenderFormat values in this generation track DXGI for these two formats.
        // Keep a structural fallback so an SDK enum variation does not block rendering.
        if (lod.IndexFormat == 42) return 4; // R32_UINT
        if (lod.IndexFormat == 57) return 2; // R16_UINT

        long maxIndexEnd = 0;
        foreach (var section in lod.Sections)
        {
            var count = section.PrimitiveType switch
            {
                0x03 => (long)section.PrimitiveCount * 3,
                0x05 => (long)section.PrimitiveCount + 2,
                _ => 0
            };
            maxIndexEnd = Math.Max(maxIndexEnd, (long)section.StartIndex + count);
        }

        var fits16 = maxIndexEnd * 2 <= lod.IndexBufferSize;
        var fits32 = maxIndexEnd * 4 <= lod.IndexBufferSize;
        if (fits32 && !fits16) return 4;
        if (fits16 && !fits32) return 2;
        return 2;
    }

    private static int EstimateTriangles(NativeMeshLodLayout lod)
    {
        long count = 0;
        var sections = lod.Sections.Where(x => x.Renderable).ToArray();
        if (sections.Length == 0) sections = lod.Sections.ToArray();
        foreach (var section in sections)
        {
            if (section.PrimitiveType is 0x03 or 0x05)
                count += section.PrimitiveCount;
        }
        return checked((int)Math.Min(count, int.MaxValue));
    }

    private static bool TryReadPosition(ReadOnlySpan<byte> source, int at, byte format, out Vector3 value)
    {
        value = default;
        try
        {
            switch (format)
            {
                case 0x03: // Float3
                case 0x04: // Float4
                    value = new Vector3(ReadFloat(source, at), ReadFloat(source, at + 4), ReadFloat(source, at + 8));
                    return true;
                case 0x07: // Half3
                case 0x08: // Half4
                    value = new Vector3(ReadHalf(source, at), ReadHalf(source, at + 2), ReadHalf(source, at + 4));
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static int PositionFormatSize(byte format) => format switch
    {
        0x03 => 12,
        0x04 => 16,
        0x07 => 6,
        0x08 => 8,
        _ => 0
    };

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(data, offset));

    private static float ReadHalf(ReadOnlySpan<byte> data, int offset)
    {
        var bits = ReadUInt16(data, offset);
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) => unchecked((int)ReadUInt32(data, offset));

    private static long ReadInt64(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 8);
        return BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 16);
        return new Guid(data.Slice(offset, 16));
    }

    private static void Ensure(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new EndOfStreamException();
    }

    private static long Align16(long value) => (value + 15) & ~15L;
}

public sealed class NativeMeshSetLayout
{
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public List<NativeMeshLodLayout> Lods { get; } = new();
}

public sealed class NativeMeshLodLayout
{
    public int LodIndex { get; init; }
    public uint MeshType { get; init; }
    public int SectionCount { get; init; }
    public long SectionOffset { get; init; }
    public uint Flags { get; init; }
    public int IndexFormat { get; init; }
    public uint IndexBufferSize { get; init; }
    public uint VertexBufferSize { get; init; }
    public int AdjacencyBufferSize { get; init; }
    public Guid ChunkId { get; init; }
    public uint InlineDataField { get; init; }
    public int? InlineDataOffset { get; set; }
    public int InlineDataLength { get; set; }
    public List<NativeMeshSectionLayout> Sections { get; } = new();

    public bool HasGeometrySource => ChunkId != Guid.Empty || InlineDataOffset.HasValue;
}

public sealed class NativeMeshSectionLayout
{
    public int SectionIndex { get; init; }
    public uint PrimitiveCount { get; init; }
    public uint StartIndex { get; init; }
    public uint VertexOffset { get; init; }
    public uint VertexCount { get; init; }
    public byte VertexStrideLegacy { get; init; }
    public byte PrimitiveType { get; init; }
    public bool Renderable { get; set; }
    public NativeGeometryDeclaration Geometry { get; set; } = new();
}

public sealed class NativeGeometryDeclaration
{
    public NativeVertexElement[] Elements { get; init; } = Array.Empty<NativeVertexElement>();
    public NativeVertexStream[] Streams { get; init; } = Array.Empty<NativeVertexStream>();
    public byte ElementCount { get; init; }
    public byte StreamCount { get; init; }
}

public readonly record struct NativeVertexElement(byte Usage, byte Format, byte Offset, byte StreamIndex);
public readonly record struct NativeVertexStream(byte VertexStride, byte Classification);
