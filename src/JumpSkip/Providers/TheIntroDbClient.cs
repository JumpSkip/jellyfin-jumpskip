using System.Globalization;
using System.Text.Json;
using JumpSkip.Models;

namespace JumpSkip.Providers;

public sealed class TheIntroDbClient : ISegmentSource
{
    public string Name => "TheIntroDB";
    public string? GetPath(MediaLookup item)
    {
        if (!item.IsMovie && (item.UnsupportedOrder || (item.Mapping is not null && item.TvdbId is not > 0))) return null;
        var parts = new List<string>();
        if (item.TvdbId > 0) parts.Add($"tvdb_id={item.TvdbId.Value.ToString(CultureInfo.InvariantCulture)}");
        else if (item.TmdbId > 0) parts.Add($"tmdb_id={item.TmdbId.Value.ToString(CultureInfo.InvariantCulture)}");
        else if (!string.IsNullOrEmpty(item.ImdbId)) parts.Add($"imdb_id={Uri.EscapeDataString(item.ImdbId)}");
        else return null;
        if (!item.IsMovie)
        {
            if (item.Season is null or < 0 || item.Episode is null or <= 0) return null;
            parts.Add($"season={item.Season.Value.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"episode={item.Episode.Value.ToString(CultureInfo.InvariantCulture)}");
            if (item.TvdbId > 0)
            {
                if (item.Mapping is not null) parts.Add($"mapping={Uri.EscapeDataString(item.Mapping)}");
                if (item.TvdbEpisodeId > 0) parts.Add($"tvdb_episode_id={item.TvdbEpisodeId.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        if (item.RuntimeTicks > 0) parts.Add($"duration_ms={(item.RuntimeTicks.Value / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture)}");
        return "media?" + string.Join('&', parts);
    }

    public ProviderPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonRanges.RequireObject(root);
        var fields = new[] { ("intro", SegmentKind.Intro), ("recap", SegmentKind.Recap), ("credits", SegmentKind.Outro), ("preview", SegmentKind.Preview) };
        var hasIdentity = root.TryGetProperty("tvdb_id", out var id) && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var tvdbId) && tvdbId > 0
            && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() is "movie" or "tv";
        if (root.TryGetProperty("error", out _) || (!hasIdentity && !fields.Any(f => root.TryGetProperty(f.Item1, out _))))
            throw new JsonException("Unrecognized TheIntroDB media response.");
        var list = new List<RawSegment>();
        foreach (var (field, kind) in fields)
        {
            // v4 omits a type entirely when it has no submissions for that type.
            if (!root.TryGetProperty(field, out var ranges) || ranges.ValueKind == JsonValueKind.Null) continue;
            if (ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() > 256) throw new JsonException("Expected bounded segment array.");
            foreach (var value in ranges.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Null) continue;
                if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected timestamp object.");
                list.Add(new(kind, JsonRanges.Number(value, "start_ms"), JsonRanges.Number(value, "end_ms")));
            }
        }
        return new(list.ToArray(), []);
    }
}
