using System.IO.Pipes;
using LocalDownloader.Host;

namespace LocalDownloader.Tests.Relay;

public sealed class AppPipeConnectorTests
{
    [Fact]
    public async Task ConnectAsync_connects_when_pipe_server_is_already_listening()
    {
        using var server = new NamedPipeServerStream(
            "LocalDownloader.App",
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverAccept = server.WaitForConnectionAsync();

        var logger = new HostLogger(CreateTempLogDir());
        var connector = new AppPipeConnector(logger, appExecutablePath: @"C:\nonexistent\LocalDownloader.App.exe");

        var pipe = await connector.ConnectAsync(CancellationToken.None);

        Assert.NotNull(pipe);
        await serverAccept.WaitAsync(TimeSpan.FromSeconds(5));
        pipe!.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_returns_null_when_no_server_and_app_exe_missing()
    {
        var logger = new HostLogger(CreateTempLogDir());
        var connector = new AppPipeConnector(logger, appExecutablePath: @"C:\nonexistent\LocalDownloader.App.exe");

        var pipe = await connector.ConnectAsync(CancellationToken.None);

        Assert.Null(pipe);
    }

    private static string CreateTempLogDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "LocalDownloaderHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
