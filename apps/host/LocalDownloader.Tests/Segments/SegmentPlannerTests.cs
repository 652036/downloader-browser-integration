using LocalDownloader.Core.Segments;

namespace LocalDownloader.Tests.Segments;

public sealed class SegmentPlannerTests
{
    [Fact]
    public void Plan_divides_evenly_when_size_is_multiple_of_connection_count()
    {
        var segments = SegmentPlanner.Plan(8 * 1024 * 1024, 8);

        Assert.Equal(8, segments.Count);
        Assert.All(segments, s => Assert.Equal(1024 * 1024, s.Length));
        AssertContiguousCoverage(segments, 8 * 1024 * 1024);
    }

    [Fact]
    public void Plan_folds_remainder_into_last_segment_when_not_evenly_divisible()
    {
        const long total = 10_000_003;
        var segments = SegmentPlanner.Plan(total, 8);

        Assert.Equal(8, segments.Count);
        AssertContiguousCoverage(segments, total);

        // First 7 segments equal size, last one absorbs the remainder.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(segments[0].Length, segments[i].Length);
        }

        Assert.True(segments[^1].Length >= segments[0].Length);
    }

    [Fact]
    public void Plan_downgrades_segment_count_for_small_files_below_min_segment_size()
    {
        // 1 MB total with default 256KB minimum segment size => at most 4 segments even if 8 requested.
        var segments = SegmentPlanner.Plan(1024 * 1024, 8);

        Assert.True(segments.Count <= 4);
        AssertContiguousCoverage(segments, 1024 * 1024);
    }

    [Fact]
    public void Plan_returns_single_segment_for_files_smaller_than_min_segment_size()
    {
        var segments = SegmentPlanner.Plan(1000, 8);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(999, segments[0].End);
    }

    [Fact]
    public void Plan_handles_single_byte_file()
    {
        var segments = SegmentPlanner.Plan(1, 8);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(0, segments[0].End);
        Assert.Equal(1, segments[0].Length);
    }

    [Fact]
    public void Plan_handles_zero_length_file()
    {
        var segments = SegmentPlanner.Plan(0, 8);

        Assert.Single(segments);
        Assert.Equal(0, segments[0].Start);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(200)]
    public void Plan_clamps_out_of_range_connection_counts(int requested)
    {
        var segments = SegmentPlanner.Plan(64 * 1024 * 1024, requested);

        Assert.InRange(segments.Count, 1, SegmentPlanner.MaxConnections);
        AssertContiguousCoverage(segments, 64 * 1024 * 1024);
    }

    private static void AssertContiguousCoverage(IReadOnlyList<SegmentRange> segments, long totalBytes)
    {
        Assert.Equal(0, segments[0].Start);
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);
        }

        Assert.Equal(totalBytes - 1, segments[^1].End);
        Assert.Equal(totalBytes, segments.Sum(s => s.Length));
    }
}
