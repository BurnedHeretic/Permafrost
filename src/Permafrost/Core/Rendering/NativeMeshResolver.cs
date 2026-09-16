using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;
using Permafrost.Core.Scene;

namespace Permafrost.Core.Rendering;

/// <summary>
/// Resolves editable placement nodes through the Frostbite render chain.
/// 0.04.3 keeps the ClassGuid-aware object walk from 0.04.2 and adds a bounded EBX import-table
/// fallback. The import table is Frostbite's authoritative dependency list, so it can recover
/// mesh assets even when an unfamiliar prefab class prevents a field-level pointer from being
/// exposed cleanly by the standalone self-describing reader.
/// </summary>
public sealed class NativeMeshResolver
{
    private const int MaxGraphDepth = 10;
    private const int MaxDocumentsPerResolve = 256;
    private const int MaxReferencesPerResolve = 4096;

    private readonly GameDataSource _source;
    private readonly Dictionary<ulong, NativeMeshInfo?> _meshSetCache = new();
    private bool _globalGuidIndexEnsured;

    public NativeMeshResolver(GameDataSource source) => _source = source;

    private sealed class ResolveTrace
    {
        public int DocumentsOpened;
        public int TargetClassGuidMatches;
        public int TargetClassGuidMisses;
        public int TargetObjectsVisited;
        public int MeshEntityObjects;
        public int ExternalRefsSeen;
        public int ExternalRefsResolved;
        public int ImportTableFallbackRefs;
        public int MeshPointersFollowed;
        public int MeshAssetObjects;
        public int MeshSetReferences;
        public int ReferenceBudgetStops;
        public string LastStatus = string.Empty;
    }

    private readonly record struct ResolveTarget(GameAssetEntry Entry, Guid ClassGuid, int Depth, bool MeshBiased);
    private readonly record struct ExternalPointerEdge(EbxPointer Pointer, string FieldName, string OwnerClass);
    private sealed record ResolveOutcome(NativeMeshInfo? Info, ResolveTrace Trace);

