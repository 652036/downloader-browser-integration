namespace LocalDownloader.Core;

public interface IDownloadEngine
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken);
}

public sealed class DownloadEngine : IDownloadEngine
{
    private readonly HttpClient _httpClient;
    private readonly string _outputDirectory;
    private readonly TaskStore _taskStore;

    public DownloadEngine(HttpClient httpClient, string outputDirectory)
        : this(httpClient, outputDirectory, new TaskStore())
    {
    }

    public DownloadEngine(HttpClient httpClient, string outputDirectory, TaskStore taskStore)
    {
        _httpClient = httpClient;
        _outputDirectory = outputDirectory;
        _taskStore = taskStore;
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        if (!DownloadRequestValidator.TryValidate(request, out var validationError))
        {
            throw new DownloadRequestException(validationError!.Code, validationError.Message);
        }

        Directory.CreateDirectory(_outputDirectory);

        var uri = new Uri(request.Url!);
        var fallbackName = FileNameSanitizer.Sanitize(Path.GetFileName(uri.LocalPath), "download.bin");
        var fileName = FileNameSanitizer.Sanitize(request.SuggestedFilename, fallbackName);
        var finalPath = GetAvailablePath(Path.Combine(_outputDirectory, fileName));
        var partPath = $"{finalPath}.part";
        var metadataPath = $"{finalPath}.task.json";
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id!;

        try
        {
            await SaveMetadataAsync("downloading", 0, id, request.Url!, finalPath, partPath, metadataPath, cancellationToken);

            using var requestMessage = CreateRequestMessage(uri, request);
            using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var bytesWritten = new FileInfo(partPath).Length;
            File.Move(partPath, finalPath);
            await SaveMetadataAsync("completed", bytesWritten, id, request.Url!, finalPath, partPath, metadataPath, cancellationToken);

            return new DownloadResult(id, "completed", finalPath, bytesWritten);
        }
        catch
        {
            var bytesWritten = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            await SaveMetadataAsync("failed", bytesWritten, id, request.Url!, finalPath, partPath, metadataPath, CancellationToken.None);
            throw;
        }
    }

    private static HttpRequestMessage CreateRequestMessage(Uri uri, DownloadRequest request)
    {
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);

        if (!string.IsNullOrWhiteSpace(request.UserAgent))
        {
            requestMessage.Headers.UserAgent.TryParseAdd(request.UserAgent);
        }

        if (Uri.TryCreate(request.Referrer, UriKind.Absolute, out var referrer) &&
            (referrer.Scheme == Uri.UriSchemeHttp || referrer.Scheme == Uri.UriSchemeHttps))
        {
            requestMessage.Headers.Referrer = referrer;
        }

        if (!string.IsNullOrWhiteSpace(request.CookieHeader))
        {
            requestMessage.Headers.TryAddWithoutValidation("Cookie", request.CookieHeader);
        }

        return requestMessage;
    }

    private async Task SaveMetadataAsync(
        string status,
        long bytesWritten,
        string id,
        string url,
        string filePath,
        string partPath,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        var metadata = new DownloadTaskMetadata(
            id,
            url,
            status,
            filePath,
            partPath,
            metadataPath,
            bytesWritten,
            DateTimeOffset.UtcNow);

        await _taskStore.SaveAsync(metadata, cancellationToken);
    }

    private static string GetAvailablePath(string path)
    {
        if (!File.Exists(path) && !File.Exists($"{path}.part") && !File.Exists($"{path}.task.json"))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !File.Exists($"{candidate}.part") && !File.Exists($"{candidate}.task.json"))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to find an available output file name.");
    }
}

public sealed record DownloadResult(string Id, string Status, string FilePath, long BytesWritten);

public sealed class DownloadRequestException : Exception
{
    public DownloadRequestException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
