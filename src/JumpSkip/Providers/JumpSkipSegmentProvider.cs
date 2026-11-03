using JumpSkip.Configuration;
using JumpSkip.Models;
using JumpSkip.Services;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace JumpSkip.Providers;

public sealed class JumpSkipSegmentProvider(ILibraryManager library, SegmentService segments,
    ChapterWriter chapters, CacheStore cache, Func<PluginConfiguration> configuration, ItemFilter filter) : IMediaSegmentProvider
{
    public string Name => Plugin.PluginName;
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Video && !item.IsVirtualItem);

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var item = library.GetItemById(request.ItemId);
        if (item is null) return [];
        var config = configuration();
        if (!config.Enabled || filter.IsBlacklisted(item, config))
        {
            chapters.Write(item, [], false);
            cache.RecordChapterSegments(item.Id, []);
            return [];
        }
        var existing = request.ExistingSegments?.ToArray() ?? [];
        var response = await segments.GetAsync(item, cancellationToken).ConfigureAwait(false);
        // Settings can disable JumpSkip or blacklist this item during an in-flight request.
        config = configuration();
        if (!config.Enabled || filter.IsBlacklisted(item, config))
        {
            chapters.Write(item, [], false);
            cache.RecordChapterSegments(item.Id, []);
            return [];
        }
        if (response.Status is "Excluded" or "UnsupportedIdentity" or "OtherProviderSegments") return existing;
        var output = response.Segments;
        chapters.Write(item, output, config.InjectChapters);
        cache.RecordChapterSegments(item.Id, output.Where(s => s.Provider == "Chapters").ToArray());
        return output.Select(s => new MediaSegmentDto
        { ItemId = item.Id, Type = Enum.Parse<MediaSegmentType>(s.Type), StartTicks = s.StartTicks, EndTicks = s.EndTicks }).ToArray();
    }

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cache.Invalidate(itemId: itemId);
        cache.RecordChapterSegments(itemId, []);
        var item = library.GetItemById(itemId);
        if (item is not null) chapters.Write(item, [], false);
        else cache.ForgetChapters(itemId);
        return Task.CompletedTask;
    }
}
