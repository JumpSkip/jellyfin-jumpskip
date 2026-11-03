namespace JumpSkip.Models;

// PostCredits is an upstream/cache discriminator, normalized to Preview before publication.
public enum SegmentKind { Intro, Recap, Outro, Preview, PostCredits }
public enum FetchStatus { Success, NotFound, ConfirmedAbsent, Error, RateLimited, Unsupported }

public sealed record RawSegment(SegmentKind Kind, double? StartMs, double? EndMs);
// Retained to read saved protected-window cache entries without another provider lookup.
public sealed record ProtectedRange(double StartMs, double EndMs);
public sealed record ProviderPayload(RawSegment[] Segments, ProtectedRange[] ProtectedRanges, bool ConfirmedAbsent = false);
public sealed record ProviderResult(string Provider, FetchStatus Status, ProviderPayload? Payload = null,
    bool Cached = false, bool Stale = false, DateTimeOffset? RetryAt = null, DateTimeOffset? ExpiresAt = null);
public sealed record Segment(string Type, long StartTicks, long EndTicks, string Provider)
{
    public double StartSeconds => StartTicks / (double)TimeSpan.TicksPerSecond;
    public double EndSeconds => EndTicks / (double)TimeSpan.TicksPerSecond;
}
public sealed record ProviderSummary(string Provider, string Status, bool Cached, bool Stale, DateTimeOffset? RetryAt, DateTimeOffset? ExpiresAt);
public sealed record SegmentResponse(Guid ItemId, string Status, long? RuntimeTicks, Segment[] Segments,
    ProviderSummary[] Providers);
public sealed record MediaLookup(bool IsMovie, string? ImdbId, long? TvdbId, long? TmdbId,
    int? Season, int? Episode, string? Mapping, bool UnsupportedOrder, long? TvdbEpisodeId, long? RuntimeTicks);
