namespace Permafrost.Core.Frostbite;

public static class GameLayoutReader
{
    public static async Task<FrostbiteInstallLayout> ReadAsync(
        string installRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        installRoot = ResolveInstallRoot(installRoot);
        var dataDir = Path.Combine(installRoot, "Data");
        var patchDir = Path.Combine(installRoot, "Patch");
        var baseLayoutPath = Path.Combine(dataDir, "layout.toc");
        var patchLayoutPath = Path.Combine(patchDir, "layout.toc");
        var layoutPath = File.Exists(patchLayoutPath) ? patchLayoutPath : baseLayoutPath;

        progress?.Report($"Reading {(File.Exists(patchLayoutPath) ? "Patch" : "Data")}/layout.toc...");
        var root = DbObjectReader.ReadLayoutRoot(layoutPath);
        var catalogs = ReadCatalogNames(root, installRoot);
        if (catalogs.Count == 0)
            throw new InvalidDataException("No installed Win32 catalogs were found in layout.toc.");

        var catalogMetadata = new Dictionary<string, FrostbiteCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedCatalog = catalog.Replace('/', Path.DirectorySeparatorChar);
            var patchCat = Path.Combine(patchDir, normalizedCatalog, "cas.cat");
            var dataCat = Path.Combine(dataDir, normalizedCatalog, "cas.cat");
            var selected = File.Exists(patchCat) ? patchCat : dataCat;
            if (!File.Exists(selected))
            {
                progress?.Report($"Catalog path not present as cas.cat ({catalog}); keeping manifest slot for index compatibility.");
                continue;
            }
            try
            {
                progress?.Report($"Reading catalog {catalog}...");
                catalogMetadata[catalog] = CatalogReader.Read(selected);
            }
            catch (Exception ex)
            {
                progress?.Report($"Catalog warning ({catalog}): {ex.Message}");
            }
        }

        var layoutShell = new FrostbiteInstallLayout
        {
            InstallRoot = installRoot,
            LayoutRoot = root,
            Manifest = new FrostbiteManifest()
        };
        layoutShell.Catalogs.AddRange(catalogs);
        foreach (var pair in catalogMetadata) layoutShell.CatalogMetadata[pair.Key] = pair.Value;

        progress?.Report("Reading bundle manifest...");
        var manifest = await ReadManifestAsync(layoutShell, root, progress, cancellationToken);

        var result = new FrostbiteInstallLayout
        {
            InstallRoot = installRoot,
            LayoutRoot = root,
            Manifest = manifest
        };
        // ReadManifestAsync may replace the provisional layout catalog list with the
        // exact 23-entry catalog order carried by the BF2 aggregation table. Use that
        // validated order for all manifest/CAS references from this point onward.
        result.Catalogs.AddRange(layoutShell.Catalogs);
        foreach (var pair in catalogMetadata) result.CatalogMetadata[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>
    /// Accepts either the Battlefront II root folder or a folder somewhere inside it
    /// (for example Data, Patch, Data\Win32, or Patch\Win32). This avoids a very
    /// common folder-picker mistake where the user selects Data rather than the game root.
    /// </summary>
    public static string ResolveInstallRoot(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new DirectoryNotFoundException("No Battlefront II folder was selected.");

        var selected = new DirectoryInfo(Path.GetFullPath(selectedPath));
        if (!selected.Exists)
            throw new DirectoryNotFoundException($"The selected folder does not exist: {selected.FullName}");

        static bool IsGameRoot(DirectoryInfo dir) =>
            File.Exists(Path.Combine(dir.FullName, "Data", "layout.toc"));

        // 1) Exact game root.
        if (IsGameRoot(selected))
            return selected.FullName;

        // 2) User selected Data itself.
        if (selected.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(selected.FullName, "layout.toc")) &&
            selected.Parent is { } dataParent && IsGameRoot(dataParent))
            return dataParent.FullName;

        // 3) User selected Patch itself or anything below Data/Patch/Win32.
        var current = selected;
        for (var i = 0; i < 6 && current != null; i++, current = current.Parent)
        {
            if (IsGameRoot(current))
                return current.FullName;
        }

        // 4) User selected a library folder such as EA Games / Origin Games / steamapps/common.
        // Only inspect immediate children so we never recursively crawl a drive.
        try
        {
            foreach (var child in selected.EnumerateDirectories())
            {
                if (IsGameRoot(child))
                    return child.FullName;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The detailed error below is more useful than failing while probing children.
        }

        var checkedPaths = new List<string>
        {
            Path.Combine(selected.FullName, "Data", "layout.toc"),
            Path.Combine(selected.FullName, "layout.toc")
        };
        if (selected.Parent != null)
            checkedPaths.Add(Path.Combine(selected.Parent.FullName, "Data", "layout.toc"));

        throw new DirectoryNotFoundException(
            "Could not locate the Battlefront II install root from the selected folder. " +
            "The editor expects the game root to contain Data\\layout.toc.\n\n" +
            $"Selected folder: {selected.FullName}\n" +
            "Checked:\n  " + string.Join("\n  ", checkedPaths) + "\n\n" +
            "Select the folder that contains the Data and Patch folders (normally the folder that also contains starwarsbattlefrontii.exe)."
        );
    }

    private static List<string> ReadCatalogNames(DbValue root, string installRoot)
    {
        // IMPORTANT: do not require a catalog to exist under Data before adding it.
        // Battlefront II installations can have catalog content supplied by Patch, and
        // some EA App layouts expose physical CAS paths through installChunk.files.
        // Filtering here caused perfectly valid installs to produce an empty catalog list.
        var result = new List<string>();
        var installManifest = root.Require("installManifest");
        var chunks = installManifest.Require("installChunks").AsList();

        foreach (var chunkValue in chunks)
        {
            var chunk = chunkValue.AsObject();
            if (!chunk.TryGetValue("name", out var nameValue))
                continue;

            var name = nameValue.AsString().TrimEnd('\0').Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Frosty's BF2 path is "win32/" + installChunk.name.  Be tolerant if
            // a layout already contains the prefix.
            var relative = name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase)
                ? name
                : "Win32/" + name;

            relative = relative.Replace('\\', '/');

            // Preserve layout order. Manifest file references encode the catalog index,
            // so removing a missing/base-only entry here can shift every later index.
            result.Add(relative);
        }

        // Diagnostic fallback for unusual installs: if the layout had no usable names,
        // discover catalog folders physically. This is only a fallback because physical
        // enumeration cannot reliably reconstruct manifest catalog indices.
        if (result.Count == 0)
        {
            foreach (var rootName in new[] { "Patch", "Data" })
            {
                var win32 = Path.Combine(installRoot, rootName, "Win32");
                if (!Directory.Exists(win32))
                    continue;

                foreach (var cat in Directory.EnumerateFiles(win32, "cas.cat", SearchOption.AllDirectories))
                {
                    var dir = Path.GetDirectoryName(cat)!;
                    var baseDir = Path.Combine(installRoot, rootName);
                    var relative = Path.GetRelativePath(baseDir, dir).Replace('\\', '/');
                    if (!result.Contains(relative, StringComparer.OrdinalIgnoreCase))
                        result.Add(relative);
                }
            }
        }

        return result;
    }

