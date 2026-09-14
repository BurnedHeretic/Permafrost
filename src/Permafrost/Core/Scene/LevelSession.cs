using Permafrost.Core.Assets;
using Permafrost.Core.Ebx;

namespace Permafrost.Core.Scene;

public sealed class LevelSession
{
    private readonly Dictionary<string, EbxDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

    public required GameDataSource DataSource { get; init; }
    public required GameAssetEntry RootAsset { get; init; }
    public required SceneNode Root { get; init; }

    public IReadOnlyDictionary<string, EbxDocument> Documents => _documents;

    internal void RegisterDocument(GameAssetEntry entry, EbxDocument document) =>
        _documents[entry.NormalizedName] = document;

    public IEnumerable<SceneNode> Flatten() => SceneBuilder.Flatten(Root);

    public IEnumerable<(string AssetName, EbxDocument Document)> DirtyDocuments =>
        _documents.Where(x => x.Value.IsDirty).Select(x => (x.Key, x.Value));

    public bool HasDirtyDocuments => _documents.Values.Any(x => x.IsDirty);

    public async Task<int> SaveDirtyAsync(CancellationToken cancellationToken = default)
    {
        if (DataSource.Workspace == null)
            throw new InvalidOperationException("No workspace has been configured for this game install.");

        var dirty = DirtyDocuments.ToArray();
        foreach (var item in dirty)
            await DataSource.Workspace.SaveDocumentAsync(item.AssetName, item.Document, cancellationToken);
        return dirty.Length;
    }
}
