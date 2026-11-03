using System.Globalization;
using System.Text.Json;
using JumpSkip.Models;

namespace JumpSkip.Providers;

public sealed class IntroDbClient : ISegmentSource
{
    public string Name => "IntroDB";
    public string? GetPath(MediaLookup item)
    {
        if (string.IsNullOrEmpty(item.ImdbId) || item.UnsupportedOrder || item.Mapping is not null) return null;
        var id = Uri.EscapeDataString(item.ImdbId);
        if (item.IsMovie) return $"segments?imdb_id={id}&is_movie=true";
        return item.Season > 0 && item.Episode > 0
            ? string.Create(CultureInfo.InvariantCulture, $"segments?imdb_id={id}&season={item.Season}&episode={item.Episode}") : null;
    }

    public ProviderPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonRanges.RequireObject(root, "intro", "recap", "outro");
        var list = new List<RawSegment>();
        foreach (var (field, kind) in new[] { ("intro", SegmentKind.Intro), ("recap", SegmentKind.Recap), ("outro", SegmentKind.Outro) })
        {
            var value = root.GetProperty(field);
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected segment object.");
            list.Add(new(kind, JsonRanges.Number(value, "start_ms"), JsonRanges.Number(value, "end_ms")));
        }
        if (root.TryGetProperty("post_credits", out var post) && post.ValueKind != JsonValueKind.Null)
        {
            if (post.ValueKind != JsonValueKind.Object) throw new JsonException("Expected post-credits object.");
            var start = JsonRanges.Number(post, "start_ms");
            var end = JsonRanges.Number(post, "end_ms");
            list.Add(new(SegmentKind.PostCredits, start, end));
        }
        var absent = new[] { "no_intro", "no_recap", "no_outro" }
            .All(n => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True);
        return new(list.ToArray(), [], absent);
    }
}

internal static class JsonRanges
{
    internal static void RequireObject(JsonElement root, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object || fields.Any(n => !root.TryGetProperty(n, out _)))
            throw new JsonException("Unrecognized provider response.");
    }
    internal static double? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Expected numeric timestamp.");
        return number;
    }
}
