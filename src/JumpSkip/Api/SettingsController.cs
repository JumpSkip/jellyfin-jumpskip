using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using JumpSkip.Configuration;
using JumpSkip.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JumpSkip.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JumpSkip/Settings")]
public sealed class SettingsController(Func<PluginConfiguration> configuration, ProviderTransport transport, SettingsService settings) : ControllerBase
{
    [HttpGet]
    public ActionResult<PluginConfiguration> Get()
    { Response.Headers.CacheControl = "no-store"; return Ok(configuration()); }

    [HttpPut]
    public async Task<ActionResult> Put([FromBody] PluginConfiguration config, CancellationToken cancellationToken)
    {
        try { return Ok(await settings.SaveAsync(config, cancellationToken).ConfigureAwait(false)); }
        catch (ValidationException e) { return BadRequest(new { Error = e.Message }); }
    }

    [HttpPost("ValidateKey")]
    public async Task<ActionResult> ValidateKey(CancellationToken cancellationToken)
    {
        var config = configuration();
        if (string.IsNullOrWhiteSpace(config.TheIntroDb.ApiKey)) return BadRequest(new { Error = "Save a TheIntroDB API key first." });
        var response = await transport.GetAsync("TheIntroDB", config.TheIntroDb, "user/stats", config.RequestTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != 200) return Ok(new { IsValid = false, response.StatusCode, response.RetryAt });
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("total", out var total)
                || total.ValueKind != JsonValueKind.Number || doc.RootElement.TryGetProperty("error", out _))
                return Ok(new { IsValid = false, StatusCode = 502 });
            return Ok(new { IsValid = true, StatusCode = 200, Stats = doc.RootElement.Clone() });
        }
        catch (JsonException) { return Ok(new { IsValid = false, StatusCode = 502 }); }
    }
}
