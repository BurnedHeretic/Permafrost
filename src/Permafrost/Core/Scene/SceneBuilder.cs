using System.Numerics;
using Permafrost.Core.Ebx;
using Permafrost.Core.Assets;

namespace Permafrost.Core.Scene;

public static class SceneBuilder
{
    public static SceneNode Build(EbxDocument document)
    {
        if (document.Objects.Count == 0)
            throw new InvalidDataException("EBX has no objects.");

        var rootObject = document.RootObject;
        var root = CreateNode(document, rootObject);

        if (rootObject.Fields.TryGetValue("Objects", out var value) && value is List<object?> objects)
        {
            foreach (var item in objects)
            {
                if (item is EbxPointer { Kind: EbxPointerKind.Internal } pointer &&
                    pointer.InternalObjectIndex >= 0 && pointer.InternalObjectIndex < document.Objects.Count)
                {
                    root.Children.Add(CreateNode(document, document.Objects[pointer.InternalObjectIndex]));
                }
            }
        }

        return root;
    }

    public static IEnumerable<SceneNode> Flatten(SceneNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var nested in Flatten(child))
                yield return nested;
    }

    public static SceneNode CreateNode(EbxDocument document, EbxObject obj, GameAssetEntry? ownerAsset = null)
    {
        var name = GetNodeName(obj);
        return new SceneNode
        {
            Name = name,
            TypeName = obj.ClassName,
            SourceObject = obj,
            Document = document,
            OwnerAsset = ownerAsset,
            Transform = TryReadTransform(obj)
        };
    }

    public static string GetNodeName(EbxObject obj)
    {
        if (obj.Get<string>("Name") is { Length: > 0 } name)
            return ShortName(name);
        if (obj.Get<string>("BundleName") is { Length: > 0 } bundle)
            return ShortName(bundle);

        if (obj.Fields.TryGetValue("Blueprint", out var blueprintValue) &&
            blueprintValue is EbxPointer { Kind: EbxPointerKind.External, External: not null } blueprint)
        {
            return $"Blueprint {blueprint.External!.FileGuid.ToString()[..8]}";
        }

        return $"Object {obj.ObjectIndex}";
    }

    public static string ShortName(string value)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx >= 0 ? normalized[(idx + 1)..] : normalized;
    }

    public static SceneTransform? TryReadTransform(EbxObject obj)
    {
        EbxObject? transform = null;
        foreach (var candidate in new[] { "BlueprintTransform", "Transform", "WorldTransform", "LocalTransform" })
        {
            transform = obj.Get<EbxObject>(candidate);
            if (IsLinearTransform(transform)) break;
            transform = null;
        }

        transform ??= FindLinearTransform(obj, new HashSet<EbxObject>(ReferenceEqualityComparer.Instance), 0);
        if (transform == null) return null;

        var right = ReadVector(transform.Get<EbxObject>("right"));
        var up = ReadVector(transform.Get<EbxObject>("up"));
        var forward = ReadVector(transform.Get<EbxObject>("forward"));
        var transStruct = transform.Get<EbxObject>("trans");
        var trans = ReadVector(transStruct);

        if (right == null || up == null || forward == null || trans == null)
            return null;

        var r = right.Value;
        var u = up.Value;
        var f = forward.Value;
        var t = trans.Value;

        var sx = r.Length();
        var sy = u.Length();
        var sz = f.Length();

        // Match Frosty's LinearTransform presentation closely.
        var t1 = Math.Atan2(f.Y, f.Z);
        var c2 = Math.Sqrt(r.X * r.X + u.X * u.X);
        var t2 = Math.Atan2(-f.X, c2);
        var s1 = Math.Sin(t1);
        var c1 = Math.Cos(t1);
        var t3 = Math.Atan2(s1 * r.Z - c1 * r.Y, c1 * u.Y - s1 * u.Z);
        const double radToDeg = 180.0 / Math.PI;

        return new SceneTransform
        {
            X = t.X,
            Y = t.Y,
            Z = t.Z,
            RotationX = -t1 * radToDeg,
            RotationY = -t2 * radToDeg,
            RotationZ = -t3 * radToDeg,
            ScaleX = sx,
            ScaleY = sy,
            ScaleZ = sz,
            LinearTransformStruct = transform,
            RightStruct = transform.Get<EbxObject>("right"),
            UpStruct = transform.Get<EbxObject>("up"),
            ForwardStruct = transform.Get<EbxObject>("forward"),
            TranslationStruct = transStruct
        };
    }

    private static EbxObject? FindLinearTransform(EbxObject obj, HashSet<EbxObject> visited, int depth)
    {
        if (depth > 4 || !visited.Add(obj)) return null;
        if (IsLinearTransform(obj)) return obj;

        foreach (var value in obj.Fields.Values)
        {
            if (value is EbxObject child)
            {
                var found = FindLinearTransform(child, visited, depth + 1);
                if (found != null) return found;
            }
            else if (value is List<object?> list)
            {
                foreach (var item in list.OfType<EbxObject>())
                {
                    var found = FindLinearTransform(item, visited, depth + 1);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    private static bool IsLinearTransform(EbxObject? obj) =>
        obj != null && obj.Fields.ContainsKey("right") && obj.Fields.ContainsKey("up") &&
        obj.Fields.ContainsKey("forward") && obj.Fields.ContainsKey("trans");

    private static Vector3? ReadVector(EbxObject? obj)
    {
        if (obj == null) return null;
        if (obj.Fields.TryGetValue("x", out var x) && x is float xf &&
            obj.Fields.TryGetValue("y", out var y) && y is float yf &&
            obj.Fields.TryGetValue("z", out var z) && z is float zf)
            return new Vector3(xf, yf, zf);
        return null;
    }

    public static IReadOnlyList<PropertyRow> GetPropertyRows(SceneNode node)
    {
        if (node.SourceObject == null)
            return Array.Empty<PropertyRow>();

        var rows = new List<PropertyRow>();
        foreach (var pair in node.SourceObject.Fields)
            rows.Add(new PropertyRow(pair.Key, FormatValue(pair.Value)));
        return rows;
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "(null)",
        string s => s,
        EbxPointer pointer => pointer.ToString(),
        EbxObject obj => $"<{obj.ClassName}>",
        List<object?> list => $"[{list.Count} items]",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
