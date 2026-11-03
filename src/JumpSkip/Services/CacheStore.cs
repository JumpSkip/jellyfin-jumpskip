using System.Text.Json;
using JumpSkip.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JumpSkip.Services;

public sealed record CacheEntry(ProviderResult Result, DateTimeOffset ExpiresAt, DateTimeOffset RetainUntil,
    HashSet<Guid> ItemIds, HashSet<Guid> SeriesIds);
public sealed record ChapterPublication(ChapterInfo[] Original, ChapterInfo[] Published);
public sealed class CacheSnapshot
{
    public int Version { get; set; } = 1;
    public Dictionary<string, CacheEntry> Entries { get; set; } = new();
    public Dictionary<string, DateTimeOffset> Cooldowns { get; set; } = new();
    public Dictionary<Guid, ChapterPublication> Chapters { get; set; } = new();
    public Dictionary<Guid, Segment[]> ChapterSegments { get; set; } = new();
}

public sealed class CacheStore
{
    private readonly object _sync = new();
    private readonly string? _path;
    private readonly ILogger<CacheStore> _logger;
    private CacheSnapshot _state = new();
    private long _generation;
    private int _changes;
    public CacheStore(string? path, ILogger<CacheStore> logger)
    {
        _path = path;
        _logger = logger;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var value = JsonSerializer.Deserialize<CacheSnapshot>(File.ReadAllText(path));
            if (value?.Version == 1 && value.Entries is not null && value.Cooldowns is not null)
            {
                foreach (var key in value.Entries.Where(p => p.Value is null || p.Value.Result is null
                    || p.Value.ItemIds is null || p.Value.SeriesIds is null
                    || p.Value.Result.Payload is { Segments: null } or { ProtectedRanges: null }
                    || p.Value.Result.Payload?.Segments.Any(s => s is null) == true
                    || p.Value.Result.Payload?.ProtectedRanges.Any(s => s is null) == true)
                    .Select(p => p.Key).ToArray()) value.Entries.Remove(key);
                _state = value;
                _state.Chapters ??= new();
                _state.ChapterSegments ??= new();
                foreach (var key in _state.ChapterSegments.Where(p => p.Value is null
                    || p.Value.Any(s => s is null || s.StartTicks < 0 || s.EndTicks <= s.StartTicks)).Select(p => p.Key).ToArray())
                    _state.ChapterSegments.Remove(key);
                foreach (var key in _state.Chapters.Where(p => p.Value is null || p.Value.Original is null || p.Value.Published is null
                    || p.Value.Original.Any(c => c is null) || p.Value.Published.Any(c => c is null)).Select(p => p.Key).ToArray())
                    _state.Chapters.Remove(key);
            }
            Prune(DateTimeOffset.UtcNow, 1000000);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { logger.LogWarning("JumpSkip cache could not be read ({Reason}); rebuilding it.", e.GetType().Name); }
    }

    public long Generation { get { lock (_sync) return _generation; } }
    public ChapterPublication? GetChapters(Guid itemId)
    { lock (_sync) return _state.Chapters.GetValueOrDefault(itemId); }

    public void RecordChapters(Guid itemId, ChapterPublication publication)
    {
        lock (_sync)
        {
            _state.Chapters[itemId] = publication;
            _changes++;
            // Save ownership before replacing names with ordinary labels such as Chapter/Preview.
            if (!Flush()) throw new IOException("Cannot preserve the original chapter list; chapter publication was skipped.");
        }
    }

    public void ForgetChapters(Guid itemId)
    { lock (_sync) { if (_state.Chapters.Remove(itemId)) { _changes++; Flush(); } } }

    public Segment[] GetChapterSegments(Guid itemId)
    { lock (_sync) return _state.ChapterSegments.GetValueOrDefault(itemId) ?? []; }