    private static async Task<FrostbiteManifest> ReadManifestAsync(
        FrostbiteInstallLayout layout,
        DbValue root,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var manifestObject = root.Require("manifest").AsObject();
        var fileRef = manifestObject["file"].AsUInt32();
        var offset = manifestObject["offset"].AsUInt32();
        var size = manifestObject["size"].AsUInt32();
        var manifestStorage = new ManifestFileRef(fileRef, offset, size);
        var raw = await layout.ReadRawSegmentAsync(manifestStorage, null, cancellationToken);

        using var ms = new MemoryStream(raw, writable: false);
        using var reader = new BinaryReader(ms);
        var fileCount = reader.ReadUInt32();
        var bundleCount = reader.ReadUInt32();
        var chunkCount = reader.ReadUInt32();

        var result = new FrostbiteManifest { ChunkCount = checked((int)chunkCount) };
        for (var i = 0; i < fileCount; i++)
            result.Files.Add(new ManifestFileRef(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64()));

        for (var i = 0; i < bundleCount; i++)
        {
            var bundle = new ManifestBundleDescriptor
            {
                Hash = reader.ReadUInt32(),
                StartIndex = reader.ReadUInt32(),
                Count = reader.ReadUInt32(),
                Unknown1 = reader.ReadUInt32(),
                Unknown2 = reader.ReadUInt32()
            };

            var start = checked((int)bundle.StartIndex);
            var count = checked((int)bundle.Count);
            if (start >= 0 && start < result.Files.Count && count > 0)
            {
                foreach (var file in result.Files.Skip(start).Take(Math.Min(count, result.Files.Count - start)))
                    bundle.Files.Add(file);
            }
            result.Bundles.Add(bundle);
        }

        // Battlefront II's public manifest StartIndex/Count mapping is not sufficient to locate
        // the physical files for each logical bundle. Acquire the BF2 aggregation map and validate
        // every bundle hash before replacing the provisional mapping above.
        try
        {
            var hashes = result.Bundles.Select(x => x.Hash).ToArray();
            var aggregation = await BundleAggregationMap.LoadOrAcquireAsync(hashes, progress, cancellationToken);
            for (var i = 0; i < result.Bundles.Count; i++)
            {
                var target = result.Bundles[i];
                target.Files.Clear();
                target.Files.AddRange(aggregation.Bundles[i].Files);
            }

            // Critical BF2 detail: the aggregation database carries the catalog index
            // order used by its ManifestFileRef values. It contains 23 catalogs and
            // intentionally omits layout-only slots such as installation/default.
            // Using the raw 24-entry installManifest list shifts every catalog index by
            // one and sends bundle reads to the wrong CAS files.
            layout.Catalogs.Clear();
            layout.Catalogs.AddRange(aggregation.Catalogs.Select(x => x.TrimEnd('\0')));

            result.UsesBundleAggregationMap = true;
            result.BundleAggregationSource = "Validated BF2 bundle aggregation map";
            result.BundleAggregationError = null;
            progress?.Report($"BF2 bundle aggregation map loaded and validated ({result.Bundles.Count:N0} bundles).");
        }
        catch (Exception ex)
        {
            result.UsesBundleAggregationMap = false;
            result.BundleAggregationSource = "Manifest fallback (aggregation map unavailable)";
            result.BundleAggregationError = ex.Message;
            progress?.Report($"Bundle aggregation compatibility warning: {ex.Message}");
        }

        // Chunk records are useful later for RES/chunk editing. Keep the stream position validation here
        // but do not index them as level assets in v0.02.
        for (var i = 0; i < chunkCount && reader.BaseStream.Position + 20 <= reader.BaseStream.Length; i++)
        {
            _ = new Guid(reader.ReadBytes(16));
            _ = reader.ReadInt32();
        }

        return result;
    }
}
