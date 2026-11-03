using JumpSkip.Configuration;
using JumpSkip.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Tasks;

public sealed class JumpSkipScheduledTask(ILibraryManager library, SegmentStorage storage, ItemFilter filter,
    CacheStore cache, Func<PluginConfiguration> configuration, ILogger<JumpSkipScheduledTask> logger) : IScheduledTask
{
    public string Name => "JumpSkip segment refresh";
    public string Key => "JumpSkip.Refresh";
    public string Description => "Refresh JumpSkip segments and chapters for eligible library items, respecting enabled providers' caches and cooldowns.";
    public string Category => "JumpSkip";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var start = 0;
        const int pageSize = 200;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video], IsVirtualItem = false,
                    Recursive = true, StartIndex = start, Limit = pageSize, OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                    GroupByPresentationUniqueKey = false
                };
                var result = library.GetItemsResult(query);
                foreach (var item in result.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var config = configuration();
                        var blacklisted = filter.IsBlacklisted(item, config);
                        if (!config.Enabled || blacklisted)
                        {
                            await storage.DeleteAsync(item, cancellationToken).ConfigureAwait(false);
                            if (blacklisted) cache.Invalidate(itemId: item.Id);
                        }
                        else if (filter.Allows(item, config)) await storage.RefreshAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception e) { logger.LogWarning("JumpSkip scan failed for {ItemId}: {Reason}.", item.Id, e.GetType().Name); }
                    progress.Report(Math.Min(99, 100d * ++start / Math.Max(1, result.TotalRecordCount)));
                }
                if (result.Items.Count < pageSize) break;
            }
            progress.Report(100);
        }
        finally { cache.Flush(); }
    }
}
