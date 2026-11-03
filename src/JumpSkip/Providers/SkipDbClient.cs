using System.Globalization;
using System.Text.Json;
using JumpSkip.Models;

namespace JumpSkip.Providers;

public sealed class SkipDbClient : ISegmentSource
{
    public string Name => "SkipDB";

    public string? GetPath(MediaLookup item)
    {
        if (string.IsNullOrEmpty(item.ImdbId)) return null;
        if (!item.IsMovie && (item.UnsupportedOrder || item.Mapping is not null
            || item.Season is null or < 0 || item.Episode is null or <= 0)) return null;
        var parts = new List<string> { "imdb_id=" + Uri.EscapeDataString(item.ImdbId) };
        if (!item.IsMovie)
        {
            parts.Add("season=" + item.Season!.Value.ToString(CultureInfo.InvariantCulture));
            parts.Add("episode=" + item.Episode!.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (item.RuntimeTicks > 0)
            parts.Add("duration=" + (item.RuntimeTicks.Value / (decimal)TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture));
        parts.Add("adjust=conservative");
        return "api/segments?" + string.Join('&', parts);
    }

    public ProviderPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonRanges.RequireObject(root, "segments");
        if (root.TryGetProperty("error", out _)) throw new JsonException("SkipDB returned an error.");
        var segments = root.GetProperty("segments");
        JsonRanges.RequireObject(segments, "intro", "recap", "outro", "preview");
        var output = new List<RawSegment>();
        var absent = 0;
        foreach (var (field, kind) in new[] { ("intro", SegmentKind.Intro), ("recap", SegmentKind.Recap),
            ("outro", SegmentKind.Outro), ("preview", SegmentKind.Preview) })
        {
            var value = segments.GetProperty(field);
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a SkipDB segment object.");
            var start = JsonRanges.Number(value, "start_ms");
            var end = JsonRanges.Number(value, "end_ms");
            if (start == 0 && end == 0) { absent++; continue; }
            if (start is null || (end is null && kind != SegmentKind.Outro))
                throw new JsonException("Missing SkipDB timestamps.");
            if (!value.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.String)
                throw new JsonException("Missing SkipDB duration match.");
            // Native skip controls cannot present uncertain matches. Do not publish an out-of-range result.
            if (match.GetString() == "out-of-range") continue;
            if (match.GetString() is not ("exact" or "shifted" or "agnostic"))
                throw new JsonException("Unknown SkipDB duration match.");
            // SkipDB already adjusts timestamps; offset_ms and length estimates are not segment boundaries.
            output.Add(new(kind, start, end));
        }
        return new(output.ToArray(), [], ConfirmedAbsent: absent == 4);
    }
}
