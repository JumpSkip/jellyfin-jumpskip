using System.Text.RegularExpressions;
using JumpSkip.Configuration;
using JumpSkip.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace JumpSkip.Services;

public sealed class ChapterFallback(ChapterWriter chapters)
{
    public Segment[] Apply(BaseItem item, Segment[] providers, IEnumerable<ProviderResult> sources, PluginConfiguration config) =>
        !config.EnableChapterSkipping ? providers : Append(chapters.ReadOriginal(item), providers, item.RunTimeTicks, config,
            SegmentMerger.Protection(sources, item.RunTimeTicks));

    internal static Segment[] Append(ChapterInfo[] chapters, Segment[] providers, long? runtime,
        PluginConfiguration config, Segment[]? protection = null)
    {
        if (!config.EnableChapterSkipping) return providers;
        var categories = new[] { ("Intro", config.ChapterIntroKeywords), ("Outro", config.ChapterOutroKeywords),
            ("Recap", config.ChapterRecapKeywords), ("Preview", config.ChapterPreviewKeywords) }
            .Where(c => config.Allows(c.Item1)).Select(c => (Type: c.Item1, Keywords: c.Item2
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        var boundaries = chapters.Where(c => c.StartPositionTicks >= 0 && (runtime is not > 0 || c.StartPositionTicks < runtime))
            .OrderBy(c => c.StartPositionTicks).GroupBy(c => c.StartPositionTicks).ToArray();
        var accepted = providers.ToList();
        var minimum = Math.Max(config.ChapterMinDurationSeconds, config.MinimumSegmentSeconds);
        for (var i = 0; i < boundaries.Length; i++)
        {
            var start = boundaries[i].Key;
            // The final chapter needs a known runtime; never guess where it ends.
            var end = i + 1 < boundaries.Length ? boundaries[i + 1].Key : runtime ?? 0;
            if (end <= start) continue;
            var duration = (end - start) / (double)TimeSpan.TicksPerSecond;
            if (duration < config.ChapterMinDurationSeconds || duration > config.ChapterMaxDurationSeconds) continue;
            // Category order resolves ambiguous titles: Intro, Outro, Recap, Preview.
            var type = categories.FirstOrDefault(c => boundaries[i].Any(ch => c.Keywords.Any(k => Matches(ch.Name, k)))).Type;
            if (type is null) continue;
            var candidate = SegmentMerger.ApplyOffsets(new(type, start, end, "Chapters"), runtime, config);
            if (candidate is null) continue;
            var overlap = OverlapTicks(candidate, providers);
            if (overlap > 0 && overlap / (double)(candidate.EndTicks - candidate.StartTicks) >= config.ChapterOverlapThreshold) continue;
            var pieces = new List<Segment> { candidate };
            foreach (var prior in accepted) pieces = SegmentMerger.Subtract(pieces, prior);
            if (type == "Outro")
                foreach (var scene in protection ?? []) pieces = SegmentMerger.Subtract(pieces, scene);
            accepted.AddRange(pieces.Where(p => (p.EndTicks - p.StartTicks) / (double)TimeSpan.TicksPerSecond >= minimum
                && (p.EndTicks - p.StartTicks) / (double)TimeSpan.TicksPerSecond <= config.ChapterMaxDurationSeconds));
        }
        return accepted.OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToArray();
    }

    internal static bool Matches(string? title, string keyword)
    {
        if (string.IsNullOrWhiteSpace(title) || keyword.Length == 0) return false;
        return keyword.Length > 3 ? title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            : Regex.IsMatch(title, @"(?<![\p{L}\p{M}\p{N}_])" + Regex.Escape(keyword) + @"(?![\p{L}\p{M}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static long OverlapTicks(Segment chapter, IEnumerable<Segment> providers)
    {
        var covered = 0L;
        var cursor = chapter.StartTicks;
        foreach (var segment in providers.OrderBy(s => s.StartTicks))
        {
            var start = Math.Max(cursor, segment.StartTicks);
            var end = Math.Min(chapter.EndTicks, segment.EndTicks);
            if (end <= start) continue;
            covered += end - start;
            cursor = end;
        }
        return covered;
    }
}
