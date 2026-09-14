namespace Permafrost.Core.Frostbite;

public sealed record CatalogResourceEntry(string Sha1, uint Offset, uint Size, uint LogicalOffset, uint ArchiveIndex);
public sealed record CatalogPatchEntry(string Sha1, string BaseSha1, string DeltaSha1);

public sealed class FrostbiteCatalog
{
    public uint ResourceCount { get; init; }
    public uint PatchCount { get; init; }
    public uint EncryptedCount { get; init; }
    public List<CatalogResourceEntry> Resources { get; } = new();
    public List<CatalogPatchEntry> Patches { get; } = new();
}

public static class CatalogReader
{
    private const string Magic = "NyanNyanNyanNyan";

    public static FrostbiteCatalog Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 0x250) throw new InvalidDataException($"Catalog is too small: {path}");
        file.Position = 0x22C;
        using var reader = new BinaryReader(file);
        var magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(16));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid Battlefront II catalog header in {path}.");

        var result = new FrostbiteCatalog
        {
            ResourceCount = reader.ReadUInt32(),
            PatchCount = reader.ReadUInt32(),
            EncryptedCount = reader.ReadUInt32()
        };
        reader.BaseStream.Position += 12;

        for (var i = 0; i < result.ResourceCount; i++)
        {
            var sha1 = Convert.ToHexString(reader.ReadBytes(20));
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var logicalOffset = reader.ReadUInt32();
            var archiveIndex = reader.ReadUInt32() & 0xFF;
            result.Resources.Add(new CatalogResourceEntry(sha1, offset, size, logicalOffset, archiveIndex));
        }

        for (var i = 0; i < result.PatchCount; i++)
        {
            var sha1 = Convert.ToHexString(reader.ReadBytes(20));
            var baseSha1 = Convert.ToHexString(reader.ReadBytes(20));
            var deltaSha1 = Convert.ToHexString(reader.ReadBytes(20));
            result.Patches.Add(new CatalogPatchEntry(sha1, baseSha1, deltaSha1));
        }

        return result;
    }
}
