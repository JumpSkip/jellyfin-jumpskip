using JumpSkip.Configuration;
using JumpSkip.Models;

namespace JumpSkip.Services;

public sealed class SegmentMerger
{
    public Segment[] Merge(IEnumerable<ProviderResult> providers, long? runtimeTicks, PluginConfiguration config)
    {
        // Upgrade JumpSkip's cached protected windows into the same model as fresh provider data.
        var results = providers.Select(p => p.Payload is not { } payload ? p : p with
        {
            Payload = payload with { Segments = payload.Segments.Concat(payload.ProtectedRanges
                .Select(r => new RawSegment(SegmentKind.PostCredits, r.StartMs, r.EndMs))).ToArray(), ProtectedRanges = [] }
        }).ToArray();
        var accepted = new List<Segment>();
        var tolerance = (long)(config.DeduplicationToleranceSeconds * TimeSpan.TicksPerSecond);
        var minimum = (long)(config.MinimumSegmentSeconds * TimeSpan.TicksPerSecond);
        var protection = Protection(results, runtimeTicks);

        foreach (var provider in results)
        {
            var higherTypes = accepted.Select(s => s.Type).ToHashSet();
            var candidates = (provider.Payload?.Segments ?? [])
                .Select(s => Normalize(s, runtimeTicks, provider.Provider))
                .Select(s => ApplyOffsets(s, runtimeTicks, config)).OfType<Segment>()
                .Where(s => config.Allows(s.Type)).OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ThenBy(s => s.Type);
            foreach (var candidate in candidates)
            {
                if (config.MergeStrategy == "MissingTypes" && higherTypes.Contains(candidate.Type)) continue;
                // Conflicting ranges of the same kind represent alternative boundaries, not extra tails.
                if (accepted.Any(s => s.Type == candidate.Type &&
                    (Overlaps(s, candidate) || (Math.Abs(s.StartTicks - candidate.StartTicks) <= tolerance
                        && Math.Abs(s.EndTicks - candidate.EndTicks) <= tolerance)))) continue;
                var pieces = new List<Segment> { candidate };
                // Protect known post-credit scenes regardless of which provider has priority.
                if (candidate.Type == "Outro")
                    foreach (var range in protection) pieces = Subtract(pieces, range);
                foreach (var prior in accepted) pieces = Subtract(pieces, prior);
                accepted.AddRange(pieces.Where(s => s.EndTicks - s.StartTicks >= minimum));
            }
            if (!config.MergeProviderSegments && accepted.Count > 0) break;
        }
        return accepted.OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToArray();
    }

    internal static Segment[] Protection(IEnumerable<ProviderResult> providers, long? runtime) => providers
        .SelectMany(p => (p.Payload?.Segments ?? []).Where(s => s.Kind == SegmentKind.PostCredits)
            .Concat((p.Payload?.ProtectedRanges ?? []).Select(r => new RawSegment(SegmentKind.PostCredits, r.StartMs, r.EndMs))))
        .Select(p => Normalize(p, runtime, "protected")).OfType<Segment>().ToArray();

    internal static Segment? ApplyOffsets(Segment? segment, long? runtime, PluginConfiguration config)
    {
        // Native fallback records have already had offsets applied when they were published.
        if (segment is null || !config.EnableTimingOffsets || segment.Provider == "Stored") return segment;
        var maximum = runtime > 0 ? runtime.Value : long.MaxValue;
        long Shift(long ticks, double seconds) => (long)decimal.Round(Math.Clamp(
            ticks + (decimal)seconds * TimeSpan.TicksPerSecond, 0, maximum));
        var start = Shift(segment.StartTicks, config.StartOffsetSeconds);
        var end = Shift(segment.EndTicks, config.EndOffsetSeconds);
        return end > start ? segment with { StartTicks = start, EndTicks = end } : null;
    }

    internal static Segment? Normalize(RawSegment raw, long? runtime, string provider)
    {
        if (raw.StartMs is null && raw.EndMs is null) return null;
        if (raw.Kind == SegmentKind.PostCredits && (raw.StartMs is null || raw.EndMs is null)) return null;
        var start = raw.StartMs ?? 0;
        var end = raw.EndMs;
        if (end is null && raw.Kind is SegmentKind.Outro or SegmentKind.Preview && runtime > 0)
            end = runtime.Value / (double)TimeSpan.TicksPerMillisecond;
        if (!double.IsFinite(start) || start < 0 || end is null || !double.IsFinite(end.Value)
            || end <= start || end >= long.MaxValue / (double)TimeSpan.TicksPerMillisecond) return null;
        var startTicks = (long)Math.Round(start * TimeSpan.TicksPerMillisecond);
        var endTicks = (long)Math.Round(end.Value * TimeSpan.TicksPerMillisecond);
        if (runtime > 0) endTicks = Math.Min(endTicks, runtime.Value);
        // IntroDB's movie scene and TheIntroDB's TV preview share Jellyfin's native Preview action.
        var kind = raw.Kind == SegmentKind.PostCredits ? SegmentKind.Preview : raw.Kind;
        return endTicks > startTicks ? new(kind.ToString(), startTicks, endTicks, provider) : null;
    }

    private static bool Overlaps(Segment a, Segment b) => a.StartTicks < b.EndTicks && b.StartTicks < a.EndTicks;
    internal static List<Segment> Subtract(List<Segment> pieces, Segment occupied)
    {
        var output = new List<Segment>();
        foreach (var part in pieces)
        {
            if (!Overlaps(part, occupied)) { output.Add(part); continue; }
            if (part.StartTicks < occupied.StartTicks) output.Add(part with { EndTicks = occupied.StartTicks });
            if (part.EndTicks > occupied.EndTicks) output.Add(part with { StartTicks = occupied.EndTicks });
        }
        return output;
    }
}
