using System.Collections.ObjectModel;
using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;

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

    internal EbxObject? TranslationStruct { get; set; }

    public SceneTransform Clone() => (SceneTransform)MemberwiseClone();
}

public sealed class SceneNode
{
    public required string Name { get; set; }
    public required string TypeName { get; init; }
    public EbxObject? SourceObject { get; init; }
    public EbxDocument? Document { get; init; }
    public GameAssetEntry? OwnerAsset { get; init; }
    public SceneTransform? Transform { get; set; }
    public bool IsReferencePlaceholder { get; init; }
    public ObservableCollection<SceneNode> Children { get; } = new();

    public string DisplayText
    {
        get
        {
            var dirty = Document?.IsDirty == true ? " *" : string.Empty;
            var prefix = IsReferencePlaceholder ? "↳ " : string.Empty;
            return string.IsNullOrWhiteSpace(Name) ? $"{prefix}{TypeName}{dirty}" : $"{prefix}{Name}  [{TypeName}]{dirty}";
        }
    }

    public override string ToString() => DisplayText;
}

public sealed record PropertyRow(string Name, string Value);
