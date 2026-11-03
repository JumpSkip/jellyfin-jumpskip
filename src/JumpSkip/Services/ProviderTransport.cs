using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JumpSkip.Configuration;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Services;

public sealed record TransportResult(int StatusCode, string Body, DateTimeOffset? RetryAt = null);

public sealed class ProviderTransport(IHttpClientFactory clients, CacheStore cache, ILogger<ProviderTransport> logger) : IDisposable
{
    private sealed class Gate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset LastSend { get; set; }
        public int Failures { get; set; }
    }
    private readonly Dictionary<string, Gate> _gates = new() { ["IntroDB"] = new(), ["TheIntroDB"] = new(), ["SkipDB"] = new() };
    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string ProviderKey(string name, ProviderSettings settings) => Fingerprint(name + "|" + settings.BaseUrl.TrimEnd('/') + "|" + settings.ApiKey);

    public async Task<TransportResult> GetAsync(string provider, ProviderSettings settings, string path, int timeout, CancellationToken token)
    {
        var gate = _gates[provider];
        var key = ProviderKey(provider, settings);
        await gate.Lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var until = cache.Cooldown(key);
                if (until > DateTimeOffset.UtcNow) return new(429, string.Empty, until);
                var floor = MinimumSpacing(provider, settings);
                var delay = gate.LastSend.AddMilliseconds(Math.Max(floor, settings.MinimumIntervalMs)) - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                gate.LastSend = DateTimeOffset.UtcNow;
                using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl.TrimEnd('/') + "/" + path);
                request.Headers.Accept.Add(new("application/json"));
                request.Headers.UserAgent.ParseAdd("JumpSkip/" + typeof(Plugin).Assembly.GetName().Version);
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    if (provider == "TheIntroDB") request.Headers.Authorization = new("Bearer", settings.ApiKey.Trim());
                    else request.Headers.Add("X-API-Key", settings.ApiKey.Trim());
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    using var client = clients.CreateClient("JumpSkip");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (status is 429 or 503 || status >= 500)
                    {
                        gate.Failures++;
                        var pause = RetryDelay(response.Headers, body, DateTimeOffset.UtcNow, gate.Failures);
                        until = DateTimeOffset.UtcNow + pause;
                        cache.SetCooldown(key, until);
                        logger.LogWarning("JumpSkip: {Provider} returned {Status}; requests resume after {Until}.", provider, status, until);
                        if (attempt < 2 && pause <= TimeSpan.FromSeconds(2))
                        { await Task.Delay(pause, token).ConfigureAwait(false); continue; }
                        return new(status, string.Empty, until);
                    }
                    gate.Failures = 0;
                    if (status is 401 or 403) cache.SetCooldown(key, DateTimeOffset.UtcNow.AddMinutes(5));
                    foreach (var prefix in new[] { "X-UsageLimit", "X-RateLimit" })
                    {
                        if (Header(response.Headers, prefix + "-Remaining") is <= 1
                            && Header(response.Headers, prefix + "-Reset") is > 0 and var seconds)
                            cache.SetCooldown(key, DateTimeOffset.UtcNow.AddSeconds(Math.Min(seconds, 86400)));
                    }
                    return new(status, body, status is 401 or 403 ? cache.Cooldown(key) : null);
                }
                catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !token.IsCancellationRequested))
                {
                    gate.Failures++;
                    until = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 10 * gate.Failures));
                    cache.SetCooldown(key, until);
                    logger.LogWarning("JumpSkip: {Provider} request failed ({Reason}); cooling down until {Until}.", provider, e.GetType().Name, until);
                    return new(0, string.Empty, until);
                }
            }
            throw new InvalidOperationException("Request retry budget exhausted unexpectedly.");
        }
        finally { gate.Lock.Release(); }
    }

    internal static int MinimumSpacing(string provider, ProviderSettings settings) => provider == "SkipDB" ? 500
        : provider == "IntroDB" || !string.IsNullOrWhiteSpace(settings.ApiKey) ? 100 : 400;

    internal static TimeSpan RetryDelay(HttpResponseHeaders headers, string body, DateTimeOffset now, int failures)
    {
        var values = new List<double>();
        if (headers.RetryAfter?.Delta is { } delta) values.Add(delta.TotalSeconds);
        if (headers.RetryAfter?.Date is { } date) values.Add((date - now).TotalSeconds);
        foreach (var header in new[] { "X-UsageLimit-Reset", "X-RateLimit-Reset" })
            if (Header(headers, header) is > 0 and var seconds) values.Add(seconds);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("reset_at", out var reset))
            {
                if (reset.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) values.Add((parsed - now).TotalSeconds);
                if (reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var epoch))
                    values.Add(((epoch > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch)) - now).TotalSeconds);
            }
            if (values.Count == 0 && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.GetString() is "usage_limit_exceeded" or "specific_media_usage_limit_exceeded")
                values.Add((new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) - now).TotalSeconds);
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException or InvalidOperationException) { }
        return TimeSpan.FromSeconds(Math.Clamp(values.Count > 0 ? values.Max() : Math.Min(300, 10 * failures), 1, 86400));
    }

    private static double? Header(HttpResponseHeaders headers, string name) => headers.TryGetValues(name, out var values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    public void Dispose() { foreach (var gate in _gates.Values) gate.Lock.Dispose(); }
}
