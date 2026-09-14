using System.Text.Json;
using Permafrost.Core.Ebx;

namespace Permafrost.Core.Workspace;

public sealed class WorkspaceManager
{
    public WorkspaceManager(string root, string installRoot)
    {
        Root = Path.GetFullPath(root);
        InstallRoot = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(ModifiedEbxRoot);
        Directory.CreateDirectory(CacheRoot);
    }

    public string Root { get; }
    public string InstallRoot { get; }
    public string ModifiedEbxRoot => Path.Combine(Root, "Modified", "Ebx");
    public string CacheRoot => Path.Combine(Root, ".permafrost", "cache");

    public static string GetDefaultPath(string installRoot)
    {
        var safeName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installRoot));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "BattlefrontII";
        foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Permafrost", "Workspaces", safeName);
    }

    public string GetOverlayPath(string assetName)
    {
        var normalized = assetName.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePart).ToArray();
        var relative = Path.Combine(parts);
        return Path.Combine(ModifiedEbxRoot, relative + ".bin");
    }

    public bool TryReadOverlay(string assetName, out byte[] data)
    {
        var path = GetOverlayPath(assetName);
        if (File.Exists(path))
        {
            data = File.ReadAllBytes(path);
            return true;
        }
        data = Array.Empty<byte>();
        return false;
    }

    public async Task SaveDocumentAsync(string assetName, EbxDocument document, CancellationToken cancellationToken = default)
    {
        var path = GetOverlayPath(assetName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, document.GetBytesCopy(), cancellationToken);
        document.IsDirty = false;
        await WriteMetadataAsync(cancellationToken);
    }

    public void Revert(string assetName)
    {
        var path = GetOverlayPath(assetName);
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task WriteMetadataAsync(CancellationToken cancellationToken)
    {
        var metadata = new
        {
            format = 1,
            editor = "Permafrost",
            installRoot = InstallRoot,
            updatedUtc = DateTime.UtcNow,
            modifiedRoot = "Modified/Ebx"
        };
        await using var stream = File.Create(Path.Combine(Root, "workspace.permafrost.json"));
        await JsonSerializer.SerializeAsync(stream, metadata, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    private static string SanitizePart(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value;
    }
}
