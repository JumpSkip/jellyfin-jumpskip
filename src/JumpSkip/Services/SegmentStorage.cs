using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;

namespace JumpSkip.Services;

public sealed class SegmentStorage(IServiceProvider services, ILibraryManager library, ChapterWriter chapters, CacheStore cache) : IDisposable
{
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private IMediaSegmentManager Manager => services.GetRequiredService<IMediaSegmentManager>();
    private SemaphoreSlim Gate(Guid id) => _locks[(uint)id.GetHashCode() % (uint)_locks.Length];

    private LibraryOptions OnlyJumpSkip(BaseItem item) => new()
    {
        DisabledMediaSegmentProviders = Manager.GetSupportedProviders(item)
            .Where(p => p.Name != Plugin.PluginName).Select(p => p.Name).ToArray()
    };
    public async Task<MediaSegmentDto[]> ReadOwnAsync(BaseItem item) =>
        (await Manager.GetSegmentsAsync(item, null, OnlyJumpSkip(item), true).ConfigureAwait(false)).ToArray();

    public async Task<bool> HasOtherAsync(BaseItem item)
    {
        var options = library.GetLibraryOptions(item);
        var filtered = new LibraryOptions { DisabledMediaSegmentProviders = options.DisabledMediaSegmentProviders.Concat([Plugin.PluginName]).ToArray() };
        return (await Manager.GetSegmentsAsync(item, null, filtered, true).ConfigureAwait(false)).Any();
    }

    public async Task RefreshAsync(BaseItem item, CancellationToken token)
    {
        if (library.GetLibraryOptions(item).DisabledMediaSegmentProviders.Contains(Plugin.PluginName, StringComparer.OrdinalIgnoreCase)) return;
        var gate = Gate(item.Id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await Manager.RunSegmentPluginProviders(item, OnlyJumpSkip(item), false, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<int> DeleteAsync(BaseItem item, CancellationToken token)
    {
        var gate = Gate(item.Id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var segments = await ReadOwnAsync(item).ConfigureAwait(false);
            foreach (var segment in segments) { token.ThrowIfCancellationRequested(); await Manager.DeleteSegmentAsync(segment.Id).ConfigureAwait(false); }
            chapters.Write(item, [], false);
            cache.RecordChapterSegments(item.Id, []);
            return segments.Length;
        }
        finally { gate.Release(); }
    }
    public void Dispose() { foreach (var gate in _locks) gate.Dispose(); }
}
