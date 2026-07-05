using System.IO;
using System.IO.Pipes;
using LocalDownloader.Core;

namespace LocalDownloader.App.Ipc;

/// <summary>
/// Named pipe server hosting \\.\pipe\LocalDownloader.App. Uses the default ACL from
/// <see cref="NamedPipeServerStream"/> (current user only) and never listens on any TCP port.
/// Accepts overlapping client connections (Host processes connect/disconnect per browser
/// port lifecycle) and dispatches each received frame to <see cref="OnMessageReceived"/>.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    public const string PipeName = "LocalDownloader.App";

    private readonly CancellationTokenSource _stopCts = new();
    private readonly List<Task> _clientLoops = new();
    private readonly List<NamedPipeServerStream> _activeConnections = new();
    private readonly object _lock = new();
    private Task? _acceptLoop;

    /// <summary>
    /// Invoked for every JSON frame received from any connected client. The returned string is
    /// written back as the response frame on the same connection (or null to send nothing).
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? OnMessageReceived { get; set; }

    /// <summary>
    /// Sends an unsolicited JSON frame (e.g. download.returnToBrowser) to every currently
    /// connected client. There is normally at most one live Host connection at a time, so this
    /// is equivalent to "tell the connected browser extension"; harmless no-op if none connected.
    /// </summary>
    public async Task BroadcastAsync(string json, CancellationToken cancellationToken)
    {
        NamedPipeServerStream[] connections;
        lock (_lock)
        {
            connections = _activeConnections.ToArray();
        }

        foreach (var pipe in connections)
        {
            try
            {
                await NativeMessaging.WriteMessageAsync(pipe, json, cancellationToken);
            }
            catch (IOException)
            {
                // Connection likely closed concurrently; HandleClientAsync will clean it up.
            }
        }
    }

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopCts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (IOException)
            {
                // All instances busy; brief backoff before retrying.
                await Task.Delay(200, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                pipe.Dispose();
                continue;
            }

            var clientTask = Task.Run(() => HandleClientAsync(pipe, cancellationToken), cancellationToken);
            lock (_lock)
            {
                _clientLoops.Add(clientTask);
                _clientLoops.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _activeConnections.Add(pipe);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? requestJson;
                try
                {
                    requestJson = await NativeMessaging.ReadMessageAsync(pipe, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    return;
                }

                if (requestJson is null)
                {
                    return;
                }

                var handler = OnMessageReceived;
                if (handler is null)
                {
                    continue;
                }

                string? responseJson;
                try
                {
                    responseJson = await handler(requestJson, cancellationToken);
                }
                catch (Exception)
                {
                    responseJson = null;
                }

                if (responseJson is not null)
                {
                    try
                    {
                        await NativeMessaging.WriteMessageAsync(pipe, responseJson, cancellationToken);
                    }
                    catch (IOException)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _activeConnections.Remove(pipe);
            }

            pipe.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();

        Task[] remaining;
        lock (_lock)
        {
            remaining = _clientLoops.ToArray();
        }

        try
        {
            if (_acceptLoop is not null)
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Best-effort shutdown.
        }

        try
        {
            await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown.
        }

        _stopCts.Dispose();
    }
}
