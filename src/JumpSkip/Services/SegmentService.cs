using System.Text.Json;
using JumpSkip.Configuration;
using JumpSkip.Models;
using JumpSkip.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Services;

public sealed class SegmentService(IEnumerable<ISegmentSource> sources, ProviderTransport transport, CacheStore cache,
    SegmentMerger merger, MediaIdentity identity, ItemFilter filter, SegmentStorage storage, Func<PluginConfiguration> configuration,
    ILogger<SegmentService> logger, ChapterFallback chapters) : IDisposable
{
    private readonly Dictionary<string, ISegmentSource> _sources = sources.ToDictionary(s => s.Name);
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<SegmentResponse> GetAsync(BaseItem item, CancellationToken token)
    {
        var config = configuration();
        if (!filter.Allows(item, config)) return Empty(item, "Excluded");
        if (config.IgnoreItemsWithOtherProviderSegments && await storage.HasOtherAsync(item).ConfigureAwait(false))
            return Empty(item, "OtherProviderSegments");
        var lookup = identity.Resolve(item);
        var results = new List<ProviderResult>();
        // Every enabled provider is considered, even after the first returns usable data.
        if (lookup is not null)
        {
            foreach (var name in config.ProviderPriority)
            {
                if (!configuration().Enabled) return Empty(item, "Excluded");
                var settings = config.Provider(name);
                if (!settings.Enabled) continue;
                results.Add(await FetchAsync(_sources[name], settings, lookup, item.Id, (item as Episode)?.SeriesId ?? Guid.Empty, config, token).ConfigureAwait(false));
            }
        }
        var status = results.Any(r => r.Status is FetchStatus.Error or FetchStatus.RateLimited) ? "Partial" : "Ready";
        if (results.Count == 0) status = lookup is null && config.ProviderPriority.Any(name => config.Provider(name).Enabled) ? "UnsupportedIdentity" : "Disabled";
        else if (results.All(r => r.Status == FetchStatus.Unsupported)) status = "UnsupportedIdentity";
        var mergeInputs = results.ToList();
        var oldChapters = cache.GetChapterSegments(item.Id);
        if (status == "Partial" || (status == "UnsupportedIdentity" && (config.EnableChapterSkipping || oldChapters.Length > 0)))
        {
            var old = await storage.ReadOwnAsync(item).ConfigureAwait(false);
            var ranges = old.Where(s => Enum.TryParse<SegmentKind>(s.Type.ToString(), out _)
                    && !oldChapters.Any(c => c.Type == s.Type.ToString() && c.StartTicks == s.StartTicks && c.EndTicks == s.EndTicks))
                .Select(s => new RawSegment(Enum.Parse<SegmentKind>(s.Type.ToString()),
                    s.StartTicks / (double)TimeSpan.TicksPerMillisecond, s.EndTicks / (double)TimeSpan.TicksPerMillisecond)).ToArray();
            mergeInputs.Add(new("Stored", FetchStatus.Success, new(ranges, []), Cached: true, Stale: true));
        }
        if (!configuration().Enabled) return Empty(item, "Excluded");
        var segments = chapters.Apply(item, merger.Merge(mergeInputs, item.RunTimeTicks, config), mergeInputs, config);
        if ((status is "Disabled" or "UnsupportedIdentity") && (segments.Length > 0 || oldChapters.Length > 0)) status = "Ready";
        return new(item.Id, status, item.RunTimeTicks, segments,
            results.Select(r => new ProviderSummary(r.Provider, r.Status.ToString(), r.Cached, r.Stale, r.RetryAt, r.ExpiresAt)).ToArray());
    }

    private async Task<ProviderResult> FetchAsync(ISegmentSource source, ProviderSettings settings, MediaLookup lookup,
        Guid itemId, Guid seriesId, PluginConfiguration config, CancellationToken token)
    {
        var path = source.GetPath(lookup);
        if (path is null) return new(source.Name, FetchStatus.Unsupported);
        var key = ProviderTransport.Fingerprint(ProviderTransport.ProviderKey(source.Name, settings) + "|" + path + "|" + lookup.RuntimeTicks);
        var gate = _locks[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_locks.Length];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var generation = cache.Generation;
            var old = cache.Get(key, itemId, seriesId);
            var now = DateTimeOffset.UtcNow;
            if (old is not null && old.ExpiresAt > now)
                return old.Result with { Cached = true, ExpiresAt = old.ExpiresAt };
            var http = await transport.GetAsync(source.Name, settings, path, config.RequestTimeoutSeconds, token).ConfigureAwait(false);
            ProviderResult result;
            if (http.StatusCode == 404) result = new(source.Name, FetchStatus.NotFound);
            else if (http.StatusCode == 200)
            {
                try
                {
                    var payload = source.Parse(http.Body);
                    var valid = payload.Segments.Any(r => SegmentMerger.Normalize(r, lookup.RuntimeTicks, source.Name) is not null);
                    result = new(source.Name, valid || payload.ProtectedRanges.Length > 0 ? FetchStatus.Success
                        : payload.ConfirmedAbsent ? FetchStatus.ConfirmedAbsent : FetchStatus.NotFound, payload);
                }
                catch (JsonException)
                { logger.LogWarning("JumpSkip: {Provider} returned an invalid response; retaining cached segments.", source.Name); result = new(source.Name, FetchStatus.Error); }
            }
            else result = new(source.Name, http.StatusCode is 429 or 503 ? FetchStatus.RateLimited : FetchStatus.Error, RetryAt: http.RetryAt);
            now = DateTimeOffset.UtcNow;
            DateTimeOffset expires;
            DateTimeOffset retain;
            if (result.Status is FetchStatus.Error or FetchStatus.RateLimited)
            {
                expires = http.RetryAt is { } retry && retry > now ? retry : now.AddSeconds(30);
                var stale = old?.Result.Payload is not null && old.RetainUntil > now;
                result = result with { Payload = stale ? old!.Result.Payload : null, Stale = stale, RetryAt = expires };
                retain = stale ? old!.RetainUntil : expires;
                expires = expires < retain ? expires : retain;
            }
            else
            {
                expires = result.Status switch
                {
                    FetchStatus.NotFound => now.AddHours(config.NegativeCacheHours),
                    FetchStatus.ConfirmedAbsent => now.AddDays(config.ConfirmedAbsentCacheDays),
                    _ => config.RefreshAfterDays == 0 ? DateTimeOffset.MaxValue : now.AddDays(config.RefreshAfterDays * (0.75 + Random.Shared.NextDouble() * 0.5))
                };
                retain = result.Status == FetchStatus.Success && expires != DateTimeOffset.MaxValue ? expires.AddDays(config.StaleIfErrorDays) : expires;
            }
            result = result with { ExpiresAt = expires };
            cache.Put(key, new(result, expires, retain, [itemId], seriesId == Guid.Empty ? [] : [seriesId]), generation, config.MaxCacheEntries);
            return result;
        }
        finally { gate.Release(); }
    }

    private static SegmentResponse Empty(BaseItem item, string status) => new(item.Id, status, item.RunTimeTicks, [], []);
    public void Dispose() { foreach (var gate in _locks) gate.Dispose(); }
}
