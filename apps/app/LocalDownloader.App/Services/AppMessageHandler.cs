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
    /// Raised when a validated download.create message arrives. download.accepted is already
    /// sent back to the browser by the time this fires; the App subscribes to show the IDM-style
    /// confirmation popup and only calls DownloadManagerService.CreateTask once the user picks
    /// "Start Download" (Cancel / Return to Browser take other paths; see App.xaml.cs).
    /// </summary>
    public event Action<DownloadRequest>? DownloadRequested;

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
