using JumpSkip.Models;
using JumpSkip.Configuration;
using JumpSkip.Services;
using JumpSkip.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JumpSkip.Api;

[ApiController]
[Authorize]
[Route("JumpSkip/Segments")]
public sealed class SegmentsController(ILibraryManager library, SegmentService segments, SegmentStorage storage, CacheStore cache,
    LibraryItems items, ItemFilter filter, Func<PluginConfiguration> configuration, ITaskManager tasks) : ControllerBase
{
    [HttpGet("{itemId:guid}")]
    public async Task<ActionResult<SegmentResponse>> Get(Guid itemId, CancellationToken cancellationToken)
    {
        var item = Accessible(itemId);
        if (item is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Ok(await segments.GetAsync(item, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("{itemId:guid}/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<SegmentResponse>> Refresh(Guid itemId, [FromQuery] bool invalidate, CancellationToken cancellationToken)
    {
        var item = itemId == Guid.Empty ? null : library.GetItemById(itemId);
        if (item is null) return NotFound();
        if (invalidate) cache.Invalidate(itemId: itemId);
        await storage.RefreshAsync(item, cancellationToken).ConfigureAwait(false);
        return Ok(await segments.GetAsync(item, cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("{itemId:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Delete(Guid itemId, CancellationToken cancellationToken)
    {
        var item = itemId == Guid.Empty ? null : library.GetItemById(itemId);
        if (item is null) return NotFound();
        var count = await storage.DeleteAsync(item, cancellationToken).ConfigureAwait(false);
        var cleared = cache.Invalidate(itemId: itemId);
        return Ok(new { ItemId = itemId, SegmentsRemoved = count, ClearedEntries = cleared });
    }

    private BaseItem? Accessible(Guid itemId)
    {
        if (itemId == Guid.Empty) return null;
        if (bool.TryParse(User.FindFirst("Jellyfin-IsApiKey")?.Value, out var apiKey) && apiKey) return library.GetItemById(itemId);
        return Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) && userId != Guid.Empty
            ? library.GetItemById<BaseItem>(itemId, userId) : null;
    }

    [HttpPost("Remove")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Remove([FromBody] RemoveSegmentsRequest request, CancellationToken cancellationToken)
    {
        if ((request.ItemIds is { Length: > 0 } ? 1 : 0) + (request.ParentId.HasValue ? 1 : 0) + (request.ScanScope ? 1 : 0) + (request.All ? 1 : 0) != 1
            || request.ItemIds is { Length: > 1000 } || request.ItemIds?.Contains(Guid.Empty) == true || request.ParentId == Guid.Empty)
            return BadRequest(new { Error = "Specify 1–1000 ItemIds, one ParentId (series/library), ScanScope: true, or All: true." });
        if (request.ParentId is { } parent && library.GetItemById(parent) is not Folder) return NotFound();
        var config = configuration();
        if (request.All) tasks.CancelIfRunning<JumpSkipScheduledTask>();
        var targets = request.All ? items.All(cancellationToken) : request.ScanScope
            ? items.All(cancellationToken).Where(item => filter.MatchesScanScope(item, config))
            : request.ParentId is { } id ? items.Descendants(id, cancellationToken)
            : request.ItemIds!.Distinct().Select(library.GetItemById).OfType<BaseItem>();
        var results = new List<object>();
        if (request.ItemIds is { } requestedIds)
            foreach (var missing in requestedIds.Distinct().Where(id => library.GetItemById(id) is null))
                results.Add(new { ItemId = missing, SegmentsRemoved = 0, Error = "NotFound" });
        var removed = 0;
        foreach (var item in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = await storage.DeleteAsync(item, cancellationToken).ConfigureAwait(false);
                cache.Invalidate(itemId: item.Id);
                removed += count;
                results.Add(new { ItemId = item.Id, SegmentsRemoved = count, Error = (string?)null });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { results.Add(new { ItemId = item.Id, SegmentsRemoved = 0, Error = e.GetType().Name }); }
        }
        return Ok(new { TotalItems = results.Count, TotalSegmentsRemoved = removed, Results = results });
    }
}

public sealed record RemoveSegmentsRequest(Guid[]? ItemIds = null, Guid? ParentId = null, bool ScanScope = false, bool All = false);
