namespace LocalDownloader.App.Services;

/// <summary>
/// Ensures only one LocalDownloader.App process runs per user session using a named mutex.
/// The mutex name is intentionally under the `Local\` namespace so it is scoped to the
/// current Terminal Services session (not shared machine-wide), matching a per-user install.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\LocalDownloader.App.SingleInstance";

    private readonly Mutex _mutex;
    private bool _ownsMutex;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
    }

    /// <summary>True if this process is the sole instance and should continue starting up.</summary>
    public bool IsPrimaryInstance
    {
        get
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
                return _ownsMutex;
            }
            catch (AbandonedMutexException)
            {
                // A prior instance crashed while holding the mutex; we still win ownership.
                _ownsMutex = true;
                return true;
            }
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
