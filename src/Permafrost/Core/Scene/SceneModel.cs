using System.Collections.ObjectModel;
using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;
using Permafrost.Core.Rendering;

namespace Permafrost.Core.Scene;

public sealed class SceneTransform
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double ScaleZ { get; set; } = 1;
    public bool IsEditorOnly { get; set; }

    internal EbxObject? LinearTransformStruct { get; set; }
    internal EbxObject? RightStruct { get; set; }
    internal EbxObject? UpStruct { get; set; }
    internal EbxObject? ForwardStruct { get; set; }
    internal EbxObject? TranslationStruct { get; set; }

    public bool IsFullyEditable => IsEditorOnly ||
        (LinearTransformStruct != null && RightStruct != null && UpStruct != null &&
         ForwardStruct != null && TranslationStruct != null);

    public SceneTransform Clone() => (SceneTransform)MemberwiseClone();
}

public sealed class SceneNode
{
    public required string Name { get; set; }
    public required string TypeName { get; init; }
    public EbxObject? SourceObject { get; init; }
    public EbxDocument? Document { get; init; }
    public GameAssetEntry? OwnerAsset { get; init; }
    public GameAssetEntry? ReferencedAsset { get; init; }
    public SceneTransform? Transform { get; set; }
    public NativeMeshInfo? NativeMesh { get; set; }
    public bool IsReferencePlaceholder { get; init; }
    public bool IsEditorOnly { get; init; }
    public bool IsImportedPlacement { get; init; }
    public ObservableCollection<SceneNode> Children { get; } = new();

    public string DisplayText
    {
        get
        {
            var dirty = Document?.IsDirty == true ? " *" : string.Empty;
            var prefix = IsReferencePlaceholder ? "↳ " : string.Empty;
            var marker = IsEditorOnly ? "[STAGED] " : IsImportedPlacement ? "[IMPORTED] " : string.Empty;
            return string.IsNullOrWhiteSpace(Name) ? $"{prefix}{marker}{TypeName}{dirty}" : $"{prefix}{marker}{Name}  [{TypeName}]{dirty}";
        }
    }

    public override string ToString() => DisplayText;
}

public sealed record PropertyRow(string Name, string Value);
