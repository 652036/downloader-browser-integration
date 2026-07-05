using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using LocalDownloader.Core;

namespace LocalDownloader.Host;

public sealed class HostMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDownloadEngine _downloadEngine;
    private readonly ConcurrentBag<Task> _backgroundTasks = new();

    public HostMessageHandler(IDownloadEngine downloadEngine)
    {
        _downloadEngine = downloadEngine;
    }

    public async Task<string> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        DownloadRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<DownloadRequest>(requestJson, JsonOptions);
        }
        catch (JsonException)
        {
            return SerializeError(null, "host_protocol_error", "Invalid JSON message.");
        }

        if (request?.Type != "download.create")
        {
            return SerializeError(request?.Id, "host_protocol_error", "Unsupported message type.");
        }

        if (!DownloadRequestValidator.TryValidate(request, out var validationError))
        {
            return SerializeError(request.Id, validationError!.Code, validationError.Message);
        }

        _backgroundTasks.Add(RunDownloadInBackgroundAsync(request));

        return JsonSerializer.Serialize(new
        {
            type = "download.accepted",
            id = request.Id,
            status = "queued"
        }, JsonOptions);
    }

    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var tasks = _backgroundTasks.ToArray();
            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks);

            if (_backgroundTasks.Count == tasks.Length)
            {
                return;
            }
        }
    }

    private async Task RunDownloadInBackgroundAsync(DownloadRequest request)
    {
        try
        {
            await _downloadEngine.DownloadAsync(request, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DownloadRequestException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException or IOException)
        {
            await Console.Error.WriteLineAsync($"Download failed for {request.Id ?? request.Url}: {ex.Message}");
        }
    }

    private static string SerializeError(string? id, string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            type = "download.error",
            id,
            code,
            message
        }, JsonOptions);
    }
}