    public void RecordChapterSegments(Guid itemId, Segment[] segments)
    {
        lock (_sync)
        {
            if (segments.Length == 0)
            {
                if (_state.ChapterSegments.Remove(itemId)) _changes++;
            }
            else if (!_state.ChapterSegments.TryGetValue(itemId, out var old) || !old.SequenceEqual(segments))
            {
                _state.ChapterSegments[itemId] = segments;
                _changes++;
            }
            // An identical retry must persist any previous failed write before publication can continue.
            if (!Flush()) throw new IOException("Cannot preserve chapter segment ownership.");
        }
    }

    public CacheEntry? Get(string key, Guid itemId, Guid seriesId)
    {
        lock (_sync)
        {
            if (!_state.Entries.TryGetValue(key, out var entry)) return null;
            if (entry.RetainUntil <= DateTimeOffset.UtcNow) { _state.Entries.Remove(key); _changes++; return null; }
            if (entry.ItemIds.Add(itemId)) _changes++;
            if (seriesId != Guid.Empty && entry.SeriesIds.Add(seriesId)) _changes++;
            return entry;
        }
    }

    public void Put(string key, CacheEntry entry, long generation, int capacity)
    {
        lock (_sync)
        {
            if (generation != _generation) return;
            if (_state.Entries.TryGetValue(key, out var old))
            {
                entry.ItemIds.UnionWith(old.ItemIds);
                entry.SeriesIds.UnionWith(old.SeriesIds);
            }
            _state.Entries[key] = entry;
            _changes++;
            if (_state.Entries.Count > capacity) Prune(DateTimeOffset.UtcNow, capacity);
            if (_changes >= 25) Flush();
        }
    }

    public DateTimeOffset Cooldown(string providerKey)
    { lock (_sync) return _state.Cooldowns.GetValueOrDefault(providerKey); }

    public void SetCooldown(string providerKey, DateTimeOffset until)
    {
        lock (_sync)
        {
            if (until > _state.Cooldowns.GetValueOrDefault(providerKey))
            { _state.Cooldowns[providerKey] = until; _changes++; Flush(); }
        }
    }

    public int Invalidate(Guid? itemId = null, Guid? seriesId = null, bool negativeOnly = false)
    {
        lock (_sync)
        {
            _generation++;
            var keys = _state.Entries.Where(p =>
                (!itemId.HasValue || p.Value.ItemIds.Contains(itemId.Value)) &&
                (!seriesId.HasValue || p.Value.SeriesIds.Contains(seriesId.Value)) &&
                (!negativeOnly || p.Value.Result.Status is FetchStatus.NotFound or FetchStatus.ConfirmedAbsent))
                .Select(p => p.Key).ToArray();
            // Still advance the generation for in-flight requests, but avoid rewriting an unchanged cache.
            if (keys.Length == 0) return 0;
            foreach (var key in keys) _state.Entries.Remove(key);
            _changes++;
            Flush();
            return keys.Length;
        }
    }

    public object Status()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            return new
            {
                Entries = _state.Entries.Count,
                Fresh = _state.Entries.Count(p => p.Value.ExpiresAt > now),
                Negative = _state.Entries.Count(p => p.Value.ExpiresAt > now && p.Value.Result.Status is FetchStatus.NotFound or FetchStatus.ConfirmedAbsent),
                Cooldowns = _state.Cooldowns.Values.Where(d => d > now).Order().ToArray()
            };
        }
    }

    public bool Flush()
    {
        lock (_sync)
        {
            if (_path is null || _changes == 0) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_state));
                File.Move(_path + ".tmp", _path, true);
                _changes = 0;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { _logger.LogWarning("JumpSkip cache save failed ({Reason}); changes remain in memory.", e.GetType().Name); return false; }
        }
    }

    private void Prune(DateTimeOffset now, int capacity)
    {
        foreach (var key in _state.Entries.Where(p => p.Value.RetainUntil <= now).Select(p => p.Key).ToArray()) _state.Entries.Remove(key);
        foreach (var key in _state.Entries.OrderBy(p => p.Value.ExpiresAt).Take(Math.Max(0, _state.Entries.Count - capacity)).Select(p => p.Key).ToArray()) _state.Entries.Remove(key);
        foreach (var key in _state.Cooldowns.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _state.Cooldowns.Remove(key);
        _changes++;
    }
}
