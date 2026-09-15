using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;
using Permafrost.Core.Scene;

namespace Permafrost.Core.Rendering;

/// <summary>
/// Resolves editable placement nodes through the native Frostbite render chain:
/// ReferenceObjectData -> Blueprint EBX -> MeshAsset EBX -> MeshSetResource (RES)
/// -> parsed MeshSet LOD -> chunk/inline vertex + index data.
/// </summary>
public sealed class NativeMeshResolver
{
    private readonly GameDataSource _source;
    private readonly Dictionary<ulong, NativeMeshInfo?> _meshSetCache = new();

    public NativeMeshResolver(GameDataSource source) => _source = source;

    /// <summary>
    /// Resolves a single EBX asset through the same Blueprint/MeshAsset/MeshSet chain used by
    /// scene placements. This powers the Asset Browser preview without requiring a level edit.
    /// </summary>
    public async Task<NativeMeshInfo?> ResolveAssetAsync(
        GameAssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind != GameAssetKind.Ebx)
            return null;
        return await ResolveFromEbxAsync(entry, cancellationToken);
    }

    public async Task<NativeMeshResolutionSummary> ResolveSceneAsync(
        SceneNode root,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var nodes = SceneBuilder.Flatten(root)
            .Where(n => n.Transform != null && n.SourceObject != null)
            .ToArray();

        var placementCandidates = 0;
        var blueprintResolved = 0;
        var meshAssetsResolved = 0;
        var meshSetsResolved = 0;
        var boundsResolved = 0;
        var chunksLinked = 0;
        var geometryResolved = 0;
        var inlineGeometryResolved = 0;
        long trianglesDecoded = 0;
        var failed = 0;
        var completed = 0;

        // External PointerRefs store FileGuids, not asset names. Frosty maintains a complete EBX
        // FileGuid table; Permafrost first expands the bundles that own the loaded scene so the
        // common case stays fast, then falls back to one cached global GUID pass if any Blueprint
        // still cannot be resolved. This fixes scenes where level EBXs were indexed but ordinary
        // prefab/mesh EBXs were invisible to the native renderer.
        var ownerBundleHashSet = new HashSet<uint>();
        foreach (var node in nodes)
        {
            if (node.OwnerAsset == null) continue;
            foreach (var hash in _source.Assets.GetBundleHashesForAsset(node.OwnerAsset))
                ownerBundleHashSet.Add(hash);
        }
        var ownerBundleHashes = ownerBundleHashSet.ToArray();
        if (ownerBundleHashes.Length > 0)
            await _source.BuildGuidIndexForBundlesAsync(ownerBundleHashes, progress, cancellationToken);

        var hasUnresolvedBlueprintGuid = nodes.Any(n =>
        {
            var pointer = n.SourceObject == null ? null : FindBlueprintPointer(n.SourceObject);
            return pointer?.External != null && ResolveEbx(pointer.External.FileGuid) == null;
        });

        if (hasUnresolvedBlueprintGuid)
        {
            progress?.Report(new ScanProgress("Building complete EBX GUID index for native references..."));
            await _source.BuildEbxGuidIndexAsync(levelsOnly: false, progress, cancellationToken);
        }

        progress?.Report(new ScanProgress("Resolving native MeshSets and LOD geometry...", 0, nodes.Length));
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pointer = FindBlueprintPointer(node.SourceObject!);
                if (pointer?.External == null)
                    continue;
                placementCandidates++;

                var blueprintEntry = ResolveEbx(pointer.External.FileGuid);
                if (blueprintEntry == null)
                {
                    failed++;
                    continue;
                }
                blueprintResolved++;

                var result = await ResolveFromEbxAsync(blueprintEntry, cancellationToken);
                if (result == null)
                {
                    failed++;
                    continue;
                }

                node.NativeMesh = result;
                if (result.MeshAsset != null) meshAssetsResolved++;
                if (result.MeshSetResource != null) meshSetsResolved++;
                if (result.HasBounds) boundsResolved++;
                if (result.LodChunkId.HasValue) chunksLinked++;
                if (result.Geometry is { } geometry)
                {
                    geometryResolved++;
                    if (geometry.UsesInlineData) inlineGeometryResolved++;
                    trianglesDecoded += geometry.TriangleCount;
                }
            }
            catch
            {
                failed++;
            }
            finally
            {
                completed++;
                if (completed % 25 == 0 || completed == nodes.Length)
                    progress?.Report(new ScanProgress("Resolving native MeshSets and LOD geometry...", completed, nodes.Length));
            }
        }

        return new NativeMeshResolutionSummary(
            placementCandidates, blueprintResolved, meshAssetsResolved, meshSetsResolved,
            boundsResolved, chunksLinked, geometryResolved, inlineGeometryResolved,
            trianglesDecoded, failed);
    }

    private async Task<NativeMeshInfo?> ResolveFromEbxAsync(GameAssetEntry firstEntry, CancellationToken cancellationToken)
    {
        var queue = new Queue<(GameAssetEntry Entry, int Depth)>();
        var visited = new HashSet<Guid>();
        queue.Enqueue((firstEntry, 0));

        while (queue.Count > 0)
        {
            var (entry, depth) = queue.Dequeue();
            if (depth > 5) continue;

            EbxDocument doc;
            try
            {
                doc = await _source.OpenEbxAsync(entry, cancellationToken);
            }
            catch
            {
                continue;
            }
            if (!visited.Add(doc.FileGuid)) continue;

            if (TryFindMeshSetResource(doc, out var resId) && resId != 0)
            {
                var info = await ResolveMeshSetAsync(entry, resId, cancellationToken);
                if (info != null) return info;
            }

            // The same EBX frequently appears in multiple Frostbite bundles. The old renderer
            // indexed only the single bundle occurrence retained by name lookup, which could miss
            // the prefab/mesh dependencies carried by another occurrence of the same asset. Expand
            // the GUID index across every bundle that contains the asset before following imports.
            var relatedBundles = _source.Assets.GetBundleHashesForAsset(entry);
            if (relatedBundles.Count > 0)
                await _source.BuildGuidIndexForBundlesAsync(relatedBundles, null, cancellationToken);

            foreach (var external in EnumerateExternalPointers(doc))
            {
                if (external.External == null) continue;
                var next = ResolveEbx(external.External.FileGuid);
                if (next != null)
                    queue.Enqueue((next, depth + 1));
            }
        }
        return null;
    }

    private async Task<NativeMeshInfo?> ResolveMeshSetAsync(
        GameAssetEntry meshAsset,
        ulong resId,
        CancellationToken cancellationToken)
    {
        if (_meshSetCache.TryGetValue(resId, out var cached))
            return cached;

        if (!_source.Assets.TryGetResById(resId, out var res))
        {
            _meshSetCache[resId] = null;
            return null;
        }

        if (res.ResType != 0 && res.ResType != MeshSetProbe.MeshSetResType)
        {
            var wrongType = new NativeMeshInfo
            {
                MeshAsset = meshAsset,
                MeshSetResource = res,
                MeshSetResId = resId,
                Status = $"MeshSetResource RID resolved to RES type 0x{res.ResType:X8}, expected 0x{MeshSetProbe.MeshSetResType:X8}."
            };
            _meshSetCache[resId] = wrongType;
            return wrongType;
        }

        try
        {
            var data = await _source.ReadResAsync(res, null, cancellationToken);
            var meta = res.ResMeta ?? Array.Empty<byte>();

            if (!MeshSetNativeParser.TryParse(data, meta, out var layout, out var parseError))
            {
                if (!MeshSetProbe.TryReadBounds(data, out var fallbackMin, out var fallbackMax))
                {
                    var invalid = new NativeMeshInfo
                    {
                        MeshAsset = meshAsset,
                        MeshSetResource = res,
                        MeshSetResId = resId,
                        Status = $"MeshSet RES resolved, but the BF2 LOD parser rejected it: {parseError}"
                    };
                    _meshSetCache[resId] = invalid;
                    return invalid;
                }

                var boundsOnly = new NativeMeshInfo
                {
                    MeshAsset = meshAsset,
                    MeshSetResource = res,
                    MeshSetResId = resId,
                    BoundsMin = fallbackMin,
                    BoundsMax = fallbackMax,
                    BoundsValid = true,
                    Status = $"MeshSet bounds resolved; BF2 LOD parser needs another layout variant: {parseError}"
                };
                _meshSetCache[resId] = boundsOnly;
                return boundsOnly;
            }

            NativeMeshGeometry? geometry = null;
            Guid? selectedChunk = null;
            var selectedChunkBytes = 0;
            var lastDecodeStatus = string.Empty;

            foreach (var lodIndex in MeshSetNativeParser.GetPreviewLodOrder(layout))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lod = layout.Lods[lodIndex];

                if (lod.ChunkId != Guid.Empty)
                {
                    selectedChunk ??= lod.ChunkId;
                    if (!_source.Layout.Manifest.Chunks.ContainsKey(lod.ChunkId) &&
                        !_source.Assets.TryGetChunkById(lod.ChunkId, out _))
                    {
                        lastDecodeStatus = $"LOD {lodIndex} points to chunk {lod.ChunkId}, but it is absent from both the global manifest and bundle-local chunk index.";
                        continue;
                    }

                    try
                    {
                        var chunkData = await _source.ReadChunkAsync(lod.ChunkId, null, cancellationToken);
                        selectedChunkBytes = Math.Max(selectedChunkBytes, chunkData.Length);
                        geometry = MeshSetNativeParser.TryDecodePreview(
                            layout, chunkData, lodIndex, lod.ChunkId, usesInlineData: false, out lastDecodeStatus);
                        if (geometry != null)
                        {
                            selectedChunk = lod.ChunkId;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastDecodeStatus = $"LOD {lodIndex} chunk read failed: {ex.Message}";
                    }
                }
                else if (lod.InlineDataOffset is { } inlineOffset && lod.InlineDataLength > 0)
                {
                    if ((long)inlineOffset + lod.InlineDataLength > data.Length)
                    {
                        lastDecodeStatus = $"LOD {lodIndex} inline data extends outside the MeshSet RES.";
                        continue;
                    }

                    geometry = MeshSetNativeParser.TryDecodePreview(
                        layout,
                        data.AsSpan(inlineOffset, lod.InlineDataLength),
                        lodIndex,
                        chunkId: null,
                        usesInlineData: true,
                        out lastDecodeStatus);
                    if (geometry != null)
                        break;
                }
            }

            // Keep a real LOD chunk visible in the inspector even when that particular layout
            // could not yet be converted to WPF triangles.
            if (!selectedChunk.HasValue)
            {
                var firstChunk = layout.Lods.Select(x => x.ChunkId).FirstOrDefault(x => x != Guid.Empty);
                if (firstChunk != Guid.Empty)
                    selectedChunk = firstChunk;
            }

            var status = geometry != null
                ? $"{lastDecodeStatus} Source: {geometry.DataSource}."
                : string.IsNullOrWhiteSpace(lastDecodeStatus)
                    ? $"MeshSet/Lod metadata parsed ({layout.Lods.Count} LODs); no chunk or inline geometry source was available."
                    : $"MeshSet/Lod metadata parsed ({layout.Lods.Count} LODs); {lastDecodeStatus}";

            var info = new NativeMeshInfo
            {
                MeshAsset = meshAsset,
                MeshSetResource = res,
                MeshSetResId = resId,
                BoundsMin = layout.BoundsMin,
                BoundsMax = layout.BoundsMax,
                BoundsValid = true,
                LodChunkId = geometry?.ChunkId ?? selectedChunk,
                LodChunkBytes = selectedChunkBytes,
                ParsedLodCount = layout.Lods.Count,
                Geometry = geometry,
                Status = status
            };
            _meshSetCache[resId] = info;
            return info;
        }
        catch
        {
            _meshSetCache[resId] = null;
            return null;
        }
    }

    private GameAssetEntry? ResolveEbx(Guid guid) =>
        _source.Assets.TryGetByGuid(guid, out var entry) && entry.Kind == GameAssetKind.Ebx ? entry : null;

    private static EbxPointer? FindBlueprintPointer(EbxObject obj)
    {
        foreach (var name in new[] { "Blueprint", "Prefab", "ObjectBlueprint", "Mesh" })
        {
            if (obj.Fields.TryGetValue(name, out var value) && value is EbxPointer { Kind: EbxPointerKind.External } pointer)
                return pointer;
        }

        foreach (var pair in obj.Fields)
        {
            if (pair.Value is EbxPointer { Kind: EbxPointerKind.External } pointer &&
                pair.Key.Contains("Blueprint", StringComparison.OrdinalIgnoreCase))
                return pointer;
        }
        return null;
    }

    private static bool TryFindMeshSetResource(EbxDocument doc, out ulong resId)
    {
        foreach (var obj in doc.Objects)
        {
            foreach (var pair in obj.Fields)
            {
                if (pair.Value is ulong value && value != 0 &&
                    pair.Key.Equals("MeshSetResource", StringComparison.OrdinalIgnoreCase))
                {
                    resId = value;
                    return true;
                }
            }
        }
        resId = 0;
        return false;
    }

    private static IEnumerable<EbxPointer> EnumerateExternalPointers(EbxDocument doc)
    {
        var visited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        foreach (var obj in doc.Objects)
        {
            foreach (var value in obj.Fields.Values)
            {
                foreach (var pointer in EnumerateExternalPointers(value, visited))
                    yield return pointer;
            }
        }
    }

    private static IEnumerable<EbxPointer> EnumerateExternalPointers(object? value, HashSet<EbxObject> visited)
    {
        switch (value)
        {
            case EbxPointer { Kind: EbxPointerKind.External } external:
                yield return external;
                break;
            case EbxObject nested when visited.Add(nested):
                foreach (var child in nested.Fields.Values)
                    foreach (var pointer in EnumerateExternalPointers(child, visited))
                        yield return pointer;
                break;
            case List<object?> list:
                foreach (var child in list)
                    foreach (var pointer in EnumerateExternalPointers(child, visited))
                        yield return pointer;
                break;
        }
    }
}
