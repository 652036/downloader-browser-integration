namespace LocalDownloader.Core.Segments;

/// <summary>
/// A single contiguous byte range [Start, End] (inclusive) within the target file.
/// </summary>
public readonly record struct SegmentRange(long Start, long End)
{
    public long Length => End - Start + 1;
}

/// <summary>
/// Computes how a file of a given size should be split into concurrent download segments.
/// </summary>
public static class SegmentPlanner
{
    public const long MinSegmentBytes = 256 * 1024;
    public const int MinConnections = 1;
    public const int MaxConnections = 32;
    public const int DefaultConnections = 8;

    /// <summary>
    /// Splits <paramref name="totalBytes"/> into up to <paramref name="requestedConnections"/> segments.
    /// The number of segments is automatically reduced so that every segment (except possibly
    /// none, since remainder bytes are folded into the last segment) is at least
    /// <see cref="MinSegmentBytes"/> bytes. Always returns at least one segment.
    /// </summary>
    public static IReadOnlyList<SegmentRange> Plan(long totalBytes, int requestedConnections)
    {
        if (totalBytes <= 0)
        {
            return new[] { new SegmentRange(0, Math.Max(totalBytes - 1, 0)) };
        }

        var connections = Math.Clamp(requestedConnections, MinConnections, MaxConnections);

        // Reduce segment count so each segment holds at least MinSegmentBytes, but never below 1.
        var maxSegmentsByMinSize = Math.Max(1, (int)(totalBytes / MinSegmentBytes));
        connections = Math.Min(connections, maxSegmentsByMinSize);
        connections = Math.Max(connections, 1);

        var baseSize = totalBytes / connections;
        var remainder = totalBytes % connections;

        var segments = new List<SegmentRange>(connections);
        var offset = 0L;
        for (var i = 0; i < connections; i++)
        {
            // Fold the remainder into the last segment so all bytes are covered exactly once.
            var size = baseSize + (i == connections - 1 ? remainder : 0);
            if (size <= 0)
            {
                continue;
            }

            var start = offset;
            var end = offset + size - 1;
            segments.Add(new SegmentRange(start, end));
            offset = end + 1;
        }

        return segments;
    }
}
