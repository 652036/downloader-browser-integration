using LocalDownloader.App.Services;

namespace LocalDownloader.App.Tests;

public sealed class ClipboardDedupeTrackerTests
{
    [Fact]
    public void ShouldOffer_returns_true_the_first_time_a_url_is_seen()
    {
        var tracker = new ClipboardDedupeTracker(TimeSpan.FromMinutes(10), () => DateTimeOffset.UtcNow);

        Assert.True(tracker.ShouldOffer("https://example.com/file.zip"));
    }

    [Fact]
    public void ShouldOffer_returns_false_for_the_same_url_within_the_dedupe_window()
    {
        var now = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
        var tracker = new ClipboardDedupeTracker(TimeSpan.FromMinutes(10), () => now);

        Assert.True(tracker.ShouldOffer("https://example.com/file.zip"));

        now = now.AddMinutes(5);
        Assert.False(tracker.ShouldOffer("https://example.com/file.zip"));
    }

    [Fact]
    public void ShouldOffer_returns_true_again_once_the_dedupe_window_elapses()
    {
        var now = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
        var tracker = new ClipboardDedupeTracker(TimeSpan.FromMinutes(10), () => now);

        Assert.True(tracker.ShouldOffer("https://example.com/file.zip"));

        now = now.AddMinutes(10).AddSeconds(1);
        Assert.True(tracker.ShouldOffer("https://example.com/file.zip"));
    }

    [Fact]
    public void ShouldOffer_treats_urls_case_insensitively()
    {
        var tracker = new ClipboardDedupeTracker();

        Assert.True(tracker.ShouldOffer("https://Example.com/File.zip"));
        Assert.False(tracker.ShouldOffer("https://example.com/file.ZIP"));
    }

    [Fact]
    public void MarkCanceled_permanently_suppresses_the_url_even_after_the_window_elapses()
    {
        var now = new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);
        var tracker = new ClipboardDedupeTracker(TimeSpan.FromMinutes(10), () => now);

        Assert.True(tracker.ShouldOffer("https://example.com/file.zip"));
        tracker.MarkCanceled("https://example.com/file.zip");

        now = now.AddDays(1);
        Assert.False(tracker.ShouldOffer("https://example.com/file.zip"));
    }

    [Fact]
    public void Different_urls_are_tracked_independently()
    {
        var tracker = new ClipboardDedupeTracker();

        Assert.True(tracker.ShouldOffer("https://example.com/a.zip"));
        Assert.True(tracker.ShouldOffer("https://example.com/b.zip"));
    }
}
