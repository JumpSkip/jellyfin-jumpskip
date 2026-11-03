using JumpSkip.Models;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace JumpSkip.Services;

public sealed class ChapterWriter(IChapterManager chapters, CacheStore cache)
{
    private const long Tolerance = TimeSpan.TicksPerMillisecond * 50;
    private readonly object[] _locks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public ChapterInfo[] ReadOriginal(BaseItem item)
    {
        if (!chapters.Supports(item)) return [];
        lock (_locks[(uint)item.Id.GetHashCode() % (uint)_locks.Length])
            return Restore(chapters.GetChapters(item.Id).ToArray(), cache.GetChapters(item.Id));
    }

    public void Write(BaseItem item, IEnumerable<Segment> segments, bool enabled)
    {
        if (!chapters.Supports(item)) return;
        lock (_locks[(uint)item.Id.GetHashCode() % (uint)_locks.Length])
        {
            var previous = chapters.GetChapters(item.Id).ToArray();
            var original = Restore(previous, cache.GetChapters(item.Id));
            var ranges = enabled ? segments.OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToArray() : [];
            var output = ranges.Length == 0 ? original : Build(original, ranges, item.RunTimeTicks, item is Movie);
            if (SameLayout(previous, output))
            {
                if (ranges.Length == 0) cache.ForgetChapters(item.Id);
                return;
            }
            if (ranges.Length != 0) cache.RecordChapters(item.Id, new(original, output));
            chapters.SaveChapters(item, output);
            if (ranges.Length == 0) cache.ForgetChapters(item.Id);
        }
    }

    internal static ChapterInfo[] Build(ChapterInfo[] original, Segment[] segments, long? runtime, bool movie)
    {
        var candidates = new List<(long Ticks, int Priority)> { (0, 1) };
        foreach (var chapter in original)
            if (!segments.Any(s => chapter.StartPositionTicks > s.StartTicks && chapter.StartPositionTicks < s.EndTicks))
                candidates.Add((chapter.StartPositionTicks, 0));
        foreach (var segment in segments)
        {
            candidates.Add((segment.StartTicks, 3));
            candidates.Add((segment.EndTicks, 2));
        }

        // Segment starts win shared/nearby boundaries, including Outro followed by Preview.
        var chosen = new List<long>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Priority).ThenBy(c => c.Ticks))
        {
            if (candidate.Ticks < 0 || (runtime > 0 && candidate.Ticks >= runtime)
                || chosen.Any(t => Math.Abs(t - candidate.Ticks) < Tolerance)) continue;
            chosen.Add(candidate.Ticks);
        }
        return chosen.Order().Select(ticks =>
        {
            var active = segments.FirstOrDefault(s => ticks >= s.StartTicks && ticks < s.EndTicks);
            var originalAtTime = original.Where(c => c.StartPositionTicks <= ticks + Tolerance)
                .MaxBy(c => c.StartPositionTicks);
            var label = active?.Type switch
            {
                null => string.IsNullOrWhiteSpace(originalAtTime?.Name) ? "Chapter" : originalAtTime.Name,
                "Preview" when movie => "Post-Credits",
                "Outro" when movie => "Credits",
                _ => active.Type
            };
            var image = original.FirstOrDefault(c => Math.Abs(c.StartPositionTicks - ticks) < Tolerance);
            return Copy(image, ticks, label);
        }).ToArray();
    }

    private static ChapterInfo[] Restore(ChapterInfo[] current, ChapterPublication? publication)
    {
        // Early JumpSkip releases used a visible ownership prefix. Remove those markers on upgrade.
        current = current.Where(c => c.Name?.StartsWith("[JumpSkip] ", StringComparison.Ordinal) != true).ToArray();
        if (publication is null) return current.Select(c => Copy(c, c.StartPositionTicks, c.Name)).ToArray();
        bool IsPublished(ChapterInfo c) => publication.Published.Any(p => p.StartPositionTicks == c.StartPositionTicks && p.Name == c.Name);
        if (!current.Any(IsPublished)) return current.Select(c => Copy(c, c.StartPositionTicks, c.Name)).ToArray();
        var restored = current.Where(c => !IsPublished(c)).Select(c => Copy(c, c.StartPositionTicks, c.Name)).ToList();
        foreach (var chapter in publication.Original)
        {
            // An external edit at this position wins; restore only our unchanged output.
            if (restored.Any(c => Math.Abs(c.StartPositionTicks - chapter.StartPositionTicks) < Tolerance)) continue;
            var image = current.FirstOrDefault(c => IsPublished(c) && c.StartPositionTicks == chapter.StartPositionTicks);
            restored.Add(Copy(image?.ImagePath is not null ? image : chapter, chapter.StartPositionTicks, chapter.Name));
        }
        return restored.OrderBy(c => c.StartPositionTicks).ToArray();
    }

    private static bool SameLayout(IEnumerable<ChapterInfo> left, IEnumerable<ChapterInfo> right) =>
        left.Select(c => (c.StartPositionTicks, c.Name)).SequenceEqual(right.Select(c => (c.StartPositionTicks, c.Name)));

    private static ChapterInfo Copy(ChapterInfo? source, long ticks, string? name) => new()
    {
        StartPositionTicks = ticks, Name = name, ImagePath = source?.ImagePath,
        ImageDateModified = source?.ImageDateModified ?? default, ImageTag = source?.ImageTag
    };
}
