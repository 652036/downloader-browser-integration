using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDownloader.App.Settings;
using LocalDownloader.Core;

namespace LocalDownloader.App.Services;

/// <summary>
/// Translates inbound IPC frames (forwarded verbatim by the thin Host proxy) into
/// DownloadManagerService / SettingsStore calls, and serializes the JSON response frame.
/// This is the App-side counterpart of the extension's outbound messages described in the
/// design doc's IPC contract section.
/// </summary>
public sealed class AppMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly DownloadManagerService _downloadManager;
    private readonly SettingsStore _settingsStore;

    /// <summary>
    /// Invoked when a download.create message arrives, before the task is queued, so the UI
    /// layer can show the confirmation popup. Return true to proceed with queuing the task
    /// (or let the caller decide asynchronously); the App wires this to popup workflow.
    /// </summary>
    public Action<DownloadRequest>? DownloadRequested { get; set; }

    public AppMessageHandler(DownloadManagerService downloadManager, SettingsStore settingsStore)
    {
        _downloadManager = downloadManager;
        _settingsStore = settingsStore;
    }

    public Task<string?> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        IpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IpcMessage>(requestJson, JsonOptions);
        }
        catch (JsonException)
        {
            return Task.FromResult<string?>(SerializeError(null, IpcErrorCode.HostProtocolError, "Invalid JSON message."));
        }

        switch (message?.Type)
        {
            case IpcMessageType.DownloadCreate:
                return Task.FromResult<string?>(HandleDownloadCreate(message));

            case IpcMessageType.SettingsGet:
                return Task.FromResult<string?>(HandleSettingsGet(message));

            default:
                return Task.FromResult<string?>(SerializeError(message?.Id, IpcErrorCode.HostProtocolError, "Unsupported message type."));
        }
    }

    private string HandleDownloadCreate(IpcMessage message)
    {
        var request = new DownloadRequest
        {
            Type = IpcMessageType.DownloadCreate,
            Id = message.Id,
            Url = message.Url,
            SuggestedFilename = message.SuggestedFilename,
            Referrer = message.Referrer,
            CookieHeader = message.CookieHeader,
            UserAgent = message.UserAgent,
            FileSize = message.FileSize,
            Mime = message.Mime,
            Source = message.Source
        };

        if (!DownloadRequestValidator.TryValidate(request, out var validationError))
        {
            return SerializeError(message.Id, validationError!.Code, validationError.Message);
        }

        DownloadRequested?.Invoke(request);

        return JsonSerializer.Serialize(new
        {
            type = IpcMessageType.DownloadAccepted,
            id = message.Id,
            status = "queued"
        }, JsonOptions);
    }

    private string HandleSettingsGet(IpcMessage message)
    {
        var settings = _settingsStore.Load();
        return JsonSerializer.Serialize(new
        {
            type = IpcMessageType.SettingsValue,
            id = message.Id,
            interceptExtensions = settings.InterceptExtensions,
            interceptMimePrefixes = settings.InterceptMimePrefixes
        }, JsonOptions);
    }

    private static string SerializeError(string? id, string code, string message)
    {
        return JsonSerializer.Serialize(new
        {
            type = IpcMessageType.DownloadError,
            id,
            code,
            message
        }, JsonOptions);
    }
}
