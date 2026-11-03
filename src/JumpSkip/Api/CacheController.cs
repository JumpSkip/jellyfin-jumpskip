using JumpSkip.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JumpSkip.Api;

public sealed record InvalidateRequest(Guid? ItemId = null, Guid? SeriesId = null, bool All = false, bool NegativeOnly = false);

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JumpSkip/Cache")]
public sealed class CacheController(CacheStore cache) : ControllerBase
{
    [HttpGet]
    public ActionResult Get() => Ok(cache.Status());

    [HttpPost("Invalidate")]
    public ActionResult Invalidate([FromBody] InvalidateRequest request)
    {
        if ((request.ItemId.HasValue ? 1 : 0) + (request.SeriesId.HasValue ? 1 : 0) + (request.All ? 1 : 0) != 1
            || request.ItemId == Guid.Empty || request.SeriesId == Guid.Empty)
            return BadRequest(new { Error = "Specify exactly one nonempty ItemId, SeriesId, or All=true." });
        return Ok(new { ClearedEntries = cache.Invalidate(request.ItemId, request.SeriesId, request.NegativeOnly) });
    }
}
