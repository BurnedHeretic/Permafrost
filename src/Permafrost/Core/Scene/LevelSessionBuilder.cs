using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;

namespace Permafrost.Core.Scene;

/// <summary>
/// Builds the editable Battlefront scene hierarchy by walking level/subworld/layer EBX references.
/// It deliberately follows only references that resolve to level-path EBXs. Object/prefab blueprints
/// are displayed on their placement node but are not recursively expanded until the mesh pipeline lands.
/// </summary>
public sealed class LevelSessionBuilder
{
    private readonly GameDataSource _source;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly LevelSession _session;
    private readonly IProgress<ScanProgress>? _progress;
    private int _loadedAssets;

    private LevelSessionBuilder(GameDataSource source, GameAssetEntry rootAsset, SceneNode root, IProgress<ScanProgress>? progress)
    {
        _source = source;
        _progress = progress;
        _session = new LevelSession { DataSource = source, RootAsset = rootAsset, Root = root };
    }

    public static async Task<LevelSession> BuildAsync(
        GameDataSource source,
        GameAssetEntry rootAsset,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var document = await source.OpenEbxAsync(rootAsset, cancellationToken);
        var root = SceneBuilder.CreateNode(document, document.RootObject, rootAsset);
        root.Name = rootAsset.ShortName;

        var builder = new LevelSessionBuilder(source, rootAsset, root, progress);
        builder._session.RegisterDocument(rootAsset, document);
        builder._visited.Add(rootAsset.NormalizedName);
        await builder.PopulateAssetAsync(rootAsset, document, root, 0, cancellationToken);
        return builder._session;
    }

    private async Task PopulateAssetAsync(
        GameAssetEntry owner,
        EbxDocument document,
        SceneNode parent,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 24)
            return;

        foreach (var objectNode in EnumerateRootObjectNodes(document, owner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parent.Children.Add(objectNode);

            var linkedAssets = ResolveLevelReferences(objectNode.SourceObject).DistinctBy(x => x.NormalizedName).ToArray();
            foreach (var linked in linkedAssets)
            {
                if (!_visited.Add(linked.NormalizedName))
                    continue;

                try
                {
                    var linkedDocument = await _source.OpenEbxAsync(linked, cancellationToken);
                    _session.RegisterDocument(linked, linkedDocument);
                    var linkedRoot = SceneBuilder.CreateNode(linkedDocument, linkedDocument.RootObject, linked);
                    linkedRoot.Name = linked.ShortName;
                    objectNode.Children.Add(linkedRoot);
                    _loadedAssets++;
                    _progress?.Report(new ScanProgress($"Opening level graph: {_loadedAssets:N0} assets"));
                    await PopulateAssetAsync(linked, linkedDocument, linkedRoot, depth + 1, cancellationToken);
                }
                catch (Exception ex)
                {
                    objectNode.Children.Add(new SceneNode
                    {
                        Name = $"Failed to open {linked.ShortName}: {ex.Message}",
                        TypeName = "LoadError",
                        OwnerAsset = linked,
                        IsReferencePlaceholder = true
                    });
                }
            }

            AddUnresolvedExternalReferencePlaceholders(objectNode);
        }
    }

    private IEnumerable<SceneNode> EnumerateRootObjectNodes(EbxDocument document, GameAssetEntry owner)
    {
        var root = document.RootObject;
        if (root.Fields.TryGetValue("Objects", out var objectsValue) && objectsValue is List<object?> objects)
        {
            foreach (var item in objects)
            {
                if (item is EbxPointer { Kind: EbxPointerKind.Internal } pointer &&
                    pointer.InternalObjectIndex >= 0 && pointer.InternalObjectIndex < document.Objects.Count)
                    yield return SceneBuilder.CreateNode(document, document.Objects[pointer.InternalObjectIndex], owner);
            }
            yield break;
        }

        // Some source assets are themselves a placement/reference object rather than a container.
        // Do not duplicate the root object in that case; its fields are still visible in the inspector.
    }

    private IEnumerable<GameAssetEntry> ResolveLevelReferences(EbxObject? obj)
    {
        if (obj == null) yield break;

        foreach (var pointer in EnumerateExternalPointers(obj))
        {
            if (pointer.External == null) continue;
            if (!_source.Assets.TryGetByGuid(pointer.External.FileGuid, out var entry)) continue;
            if (entry.Kind != GameAssetKind.Ebx) continue;
            if (!entry.NormalizedName.StartsWith("levels/", StringComparison.OrdinalIgnoreCase)) continue;
            yield return entry;
        }
    }

    private void AddUnresolvedExternalReferencePlaceholders(SceneNode node)
    {
        if (node.SourceObject == null) return;
        var existing = new HashSet<Guid>();
        foreach (var child in node.Children)
            if (child.OwnerAsset?.FileGuid is { } g) existing.Add(g);

        foreach (var pointer in EnumerateExternalPointers(node.SourceObject))
        {
            if (pointer.External == null || existing.Contains(pointer.External.FileGuid)) continue;
            if (_source.Assets.TryGetByGuid(pointer.External.FileGuid, out var known))
            {
                // Non-level references (typically SpatialPrefabBlueprint/ObjectBlueprint) are useful context.
                if (!known.NormalizedName.StartsWith("levels/", StringComparison.OrdinalIgnoreCase))
                {
                    node.Children.Add(new SceneNode
                    {
                        Name = known.ShortName,
                        TypeName = known.Kind == GameAssetKind.Ebx ? "External EBX" : known.Kind.ToString(),
                        OwnerAsset = known,
                        IsReferencePlaceholder = true
                    });
                }
                continue;
            }

            // Only show an unresolved GUID for reference objects, otherwise large gameplay objects become noisy.
            if (node.TypeName.Contains("ReferenceObjectData", StringComparison.OrdinalIgnoreCase))
            {
                node.Children.Add(new SceneNode
                {
                    Name = $"External {pointer.External.FileGuid}",
                    TypeName = "Unresolved reference",
                    IsReferencePlaceholder = true
                });
            }
        }
    }

    private static IEnumerable<EbxPointer> EnumerateExternalPointers(EbxObject obj)
    {
        var visited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        foreach (var value in obj.Fields.Values)
            foreach (var pointer in EnumerateExternalPointers(value, visited))
                yield return pointer;
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
