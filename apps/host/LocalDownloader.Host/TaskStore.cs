using System.Text.Json;

namespace LocalDownloader.Host;

public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task SaveAsync(DownloadTaskMetadata metadata, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadata.MetadataPath)!);

        var tempPath = $"{metadata.MetadataPath}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken);

        if (File.Exists(metadata.MetadataPath))
        {
            File.Delete(metadata.MetadataPath);
        }

        File.Move(tempPath, metadata.MetadataPath);
    }
}

public sealed record DownloadTaskMetadata(
    string Id,
    string Url,
    string Status,
    string FilePath,
    string PartPath,
    string MetadataPath,
    long BytesWritten,
    DateTimeOffset UpdatedAt);
