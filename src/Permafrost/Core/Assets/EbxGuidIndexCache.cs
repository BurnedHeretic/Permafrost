namespace Permafrost.Core.Assets;

/// <summary>
/// Persistent FileGuid -> EBX-name cache. Frosty builds a complete EBX GUID table during asset
/// initialization; Permafrost now does the same once, then reuses it on later launches so external
/// PointerRefs (blueprints, meshes, materials, etc.) can resolve without a repeated deep CAS pass.
/// </summary>
public static class EbxGuidIndexCache
{
    private const string Magic = "PFEbxGuidIndexV1";

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Permafrost", "Cache", "EbxGuidIndex.bin");

    public static int TryLoad(GameAssetIndex index, int manifestBundleCount)
    {
        try
        {
            if (!File.Exists(CachePath)) return 0;
            using var stream = File.OpenRead(CachePath);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal)) return 0;

            var bundleCount = reader.ReadInt32();
            var uniqueEbxCount = reader.ReadInt32();
            var recordCount = reader.ReadInt32();
            if (bundleCount != manifestBundleCount || uniqueEbxCount != index.UniqueEbxCount || recordCount < 0)
                return 0;

            var loaded = 0;
            for (var i = 0; i < recordCount; i++)
            {
                var name = reader.ReadString();
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16) return 0;
                var guid = new Guid(bytes);
                if (guid == Guid.Empty) continue;
                if (index.TryGetByName(name, out var entry) && entry.Kind == GameAssetKind.Ebx)
                {
                    index.RegisterGuid(entry, guid);
                    loaded++;
                }
            }
            return loaded;
        }
        catch
        {
            return 0;
        }
    }

    public static void Save(GameAssetIndex index, int manifestBundleCount)
    {
        try
        {
            var records = index.UniqueEntries
                .Where(x => x.Kind == GameAssetKind.Ebx && x.FileGuid is { } g && g != Guid.Empty)
                .OrderBy(x => x.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var dir = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(dir);
            var temp = CachePath + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(manifestBundleCount);
                writer.Write(index.UniqueEbxCount);
                writer.Write(records.Length);
                foreach (var entry in records)
                {
                    writer.Write(entry.Name);
                    writer.Write(entry.FileGuid!.Value.ToByteArray());
                }
            }
            File.Move(temp, CachePath, overwrite: true);
        }
        catch
        {
            // A cache failure must never block editing. The next session can rebuild it.
        }
    }
}
