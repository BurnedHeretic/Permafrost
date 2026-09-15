using System.Numerics;
using Permafrost.Core.Assets;
using Permafrost.Core.Commands;
using Permafrost.Core.Ebx;
using Permafrost.Core.Rendering;

namespace Permafrost.Core.Scene;

public sealed record LevelLayerTarget(GameAssetEntry Asset, EbxDocument Document)
{
    public string DisplayName => $"{Asset.ShortName}  [{Document.RootObject.ClassName}]";
    public override string ToString() => DisplayName;
}

/// <summary>
/// Append-only structural placement import for self-describing BF2 EBX v4 LayerData.
/// The first supported production path is SpatialPrefabBlueprint -> SpatialPrefabReferenceObjectData,
/// which covers a large share of static level props/architecture without requiring Frosty's SDK.
/// </summary>
public static class LevelObjectImporter
{
    public static IReadOnlyList<LevelLayerTarget> GetTargets(LevelSession session)
    {
        var targets = new List<LevelLayerTarget>();
        foreach (var pair in session.Documents)
        {
            var document = pair.Value;
            if (document.RootObject.Fields.TryGetValue("Objects", out var objects) && objects is List<object?> &&
                session.DataSource.Assets.TryGetByName(pair.Key, out var asset))
            {
                targets.Add(new LevelLayerTarget(asset, document));
            }
        }

        return targets
            .OrderByDescending(x => x.Document.RootObject.ClassName.Contains("LayerData", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<ImportedPlacement> PrepareAsync(
        LevelSession session,
        LevelLayerTarget target,
        GameAssetEntry sourceAsset,
        NativeMeshInfo? nativeMesh,
        Vector3 placementPoint,
        CancellationToken cancellationToken = default)
    {
        if (target.Document.Magic != 0x0FB4D1CE)
            throw new NotSupportedException("Real placement import currently targets BF2 EBX v4 LayerData only.");
        if (target.Document.BoxedValueCount != 0)
            throw new NotSupportedException("This target LayerData contains boxed values that Permafrost cannot structurally rewrite yet. Choose another layer.");
        if (target.Document.RootObject.Fields.TryGetValue("Objects", out var objectsValue) is false || objectsValue is not List<object?> rootObjects)
            throw new InvalidDataException("The selected target does not expose a writable Objects array.");

        var sourceDocument = await session.DataSource.OpenEbxAsync(sourceAsset, cancellationToken);
        var exportedBlueprint = sourceDocument.RootObject.InstanceGuid != Guid.Empty
            ? sourceDocument.RootObject
            : sourceDocument.Objects.FirstOrDefault(x => x.InstanceGuid != Guid.Empty);
        if (exportedBlueprint == null)
            throw new InvalidDataException($"{sourceAsset.Name} has no exported EBX root object that can be referenced by a level placement.");

        var referenceClass = GetReferenceClassName(exportedBlueprint.ClassName);
        if (referenceClass == null)
        {
            throw new NotSupportedException(
                $"Real import currently supports SpatialPrefabBlueprint/ObjectBlueprint assets. '{sourceAsset.Name}' exports {exportedBlueprint.ClassName}. " +
                "You can still PREVIEW/STAGE this asset while additional reference types are added.");
        }

        var template = target.Document.Objects.FirstOrDefault(x =>
            x.ClassName.Equals(referenceClass, StringComparison.OrdinalIgnoreCase));
        if (template == null)
        {
            throw new InvalidDataException(
                $"Target layer '{target.Asset.Name}' has no {referenceClass} template to clone safely. Choose another loaded LayerData target.");
        }
        if (ContainsInternalPointer(template))
        {
            throw new NotSupportedException(
                $"The available {referenceClass} template contains internal object references. Permafrost will not clone it until that reference graph is handled safely.");
        }

        var clone = CloneObject(template, isTopLevel: true);
        var import = new EbxImportReference(sourceDocument.FileGuid, exportedBlueprint.InstanceGuid);
        clone.Fields["Blueprint"] = new EbxPointer
        {
            Kind = EbxPointerKind.External,
            ImportIndex = -1,
            External = import
        };

        var transform = SceneBuilder.TryReadTransform(clone)
            ?? throw new InvalidDataException($"Cloned {referenceClass} does not contain a readable BlueprintTransform.");

        var parentNode = session.Flatten().FirstOrDefault(n =>
            ReferenceEquals(n.Document, target.Document) && ReferenceEquals(n.SourceObject, target.Document.RootObject))
            ?? session.Root;

        var node = new SceneNode
        {
            Name = sourceAsset.ShortName,
            TypeName = referenceClass,
            SourceObject = clone,
            Document = target.Document,
            OwnerAsset = target.Asset,
            ReferencedAsset = sourceAsset,
            Transform = transform,
            NativeMesh = nativeMesh,
            IsImportedPlacement = true
        };

        // Apply the editor placement before attachment. SetFloat automatically switches this
        // document to full model serialization because cloned structs have no retail byte offsets.
        SceneTransformEditor.Apply(node, new TransformSnapshot(
            placementPoint.X, placementPoint.Y, placementPoint.Z,
            0, 0, 0, 1, 1, 1));

        return new ImportedPlacement(target, rootObjects, import, clone, node, parentNode);
    }

    private static string? GetReferenceClassName(string blueprintClass) =>
        blueprintClass.Contains("SpatialPrefabBlueprint", StringComparison.OrdinalIgnoreCase)
            ? "SpatialPrefabReferenceObjectData"
            : blueprintClass.Contains("ObjectBlueprint", StringComparison.OrdinalIgnoreCase)
                ? "ObjectReferenceObjectData"
                : null;

    private static EbxObject CloneObject(EbxObject source, bool isTopLevel = false)
    {
        var clone = new EbxObject(source.Descriptor, source.IsStruct)
        {
            InstanceGuid = isTopLevel ? Guid.Empty : source.InstanceGuid,
            ObjectIndex = -1
        };
        foreach (var pair in source.Fields)
            clone.Fields[pair.Key] = CloneValue(pair.Value);
        return clone;
    }

    private static object? CloneValue(object? value) => value switch
    {
        EbxObject obj => CloneObject(obj),
        EbxPointer p => new EbxPointer
        {
            Kind = p.Kind,
            InternalObjectIndex = p.InternalObjectIndex,
            ImportIndex = p.ImportIndex,
            External = p.External
        },
        List<object?> list => list.Select(CloneValue).ToList(),
        _ => value
    };

    private static bool ContainsInternalPointer(EbxObject obj)
    {
        var visited = new HashSet<EbxObject>(ReferenceEqualityComparer.Instance);
        return ContainsInternalPointerValue(obj, visited);
    }

    private static bool ContainsInternalPointerValue(object? value, HashSet<EbxObject> visited)
    {
        switch (value)
        {
            case EbxPointer { Kind: EbxPointerKind.Internal }:
                return true;
            case EbxObject obj when visited.Add(obj):
                return obj.Fields.Values.Any(x => ContainsInternalPointerValue(x, visited));
            case List<object?> list:
                return list.Any(x => ContainsInternalPointerValue(x, visited));
            default:
                return false;
        }
    }
}

public sealed class ImportedPlacement
{
    private readonly LevelLayerTarget _target;
    private readonly List<object?> _rootObjects;
    private readonly EbxImportReference _import;
    private readonly EbxObject _object;
    private readonly SceneNode _parentNode;
    private EbxPointer? _rootPointer;

    internal ImportedPlacement(
        LevelLayerTarget target,
        List<object?> rootObjects,
        EbxImportReference import,
        EbxObject obj,
        SceneNode node,
        SceneNode parentNode)
    {
        _target = target;
        _rootObjects = rootObjects;
        _import = import;
        _object = obj;
        Node = node;
        _parentNode = parentNode;
    }

    public SceneNode Node { get; }
    public bool IsAttached => _rootPointer != null && _rootObjects.Contains(_rootPointer);

    public void Attach()
    {
        var document = _target.Document;
        var importIndex = document.Imports.FindIndex(x => x == _import);
        if (importIndex < 0)
        {
            document.Imports.Add(_import);
            importIndex = document.Imports.Count - 1;
        }

        if (!document.Objects.Contains(_object))
        {
            _object.ObjectIndex = document.Objects.Count;
            document.Objects.Add(_object);
        }
        else
        {
            _object.ObjectIndex = document.Objects.IndexOf(_object);
        }

        _object.Fields["Blueprint"] = new EbxPointer
        {
            Kind = EbxPointerKind.External,
            ImportIndex = importIndex,
            External = _import
        };

        _rootPointer = new EbxPointer
        {
            Kind = EbxPointerKind.Internal,
            InternalObjectIndex = _object.ObjectIndex
        };
        if (!_rootObjects.Any(x => x is EbxPointer p && p.Kind == EbxPointerKind.Internal && p.InternalObjectIndex == _object.ObjectIndex))
            _rootObjects.Add(_rootPointer);

        if (!_parentNode.Children.Contains(Node))
            _parentNode.Children.Add(Node);

        document.MarkStructureDirty();
    }

    public void Detach()
    {
        var document = _target.Document;
        if (_rootPointer != null)
            _rootObjects.Remove(_rootPointer);
        else
            _rootObjects.RemoveAll(x => x is EbxPointer p && p.Kind == EbxPointerKind.Internal && p.InternalObjectIndex == _object.ObjectIndex);
        _rootPointer = null;
        _parentNode.Children.Remove(Node);

        // Undoing imports in stack order normally makes this the final object, allowing a clean
        // physical removal. If later structural objects depend on its index, leave it unreachable
        // rather than shifting every internal pointer in the document.
        if (document.Objects.Count > 0 && ReferenceEquals(document.Objects[^1], _object))
        {
            document.Objects.RemoveAt(document.Objects.Count - 1);
            _object.ObjectIndex = -1;
        }
        document.MarkStructureDirty();
    }
}
