using JumpSkip.Configuration;
using JumpSkip.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Services;

public sealed record SettingsSaveResult(bool Saved, bool RefreshQueued, int BlacklistedItemsCleaned, int CleanupFailures, int DisabledItemsCleaned);

public sealed class SettingsService(Action<PluginConfiguration> saveConfiguration, LibraryItems items,
    ItemFilter filter, SegmentStorage storage, CacheStore cache, ITaskManager tasks, ILogger<SettingsService> logger)
{
    public async Task<SettingsSaveResult> SaveAsync(PluginConfiguration config, CancellationToken token)
    {
        config.Validate();
        saveConfiguration(config);
        var cleaned = 0;
        var disabledCleaned = 0;
        var failures = 0;
        try
        {
            // Disabling must withdraw existing output as well as stopping future fetches.
            if (!config.Enabled || config.ExcludedLibraryIds.Length > 0 || config.ExcludedItemIds.Length > 0)
                foreach (var item in items.All(token))
                {
                    var blacklisted = filter.IsBlacklisted(item, config);
                    if (config.Enabled && !blacklisted) continue;
                    try
                    {
                        await storage.DeleteAsync(item, token).ConfigureAwait(false);
                        if (blacklisted) { cache.Invalidate(itemId: item.Id); cleaned++; }
                        if (!config.Enabled) disabledCleaned++;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        failures++;
                        logger.LogWarning("JumpSkip settings cleanup failed for {ItemId}: {Reason}.", item.Id, e.GetType().Name);
                    }
                }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failures++;
            logger.LogWarning("JumpSkip settings cleanup scan failed: {Reason}.", e.GetType().Name);
        }
        finally
        {
            cache.Flush();
            // Restart an active scan so items it already passed are refreshed with the new settings.
            // Also retry disable/blacklist cleanup if the HTTP request was interrupted after saving.
            tasks.CancelIfRunningAndQueue<JumpSkipScheduledTask>();
        }
        return new(true, true, cleaned, failures, disabledCleaned);
    }
}
