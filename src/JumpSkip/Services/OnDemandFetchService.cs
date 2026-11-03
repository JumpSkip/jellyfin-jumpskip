using JumpSkip.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Services;

public sealed class OnDemandFetchService(ILibraryManager library, ISessionManager sessions, SegmentStorage storage,
    CacheStore cache, ItemFilter filter, Func<PluginConfiguration> configuration, ILogger<OnDemandFetchService> logger) : BackgroundService
{
    private readonly PendingFetchQueue _queue = new(10000);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded += Added;
        library.ItemUpdated += Updated;
        library.ItemRemoved += Removed;
        sessions.PlaybackStart += Playing;
        return base.StartAsync(cancellationToken);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        library.ItemAdded -= Added;
        library.ItemUpdated -= Updated;
        library.ItemRemoved -= Removed;
        sessions.PlaybackStart -= Playing;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        cache.Flush();
    }
    private void Added(object? sender, ItemChangeEventArgs e) => Enqueue(e.Item, configuration().NewItemDelaySeconds);
    private void Updated(object? sender, ItemChangeEventArgs e)
    {
        if ((e.UpdateReason & (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit | ItemUpdateType.MetadataImport)) != 0) Enqueue(e.Item, 1);
    }
    private void Removed(object? sender, ItemChangeEventArgs e)
    {
        _queue.Remove(e.Item.Id);
        cache.Invalidate(itemId: e.Item.Id);
    }
    private void Playing(object? sender, PlaybackProgressEventArgs e) => Enqueue(e.Item, 0);
    private void Enqueue(BaseItem? item, int seconds)
    {
        var config = configuration();
        if (item is not Video || item.IsVirtualItem || !config.Enabled || !config.EnableOnDemandFetch) return;
        _queue.Enqueue(item.Id, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(Worker(stoppingToken), Worker(stoppingToken), FlushLoop(stoppingToken));
    private async Task Worker(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var itemId in _queue.TakeDue(DateTimeOffset.UtcNow, 20))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var item = library.GetItemById(itemId);
                    var config = configuration();
                    if (item is not null && config.EnableOnDemandFetch && filter.Allows(item, config))
                        await storage.RefreshAsync(item, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception e) { logger.LogWarning("JumpSkip on-demand fetch failed for {ItemId}: {Reason}.", itemId, e.GetType().Name); }
            }
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }
    private async Task FlushLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) cache.Flush();
    }
}
