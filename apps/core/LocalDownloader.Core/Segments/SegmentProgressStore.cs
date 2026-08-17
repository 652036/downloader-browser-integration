using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalDownloader.Core.Segments;

/// <summary>
/// Progress of a single segment, persisted to the sidecar `.task.json` file.
/// <see cref="CompletedBytes"/> counts bytes written for this segment so far; resuming
/// continues at <c>Start + CompletedBytes</c>.
/// </summary>
public sealed class SegmentProgress
{
    /// <summary>Backing field for <see cref="End"/>, exposed so worker threads can
    /// <see cref="System.Threading.Volatile"/>-read/write it directly: a work-stealing split
    /// shrinks a running segment's End concurrently with the writer thread appending bytes, and
    /// the writer must observe the new boundary without taking a lock on every write.</summary>
    [JsonIgnore]
    internal long EndUnsafe;

    [JsonPropertyName("start")]
    public long Start { get; set; }

    [JsonPropertyName("end")]
    public long End
    {
        get => Volatile.Read(ref EndUnsafe);
        set => Volatile.Write(ref EndUnsafe, value);
    }

    [JsonPropertyName("completedBytes")]
    public long CompletedBytes { get; set; }

    /// <summary>Transient (non-rate-limit) retry attempts consumed by this segment. Lives on
    /// the segment so it survives requeue; a local counter would reset and retry forever.</summary>
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    [JsonIgnore]
    public bool IsComplete => CompletedBytes >= (End - Start + 1);
}

/// <summary>
/// Sidecar `.task.json` contents describing the state of an in-progress or paused segmented
/// download. Lives next to the `.part` file and is deleted once the download completes.
/// </summary>
public sealed class SegmentedTaskState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "queued";

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("partPath")]
    public string PartPath { get; set; } = string.Empty;

    [JsonPropertyName("metadataPath")]
    public string MetadataPath { get; set; } = string.Empty;

    [JsonPropertyName("totalBytes")]
    public long? TotalBytes { get; set; }

    /// <summary>
    /// Instance-length observed from a 206 Content-Range during this process lifetime.
    /// Not persisted. Used to reject a stale probe size before finalize.
    /// </summary>
    [JsonIgnore]
    internal long? ObservedInstanceLength { get; set; }

    private readonly object _observedLock = new();

    /// <summary>
    /// Records a 206 instance-length. Throws <see cref="SegmentedDownloadException"/> if it
    /// conflicts with a previously observed value or with the probed <see cref="TotalBytes"/>.
    /// </summary>
    internal void NoteObservedInstanceLength(long instanceLength)
    {
        lock (_observedLock)
        {
            if (ObservedInstanceLength is { } existing && existing != instanceLength)
            {
                throw new SegmentedDownloadException(
                    $"Size mismatch / stale probe: server advertised conflicting instance-lengths {existing} and {instanceLength}.");
            }

            ObservedInstanceLength = instanceLength;

            if (TotalBytes is { } planned && instanceLength != planned)
            {
                throw new SegmentedDownloadException(
                    $"Size mismatch / stale probe: server instance-length is {instanceLength} but probe planned {planned} bytes.");
            }
        }
    }

    [JsonPropertyName("supportsRange")]
    public bool SupportsRange { get; set; }

    [JsonPropertyName("segments")]
    public List<SegmentProgress> Segments { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public long CompletedBytes => Segments.Sum(s => s.CompletedBytes);
}

/// <summary>
/// Reads and atomically writes <see cref="SegmentedTaskState"/> sidecar files.
/// </summary>
public sealed class SegmentProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task SaveAsync(SegmentedTaskState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(state.MetadataPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{state.MetadataPath}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);

        if (File.Exists(state.MetadataPath))
        {
            File.Delete(state.MetadataPath);
        }

        File.Move(tempPath, state.MetadataPath);
    }

    public async Task<SegmentedTaskState?> LoadAsync(string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        return JsonSerializer.Deserialize<SegmentedTaskState>(json, JsonOptions);
    }

    public void Delete(string metadataPath)
    {
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }
    }
}
