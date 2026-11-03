using JumpSkip.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace JumpSkip.Services;

public sealed class ItemFilter(ILibraryManager library)
{
    public bool Allows(BaseItem item, PluginConfiguration config)
    {
        if (!config.Enabled || item is not Video || item.IsVirtualItem) return false;
        if (library.GetLibraryOptions(item).DisabledMediaSegmentProviders.Contains(Plugin.PluginName, StringComparer.OrdinalIgnoreCase)) return false;
        return MatchesScanScope(item, config);
    }

    public bool IsBlacklisted(BaseItem item, PluginConfiguration config) =>
        Matches(item, config.ExcludedItemIds, config.ExcludedLibraryIds);

    // JumpSkip's retired whitelist arrays remain readable but no longer restrict scanning.
    public bool MatchesScanScope(BaseItem item, PluginConfiguration config) =>
        item is Video && !item.IsVirtualItem
        && !IsBlacklisted(item, config);

    private bool Matches(BaseItem item, Guid[] itemIds, Guid[] libraryIds) =>
        itemIds.Contains(item.Id) || (item is Episode ep && itemIds.Contains(ep.SeriesId))
        || (libraryIds.Length > 0 && library.GetItemList(new InternalItemsQuery
        { ItemIds = [item.Id], AncestorIds = libraryIds }).Count > 0);
}