    public async Task<NativeMeshInfo?> ResolveAssetAsync(
        GameAssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind != GameAssetKind.Ebx)
            return null;
        var outcome = await ResolveFromEbxAsync(entry, Guid.Empty, cancellationToken);
        return outcome.Info;
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
        var documentsOpened = 0;
        var classGuidMatches = 0;
        var classGuidMisses = 0;
        var targetObjectsVisited = 0;
        var meshEntityObjects = 0;
        var externalRefsSeen = 0;
        var externalRefsResolved = 0;
        var importFallbackRefs = 0;
        var meshPointersFollowed = 0;
        var meshAssetsResolved = 0;
        var meshSetsResolved = 0;
        var boundsResolved = 0;
        var chunksLinked = 0;
        var geometryResolved = 0;
        var inlineGeometryResolved = 0;
        long trianglesDecoded = 0;
        var failed = 0;
        var completed = 0;

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
            _globalGuidIndexEnsured = true;
        }

        progress?.Report(new ScanProgress("Resolving prefab object graphs and native MeshSets...", 0, nodes.Length));
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

                var outcome = await ResolveFromEbxAsync(
                    blueprintEntry,
                    pointer.External.ClassGuid,
                    cancellationToken);

                documentsOpened += outcome.Trace.DocumentsOpened;
                classGuidMatches += outcome.Trace.TargetClassGuidMatches;
                classGuidMisses += outcome.Trace.TargetClassGuidMisses;
                targetObjectsVisited += outcome.Trace.TargetObjectsVisited;
                meshEntityObjects += outcome.Trace.MeshEntityObjects;
                externalRefsSeen += outcome.Trace.ExternalRefsSeen;
                externalRefsResolved += outcome.Trace.ExternalRefsResolved;
                importFallbackRefs += outcome.Trace.ImportTableFallbackRefs;
                meshPointersFollowed += outcome.Trace.MeshPointersFollowed;
                if (outcome.Trace.MeshAssetObjects > 0)
                    meshAssetsResolved++;

                var result = outcome.Info;
                if (result == null)
                {
                    failed++;
                    continue;
                }

                // Even a partial result is useful in the inspector because it explains whether the
                // failure is a missing RES RID, wrong resource type, parser variant, or chunk read.
                node.NativeMesh = result;
                if (result.MeshSetResource != null) meshSetsResolved++;
                if (result.HasBounds) boundsResolved++;
                if (result.LodChunkId.HasValue) chunksLinked++;
                if (result.Geometry is { } geometry)
                {
                    geometryResolved++;
                    if (geometry.UsesInlineData) inlineGeometryResolved++;
                    trianglesDecoded += geometry.TriangleCount;
                }

                if (result.MeshSetResource == null && !result.HasBounds && result.Geometry == null)
                    failed++;
            }
            catch
            {
                failed++;
            }
            finally
            {
                completed++;
                if (completed % 25 == 0 || completed == nodes.Length)
                    progress?.Report(new ScanProgress("Resolving prefab object graphs and native MeshSets...", completed, nodes.Length));
            }
        }

        return new NativeMeshResolutionSummary(
            placementCandidates, blueprintResolved, documentsOpened, classGuidMatches, classGuidMisses,
            targetObjectsVisited, meshEntityObjects, externalRefsSeen, externalRefsResolved,
            importFallbackRefs, meshPointersFollowed, meshAssetsResolved, meshSetsResolved,
            boundsResolved, chunksLinked, geometryResolved, inlineGeometryResolved, trianglesDecoded, failed);
    }

    /// <summary>
    /// Resolve one EBX pointer chain. The ClassGuid is significant: prefab EBXs can contain many
    /// exported objects, and following only FileGuid loses which SpatialPrefabBlueprint/GameObject
    /// the placement actually referenced.
    /// </summary>
    private async Task<ResolveOutcome> ResolveFromEbxAsync(
        GameAssetEntry firstEntry,
        Guid firstClassGuid,
        CancellationToken cancellationToken)
    {
        var trace = new ResolveTrace();
        var queue = new Queue<ResolveTarget>();
        var visited = new HashSet<(Guid FileGuid, Guid ClassGuid)>();
        NativeMeshInfo? bestPartial = null;
        var referencesQueued = 0;

        queue.Enqueue(new ResolveTarget(firstEntry, firstClassGuid, 0, false));
        referencesQueued++;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trace.DocumentsOpened >= MaxDocumentsPerResolve)
            {
                trace.ReferenceBudgetStops++;
                trace.LastStatus = $"Native dependency walk stopped after {MaxDocumentsPerResolve:N0} EBX documents to avoid an unbounded prefab graph.";
                break;
            }

            var target = queue.Dequeue();
            if (target.Depth > MaxGraphDepth)
                continue;

            EbxDocument doc;
            try
            {
                doc = await _source.OpenEbxAsync(target.Entry, cancellationToken);
                trace.DocumentsOpened++;
            }
            catch (Exception ex)
            {
                trace.LastStatus = $"Failed to open EBX {target.Entry.Name}: {ex.Message}";
                continue;
            }

            var visitKey = (doc.FileGuid, target.ClassGuid);
            if (!visited.Add(visitKey))
                continue;

            var root = FindTargetObject(doc, target.ClassGuid);
            var classGuidMiss = target.ClassGuid != Guid.Empty && root == null;
            if (target.ClassGuid != Guid.Empty)
            {
                if (classGuidMiss) trace.TargetClassGuidMisses++;
                else trace.TargetClassGuidMatches++;
            }
            root ??= doc.RootObject;

            var reachable = EnumerateReachableObjects(doc, root).ToList();
            // A ClassGuid selects one exported object, but some prefab variants keep useful child
            // objects outside a normal internal-pointer ownership graph. When the target cannot be
            // mapped, or a prefab appears childless, inspect all instances in that EBX as a bounded
            // compatibility fallback. This mirrors Frosty's ability to address any exported object.
            if ((classGuidMiss || (IsPrefabClass(root.ClassName) && reachable.Count <= 1)) && doc.Objects.Count > 1)
            {
                foreach (var obj in doc.Objects)
                    if (!reachable.Contains(obj, ReferenceEqualityComparer.Instance))
                        reachable.Add(obj);
            }

            trace.TargetObjectsVisited += reachable.Count;
            foreach (var obj in reachable)
            {
                if (IsMeshEntityClass(obj.ClassName))
                    trace.MeshEntityObjects++;
            }

            // MeshAsset classes expose a ResourceRef named MeshSetResource. Search recursively
            // through embedded structs so profile-specific wrapper structs do not hide the RID.
            foreach (var obj in reachable)
            {
                foreach (var rid in EnumerateMeshSetResources(obj))
                {
                    trace.MeshAssetObjects++;
                    trace.MeshSetReferences++;
                    var info = await ResolveMeshSetAsync(target.Entry, rid, cancellationToken);
                    if (info == null)
                        continue;

                    if (info.MeshSetResource != null && (info.HasBounds || info.ParsedLodCount > 0 || info.HasGeometry))
                        return new ResolveOutcome(info, trace);

                    bestPartial ??= info;
                }
            }

            var relatedBundles = _source.Assets.GetBundleHashesForAsset(target.Entry);
            if (relatedBundles.Count > 0)
                await _source.BuildGuidIndexForBundlesAsync(relatedBundles, null, cancellationToken);

            // First follow pointers actually present in the selected object graph. Mesh-named
            // fields are queued first, but non-mesh dependencies remain eligible because BF2 uses
            // several wrapper/prefab layers before a MeshAsset is reached.
            var edges = EnumerateExternalPointerEdges(reachable)
                .Where(x => x.Pointer.External != null)
                .OrderByDescending(x => IsMeshField(x.FieldName) || IsMeshEntityClass(x.OwnerClass))
                .ToArray();
            trace.ExternalRefsSeen += edges.Length;

            var representedImports = new HashSet<(Guid FileGuid, Guid ClassGuid)>();
            foreach (var edge in edges)
            {
                var external = edge.Pointer.External!;
                representedImports.Add((external.FileGuid, external.ClassGuid));
                if (referencesQueued >= MaxReferencesPerResolve)
                {
                    trace.ReferenceBudgetStops++;
                    break;
                }

                var next = await ResolveEbxForGraphAsync(external.FileGuid, cancellationToken);
                if (next == null)
                    continue;

                trace.ExternalRefsResolved++;
                var meshBiased = IsMeshField(edge.FieldName) || IsMeshEntityClass(edge.OwnerClass) || IsLikelyMeshAssetName(next.Name);
                if (meshBiased)
                    trace.MeshPointersFollowed++;

                queue.Enqueue(new ResolveTarget(next, external.ClassGuid, target.Depth + 1, meshBiased));
                referencesQueued++;
            }

            // Important BF2 fallback: the EBX import table is the authoritative dependency list.
            // If a self-described class variant is unfamiliar, a field pointer can be missed even
            // though its FileGuid/ClassGuid pair still exists in Imports. Walk those remaining
            // imports after the explicit object graph, within a strict budget.
            if (referencesQueued < MaxReferencesPerResolve && doc.Imports.Count > 0)
            {
                foreach (var import in doc.Imports)
                {
                    if (referencesQueued >= MaxReferencesPerResolve)
                    {
                        trace.ReferenceBudgetStops++;
                        break;
                    }
                    if (import.FileGuid == Guid.Empty || representedImports.Contains((import.FileGuid, import.ClassGuid)))
                        continue;

                    trace.ExternalRefsSeen++;
                    var next = await ResolveEbxForGraphAsync(import.FileGuid, cancellationToken);
                    if (next == null)
                        continue;

                    trace.ExternalRefsResolved++;
                    trace.ImportTableFallbackRefs++;
                    var meshBiased = IsLikelyMeshAssetName(next.Name);
                    if (meshBiased)
                        trace.MeshPointersFollowed++;

                    queue.Enqueue(new ResolveTarget(next, import.ClassGuid, target.Depth + 1, meshBiased));
                    referencesQueued++;
                }
            }
        }

        if (bestPartial != null)
            return new ResolveOutcome(bestPartial, trace);

        var status = !string.IsNullOrWhiteSpace(trace.LastStatus)
            ? trace.LastStatus
            : $"Blueprint dependency graph resolved, but no renderable MeshSet was reached. " +
              $"Opened {trace.DocumentsOpened:N0} EBX document(s), visited {trace.TargetObjectsVisited:N0} object(s), " +
              $"matched {trace.TargetClassGuidMatches:N0} ClassGuid target(s) ({trace.TargetClassGuidMisses:N0} miss(es)), " +
              $"found {trace.MeshEntityObjects:N0} mesh entity object(s), resolved {trace.ExternalRefsResolved:N0}/{trace.ExternalRefsSeen:N0} external reference(s), " +
              $"used {trace.ImportTableFallbackRefs:N0} import-table fallback reference(s), and found {trace.MeshSetReferences:N0} MeshSetResource field(s).";
        return new ResolveOutcome(new NativeMeshInfo { Status = status }, trace);
    }

    private async Task<GameAssetEntry?> ResolveEbxForGraphAsync(Guid guid, CancellationToken cancellationToken)
    {
        var entry = ResolveEbx(guid);
        if (entry != null)
            return entry;

        if (_globalGuidIndexEnsured)
            return null;

        // A level's own bundle set does not necessarily contain every indirect prefab/mesh
        // dependency. Build Frosty's equivalent global FileGuid table only when the graph actually
        // encounters an unresolved dependency, then persist it through GameDataSource's cache.
        await _source.BuildEbxGuidIndexAsync(levelsOnly: false, progress: null, cancellationToken: cancellationToken);
        _globalGuidIndexEnsured = true;
        return ResolveEbx(guid);
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
            var missing = new NativeMeshInfo
            {
                MeshAsset = meshAsset,
                MeshSetResId = resId,
                Status = $"Mesh asset resolved and references MeshSetResource 0x{resId:X16}, but that RID is not present in the indexed RES table."
            };
            _meshSetCache[resId] = missing;
            return missing;
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
        catch (Exception ex)
        {
            var failed = new NativeMeshInfo
            {
                MeshAsset = meshAsset,
                MeshSetResource = res,
                MeshSetResId = resId,
                Status = $"MeshSet RES read/decode failed: {ex.Message}"
            };
            _meshSetCache[resId] = failed;
            return failed;
        }
    }


    private GameAssetEntry? ResolveEbx(Guid guid) =>
        _source.Assets.TryGetByGuid(guid, out var entry) && entry.Kind == GameAssetKind.Ebx ? entry : null;

    private static EbxObject? FindTargetObject(EbxDocument doc, Guid classGuid)
    {
        if (classGuid == Guid.Empty)
            return doc.RootObject;

        return doc.Objects.FirstOrDefault(x => x.InstanceGuid == classGuid);
    }

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

    private static IEnumerable<ulong> EnumerateMeshSetResources(EbxObject obj)
    {
        var visited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        foreach (var rid in EnumerateMeshSetResourcesValue(obj, visited))
            yield return rid;
    }

    private static IEnumerable<ulong> EnumerateMeshSetResourcesValue(
        object? value,
        HashSet<EbxObject> visited)
    {
        switch (value)
        {
            case EbxObject obj when visited.Add(obj):
                foreach (var pair in obj.Fields)
                {
                    if (pair.Key.Equals("MeshSetResource", StringComparison.OrdinalIgnoreCase) &&
                        TryAsResourceId(pair.Value, out var rid) && rid != 0)
                    {
                        yield return rid;
                    }

                    // Only recurse into embedded structs/lists. Internal object pointers are walked
                    // separately by EnumerateReachableObjects, which avoids cycles and duplicates.
                    if (pair.Value is EbxObject or List<object?>)
                    {
                        foreach (var nestedRid in EnumerateMeshSetResourcesValue(pair.Value, visited))
                            yield return nestedRid;
                    }
                }
                break;

            case List<object?> list:
                foreach (var child in list)
                    foreach (var rid in EnumerateMeshSetResourcesValue(child, visited))
                        yield return rid;
                break;
        }
    }

    private static bool TryAsResourceId(object? value, out ulong rid)
    {
        switch (value)
        {
            case ulong u:
                rid = u;
                return true;
            case long l when l > 0:
                rid = unchecked((ulong)l);
                return true;
            case uint u32:
                rid = u32;
                return true;
            default:
                rid = 0;
                return false;
        }
    }

    private static IEnumerable<EbxObject> EnumerateReachableObjects(EbxDocument doc, EbxObject start)
    {
        var visited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<EbxObject>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (!visited.Add(obj))
                continue;

            yield return obj;
            foreach (var value in obj.Fields.Values)
                PushReachable(value, doc, stack, visited);
        }
    }

    private static void PushReachable(
        object? value,
        EbxDocument doc,
        Stack<EbxObject> stack,
        HashSet<EbxObject> visited)
    {
        switch (value)
        {
            case EbxObject nested when !visited.Contains(nested):
                stack.Push(nested);
                break;
            case EbxPointer { Kind: EbxPointerKind.Internal } pointer
                when pointer.InternalObjectIndex >= 0 && pointer.InternalObjectIndex < doc.Objects.Count:
            {
                var target = doc.Objects[pointer.InternalObjectIndex];
                if (!visited.Contains(target)) stack.Push(target);
                break;
            }
            case List<object?> list:
                foreach (var child in list)
                    PushReachable(child, doc, stack, visited);
                break;
        }
    }

    private static IEnumerable<ExternalPointerEdge> EnumerateExternalPointerEdges(IEnumerable<EbxObject> objects)
    {
        var nestedVisited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<(Guid FileGuid, Guid ClassGuid, string Field, string Owner)>();
        foreach (var obj in objects)
        {
            foreach (var pair in obj.Fields)
            {
                foreach (var edge in EnumerateExternalPointerEdges(pair.Value, pair.Key, obj.ClassName, nestedVisited))
                {
                    var external = edge.Pointer.External;
                    if (external == null) continue;
                    if (seen.Add((external.FileGuid, external.ClassGuid, edge.FieldName, edge.OwnerClass)))
                        yield return edge;
                }
            }
        }
    }

    private static IEnumerable<ExternalPointerEdge> EnumerateExternalPointerEdges(
        object? value,
        string fieldName,
        string ownerClass,
        HashSet<EbxObject> visited)
    {
        switch (value)
        {
            case EbxPointer { Kind: EbxPointerKind.External } external:
                yield return new ExternalPointerEdge(external, fieldName, ownerClass);
                break;
            case EbxObject nested when visited.Add(nested):
                foreach (var pair in nested.Fields)
                    foreach (var edge in EnumerateExternalPointerEdges(pair.Value, pair.Key, nested.ClassName, visited))
                        yield return edge;
                break;
            case List<object?> list:
                foreach (var child in list)
                    foreach (var edge in EnumerateExternalPointerEdges(child, fieldName, ownerClass, visited))
                        yield return edge;
                break;
        }
    }

    private static bool IsPrefabClass(string className) =>
        className.Contains("PrefabBlueprint", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WorldPartData", StringComparison.OrdinalIgnoreCase);

    private static bool IsMeshEntityClass(string className) =>
        className.Contains("MeshEntityData", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("MeshComponentData", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("MeshProxy", StringComparison.OrdinalIgnoreCase);

    private static bool IsMeshField(string fieldName) =>
        fieldName.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("MeshAsset", StringComparison.OrdinalIgnoreCase) ||
        fieldName.Contains("Mesh", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyMeshAssetName(string assetName)
    {
        var name = assetName.Replace('\\', '/');
        return name.Contains("_mesh", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("/mesh/", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("meshasset", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("rigidmesh", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("compositemesh", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("skinnedmesh", StringComparison.OrdinalIgnoreCase);
    }
}
