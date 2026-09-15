using Permafrost.Core.Commands;
using Permafrost.Core.Ebx;

namespace Permafrost.Core.Scene;

public static class SceneTransformEditor
{
    public static void Apply(SceneNode node, TransformSnapshot value)
    {
        if (node.Transform is not { } transform)
            throw new InvalidOperationException("The selected node has no transform.");

        if (node.IsEditorOnly)
        {
            ApplyValues(transform, value);
            return;
        }

        if (node.Document == null || transform.RightStruct == null || transform.UpStruct == null ||
            transform.ForwardStruct == null || transform.TranslationStruct == null)
            throw new InvalidOperationException("The selected node does not have a writable Frostbite LinearTransform.");

        var rx = value.RotationX * Math.PI / 180.0;
        var ry = value.RotationY * Math.PI / 180.0;
        var rz = value.RotationZ * Math.PI / 180.0;
        var cx = Math.Cos(rx); var sx = Math.Sin(rx);
        var cy = Math.Cos(ry); var sy = Math.Sin(ry);
        var cz = Math.Cos(rz); var sz = Math.Sin(rz);

        var right = (
            X: cy * cz * value.ScaleX,
            Y: (sx * sy * cz + cx * sz) * value.ScaleX,
            Z: (-cx * sy * cz + sx * sz) * value.ScaleX);
        var up = (
            X: -cy * sz * value.ScaleY,
            Y: (-sx * sy * sz + cx * cz) * value.ScaleY,
            Z: (cx * sy * sz + sx * cz) * value.ScaleY);
        var forward = (
            X: sy * value.ScaleZ,
            Y: -sx * cy * value.ScaleZ,
            Z: cx * cy * value.ScaleZ);

        PatchVector(node.Document, transform.RightStruct, right.X, right.Y, right.Z);
        PatchVector(node.Document, transform.UpStruct, up.X, up.Y, up.Z);
        PatchVector(node.Document, transform.ForwardStruct, forward.X, forward.Y, forward.Z);
        PatchVector(node.Document, transform.TranslationStruct, value.X, value.Y, value.Z);
        ApplyValues(transform, value);
    }

    private static void ApplyValues(SceneTransform transform, TransformSnapshot value)
    {
        transform.X = value.X;
        transform.Y = value.Y;
        transform.Z = value.Z;
        transform.RotationX = value.RotationX;
        transform.RotationY = value.RotationY;
        transform.RotationZ = value.RotationZ;
        transform.ScaleX = value.ScaleX;
        transform.ScaleY = value.ScaleY;
        transform.ScaleZ = value.ScaleZ;
    }

    private static void PatchVector(EbxDocument document, EbxObject vector, double x, double y, double z)
    {
        document.SetFloat(vector, "x", (float)x);
        document.SetFloat(vector, "y", (float)y);
        document.SetFloat(vector, "z", (float)z);
    }
}
