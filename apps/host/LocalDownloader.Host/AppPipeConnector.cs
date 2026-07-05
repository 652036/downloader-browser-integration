using System.Diagnostics;
using System.IO.Pipes;

namespace LocalDownloader.Host;

/// <summary>
/// Connects to the LocalDownloader.App named pipe, launching the App executable (from the
/// same directory as the Host) if the pipe is not already listening. Retries every 500ms up
/// to a 5 second total timeout, matching the design doc's App-not-running recovery path.
/// </summary>
public sealed class AppPipeConnector
{
    private const string PipeName = "LocalDownloader.App";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(5);

    private readonly HostLogger _logger;
    private readonly string _appExecutablePath;

    public AppPipeConnector(HostLogger logger)
        : this(logger, DefaultAppExecutablePath())
    {
    }

    public AppPipeConnector(HostLogger logger, string appExecutablePath)
    {
        _logger = logger;
        _appExecutablePath = appExecutablePath;
    }

    public static string DefaultAppExecutablePath() =>
        Path.Combine(AppContext.BaseDirectory, "LocalDownloader.App.exe");

    /// <summary>
    /// Connects to the App's pipe, starting the App process if the first attempt fails.
    /// Returns null (rather than throwing) if the App could not be reached within the timeout,
    /// so the caller can synthesize a download.error response instead of crashing.
    /// </summary>
    public async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = await TryConnectOnceAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
        if (pipe is not null)
        {
            return pipe;
        }

        TryLaunchApp();

        using var timeoutCts = new CancellationTokenSource(TotalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.IsCancellationRequested)
        {
            pipe = await TryConnectOnceAsync(RetryInterval, linkedCts.Token);
            if (pipe is not null)
            {
                return pipe;
            }
        }

        _logger.Log("Failed to connect to LocalDownloader.App pipe within timeout.");
        return null;
    }

    private async Task<NamedPipeClientStream?> TryConnectOnceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            pipe.Dispose();
            return null;
        }
    }

    private void TryLaunchApp()
    {
        try
        {
            if (!File.Exists(_appExecutablePath))
            {
                _logger.Log($"App executable not found at {_appExecutablePath}.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _appExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(_appExecutablePath) ?? AppContext.BaseDirectory
            };

            Process.Start(startInfo);
            _logger.Log($"Launched {_appExecutablePath}.");
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to launch App executable: {ex.Message}");
        }
    }
}
