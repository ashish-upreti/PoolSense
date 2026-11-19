namespace PoolSense.Api.Services.CodeRAG;

public static class CodeStoragePath
{
    public static string Resolve(string storagePath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new InvalidOperationException("CodeRAG:StoragePath must specify a writable directory, for example artifacts/code-knowledge relative to the API content root.");
        try
        {
            return Path.GetFullPath(storagePath, contentRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidOperationException("CodeRAG:StoragePath is invalid. Configure a valid absolute directory or a path relative to the API content root.", exception);
        }
    }

    public static void EnsureWritable(string fullPath)
    {
        try
        {
            Directory.CreateDirectory(fullPath);
            using var probe = new FileStream(Path.Combine(fullPath, $".poolsense-storage-check-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            probe.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                $"Code knowledge storage '{fullPath}' is unavailable or not writable. Check the drive, directory permissions for the API identity, and CodeRAG:StoragePath (environment key CodeRAG__StoragePath). Use artifacts/code-knowledge relative to the API content root or an existing writable persistent volume.",
                exception);
        }
    }
}
