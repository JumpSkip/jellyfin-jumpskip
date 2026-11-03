using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JumpSkip.Services;

public sealed class LibraryItems(ILibraryManager library)
{
    public IEnumerable<BaseItem> Descendants(Guid parentId, CancellationToken token) => Enumerate(parentId, token);

    public IEnumerable<BaseItem> All(CancellationToken token) => Enumerate(null, token);

    private IEnumerable<BaseItem> Enumerate(Guid? parentId, CancellationToken token)
    {
        var offset = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var items = library.GetItemList(new InternalItemsQuery
            {
                AncestorIds = parentId is { } id ? [id] : [], Recursive = true, IsVirtualItem = false,
                IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie, BaseItemKind.Video],
                StartIndex = offset, Limit = 200, GroupByPresentationUniqueKey = false,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
            });
            foreach (var item in items) yield return item;
            if (items.Count < 200) yield break;
            offset += items.Count;
        }
    }
}
