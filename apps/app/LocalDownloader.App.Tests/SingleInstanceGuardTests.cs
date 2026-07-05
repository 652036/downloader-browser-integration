using LocalDownloader.App.Services;

namespace LocalDownloader.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void IsPrimaryInstance_is_false_when_mutex_already_held_by_another_owner()
    {
        // Simulate "another instance already running" by holding the same named mutex on a
        // separate thread (Mutex ownership is thread-affine, so this does not just re-enter).
        using var externalMutex = new Mutex(initiallyOwned: false, SingleInstanceGuard.MutexName);
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        var holderThread = new Thread(() =>
        {
            externalMutex.WaitOne();
            acquired.Set();
            release.Wait();
            externalMutex.ReleaseMutex();
        });
        holderThread.IsBackground = true;
        holderThread.Start();

        try
        {
            acquired.Wait(TimeSpan.FromSeconds(5));

            using var guard = new SingleInstanceGuard();
            Assert.False(guard.IsPrimaryInstance);
        }
        finally
        {
            release.Set();
            holderThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void IsPrimaryInstance_is_true_when_no_other_owner_holds_the_mutex()
    {
        using var guard = new SingleInstanceGuard();

        Assert.True(guard.IsPrimaryInstance);
    }
}
