using System.Collections.Concurrent;
using Permafrost.Core.Ebx;
using Permafrost.Core.Frostbite;
using Permafrost.Core.Workspace;

namespace Permafrost.Core.Assets;

public sealed record ScanProgress(string Stage, int Completed = 0, int Total = 0)
{
    public string Display => Total > 0 ? $"{Stage} ({Completed:N0}/{Total:N0})" : Stage;
}

public sealed class ScanDiagnostics
{
    public int BundlesAttempted { get; internal set; }
    public int BundlesIndexed { get; internal set; }
    public int BundlesFailed { get; internal set; }
    public List<string> Warnings { get; } = new();
    public int CachedEbxGuidsLoaded { get; internal set; }
}

public sealed class GameDataSource
{
    private readonly ConcurrentDictionary<string, EbxDocument> _documentCache = new(StringComparer.OrdinalIgnoreCase);

    public required FrostbiteInstallLayout Layout { get; init; }
    public required GameAssetIndex Assets { get; init; }
    public required ScanDiagnostics Diagnostics { get; init; }
    public WorkspaceManager? Workspace { get; set; }

    public static async Task<GameDataSource> ScanAsync(
        string installRoot,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layoutProgress = progress == null ? null : new Progress<string>(s => progress.Report(new ScanProgress(s)));
        var layout = await GameLayoutReader.ReadAsync(installRoot, layoutProgress, cancellationToken);
        var index = new GameAssetIndex();
        var diagnostics = new ScanDiagnostics();

        if (!layout.Manifest.UsesBundleAggregationMap)
        {
            diagnostics.Warnings.Add(
                "The BF2 bundle aggregation map was unavailable, so the editor skipped the expensive legacy bundle-header probe. " +
                (string.IsNullOrWhiteSpace(layout.Manifest.BundleAggregationError)
                    ? "See the scan status for the compatibility-map issue."
                    : layout.Manifest.BundleAggregationError));
            return new GameDataSource
            {
                Layout = layout,
                Assets = index,
                Diagnostics = diagnostics
            };
        }

        progress?.Report(new ScanProgress("Indexing bundle headers...", 0, layout.Manifest.Bundles.Count));
        var completed = 0;
        var attempted = 0;
        var indexed = 0;
        var failed = 0;
        var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        var sync = new object();
        var tasks = layout.Manifest.Bundles.Select(async bundle =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref attempted);
                try
                {
                    var entries = await BundleReader.ReadAssetsAsync(layout, bundle, cancellationToken);
                    lock (sync)
                    {
                        foreach (var entry in entries) index.Add(entry);
                    }
                    Interlocked.Increment(ref indexed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    lock (diagnostics.Warnings)
                    {
                        if (diagnostics.Warnings.Count < 100)
                            diagnostics.Warnings.Add($"Bundle 0x{bundle.Hash:X8}: {ex.Message}");
                    }
                }
            }
            finally
            {
                gate.Release();
                var now = Interlocked.Increment(ref completed);
                if (now % 100 == 0 || now == layout.Manifest.Bundles.Count)
                    progress?.Report(new ScanProgress("Indexing bundle headers...", now, layout.Manifest.Bundles.Count));
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        diagnostics.BundlesAttempted = attempted;
        diagnostics.BundlesIndexed = indexed;
        diagnostics.BundlesFailed = failed;

        if (index.EbxCount == 0)
        {
            diagnostics.Warnings.Add("No EBX entries were indexed even though the BF2 aggregation map validated. This points to CAS block decoding or bundle-header parsing rather than the aggregation mapping; inspect the per-bundle warnings above.");
        }

        // Frosty keeps an EBX FileGuid lookup for the entire asset database. Load Permafrost's
        // persisted equivalent before the level-only pass so arbitrary Blueprint/MeshAsset imports
        // can resolve immediately on subsequent launches.
        diagnostics.CachedEbxGuidsLoaded = EbxGuidIndexCache.TryLoad(index, layout.Manifest.Bundles.Count);
        if (diagnostics.CachedEbxGuidsLoaded > 0)
            progress?.Report(new ScanProgress($"Loaded {diagnostics.CachedEbxGuidsLoaded:N0} cached EBX GUIDs."));

        return new GameDataSource
        {
            Layout = layout,
            Assets = index,
            Diagnostics = diagnostics
        };
    }

    public Task BuildLevelGuidIndexAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        BuildEbxGuidIndexAsync(levelsOnly: true, progress, cancellationToken);

    public async Task BuildEbxGuidIndexAsync(
        bool levelsOnly,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = (levelsOnly
                ? Assets.LevelAssets
                : Assets.UniqueEntries.Where(x => x.Kind == GameAssetKind.Ebx))
            .Where(x => !x.FileGuid.HasValue)
            .ToArray();
        if (entries.Length == 0) return;

        var completed = 0;
        var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, 12));
        var stage = levelsOnly ? "Indexing level EBX GUIDs..." : "Deep-indexing all EBX GUIDs...";
        progress?.Report(new ScanProgress(stage, 0, entries.Length));
        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                try
                {
                    var raw = await Layout.ReadRawSegmentAsync(entry.Storage, 131072, cancellationToken);
                    if (EbxV4Reader.TryReadFileGuid(raw, out var directGuid))
                    {
                        Assets.RegisterGuid(entry, directGuid);
                    }
                    else
                    {
                        var header = CasBlockCodec.Decompress(raw, 64);
                        if (EbxV4Reader.TryReadFileGuid(header, out var guid))
                            Assets.RegisterGuid(entry, guid);
                    }
                }
                catch
                {
                    // GUID indexing is best-effort; opening the asset itself will produce a useful error later.
                }
            }
            finally
            {
                gate.Release();
                var now = Interlocked.Increment(ref completed);
                if (now % 250 == 0 || now == entries.Length)
                    progress?.Report(new ScanProgress(stage, now, entries.Length));
            }
        }).ToArray();
        await Task.WhenAll(tasks);

        if (!levelsOnly)
        {
            progress?.Report(new ScanProgress("Saving EBX GUID cache..."));
            EbxGuidIndexCache.Save(Assets, Layout.Manifest.Bundles.Count);
        }
    }


    /// <summary>
    /// Index only EBX headers that live in the supplied bundle hashes. This is the fast path used
    /// by the native scene renderer: level references tend to pull their prefab/mesh blueprints
    /// from a relatively small set of bundles, so we avoid a multi-million-entry global GUID pass.
    /// </summary>
    public async Task BuildGuidIndexForBundlesAsync(
        IEnumerable<uint> bundleHashes,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = Assets.GetUniqueEbxForBundles(bundleHashes)
            .Where(x => !x.FileGuid.HasValue)
            .ToArray();
        if (entries.Length == 0) return;

        var completed = 0;
        var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, 12));
        progress?.Report(new ScanProgress("Indexing local render GUIDs...", 0, entries.Length));
        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await TryIndexGuidAsync(entry, cancellationToken);
            }
            finally
            {
                gate.Release();
                var now = Interlocked.Increment(ref completed);
                if (now % 100 == 0 || now == entries.Length)
                    progress?.Report(new ScanProgress("Indexing local render GUIDs...", now, entries.Length));
            }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task TryIndexGuidAsync(GameAssetEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await Layout.ReadRawSegmentAsync(entry.Storage, 131072, cancellationToken);
            if (EbxV4Reader.TryReadFileGuid(raw, out var directGuid))
            {
                Assets.RegisterGuid(entry, directGuid);
                return;
            }

            var header = CasBlockCodec.Decompress(raw, 64);
            if (EbxV4Reader.TryReadFileGuid(header, out var guid))
                Assets.RegisterGuid(entry, guid);
        }
        catch
        {
            // Render GUID indexing is best effort. The resolver records unresolved chains.
        }
    }

    public async Task<byte[]> ReadResAsync(
        GameAssetEntry entry,
        int? neededBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (entry.Kind != GameAssetKind.Res)
            throw new InvalidOperationException($"Asset '{entry.Name}' is not RES.");
        var raw = await Layout.ReadRawSegmentAsync(entry.Storage, null, cancellationToken);
        int? targetSize = neededBytes;
        if (!targetSize.HasValue && entry.OriginalSize != 0)
            targetSize = checked((int)entry.OriginalSize);
        return CasBlockCodec.Decompress(raw, targetSize);
    }

    public async Task<byte[]> ReadChunkAsync(
        Guid chunkId,
        int? neededBytes = null,
        CancellationToken cancellationToken = default)
    {
        ManifestFileRef storage;
        if (Layout.Manifest.Chunks.TryGetValue(chunkId, out var chunk))
        {
            storage = chunk.Storage;
        }
        else if (Assets.TryGetChunkById(chunkId, out var bundleChunk))
        {
            storage = bundleChunk.Storage;
            // Bundle chunk logicalSize/logicalOffset describe Frostbite logical ranges, not a
            // trustworthy decompressed CAS byte count for every SWBF2 chunk variant. Read the
            // complete aggregation-mapped segment unless the caller explicitly requests a cap.
        }
        else
        {
            throw new KeyNotFoundException($"Chunk '{chunkId}' is not present in either the global BF2 manifest or indexed bundle-local chunk tables.");
        }

        var raw = await Layout.ReadRawSegmentAsync(storage, null, cancellationToken);
        return CasBlockCodec.Decompress(raw, neededBytes);
    }

    public async Task<EbxDocument> OpenEbxAsync(GameAssetEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.Kind != GameAssetKind.Ebx)
            throw new InvalidOperationException($"Asset '{entry.Name}' is not EBX.");

        if (_documentCache.TryGetValue(entry.NormalizedName, out var cached))
            return cached;

        byte[] data;
        if (Workspace != null && Workspace.TryReadOverlay(entry.Name, out var overlay))
        {
            data = overlay;
        }
        else
        {
            var raw = await Layout.ReadRawSegmentAsync(entry.Storage, null, cancellationToken);
            if (EbxV4Reader.TryReadFileGuid(raw, out _))
                data = raw;
            else
                data = CasBlockCodec.Decompress(raw, entry.OriginalSize == 0 ? null : checked((int)entry.OriginalSize));
        }

        var document = EbxV4Reader.Read(data, entry.Name);
        entry.FileGuid = document.FileGuid;
        Assets.RegisterGuid(entry, document.FileGuid);
        _documentCache[entry.NormalizedName] = document;
        return document;
    }

    public async Task<EbxDocument> OpenEbxByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!Assets.TryGetByName(name, out var entry))
            throw new KeyNotFoundException($"EBX '{name}' is not present in the current game index.");
        return await OpenEbxAsync(entry, cancellationToken);
    }

    public void ClearDocumentCache() => _documentCache.Clear();
}
