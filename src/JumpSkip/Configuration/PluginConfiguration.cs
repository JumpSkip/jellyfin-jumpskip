using System.ComponentModel.DataAnnotations;
using MediaBrowser.Model.Plugins;

namespace JumpSkip.Configuration;

public sealed class ProviderSettings
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int MinimumIntervalMs { get; set; } = 400;
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public ProviderSettings IntroDb { get; set; } = new() { BaseUrl = "https://api.introdb.app", MinimumIntervalMs = 100 };
    public ProviderSettings TheIntroDb { get; set; } = new() { BaseUrl = "https://api.theintrodb.org/v4", MinimumIntervalMs = 400 };
    public ProviderSettings SkipDb { get; set; } = new() { BaseUrl = "https://api.skipdb.tv", MinimumIntervalMs = 500 };
    private string[] _providerPriority = ["TheIntroDB", "IntroDB", "SkipDB"];
    public string[] ProviderPriority
    {
        get => _providerPriority;
        // Accept saved two-provider configurations without changing their relative order.
        set => _providerPriority = value is { Length: 2 } && value.Contains("IntroDB") && value.Contains("TheIntroDB")
            ? [.. value, "SkipDB"] : value;
    }
    public string MergeStrategy { get; set; } = "FillGaps";
    public bool MergeProviderSegments { get; set; } = true;
    public bool EnableIntro { get; set; } = true;
    public bool EnableRecap { get; set; } = true;
    public bool EnableOutro { get; set; } = true;
    public bool EnablePreview { get; set; } = true;
    public bool EnableTimingOffsets { get; set; }
    public double StartOffsetSeconds { get; set; }
    public double EndOffsetSeconds { get; set; }
    public bool EnableChapterSkipping { get; set; } = true;
    public string ChapterIntroKeywords { get; set; } = "intro,introduction,opening,op,theme,title sequence";
    public string ChapterOutroKeywords { get; set; } = "ed,ending,outro,credits,end credits,closing";
    public string ChapterRecapKeywords { get; set; } = "recap,previously";
    public string ChapterPreviewKeywords { get; set; } = "preview,next episode";
    public double ChapterMinDurationSeconds { get; set; } = 10;
    public double ChapterMaxDurationSeconds { get; set; } = 600;
    public double ChapterOverlapThreshold { get; set; } = 0.5;
    public double DeduplicationToleranceSeconds { get; set; } = 1;
    public double MinimumSegmentSeconds { get; set; } = 3;
    public Guid[] SelectedLibraryIds { get; set; } = [];
    public Guid[] SelectedItemIds { get; set; } = [];
    public Guid[] ExcludedLibraryIds { get; set; } = [];
    public Guid[] ExcludedItemIds { get; set; } = [];
    public bool IgnoreItemsWithOtherProviderSegments { get; set; }
    public bool EnableOnDemandFetch { get; set; } = true;
    public int NewItemDelaySeconds { get; set; } = 120;
    public bool InjectChapters { get; set; } = true;
    public int RefreshAfterDays { get; set; } = 30;
    public int NegativeCacheHours { get; set; } = 24;
    public int ConfirmedAbsentCacheDays { get; set; } = 14;
    public int StaleIfErrorDays { get; set; } = 90;
    public int MaxCacheEntries { get; set; } = 50000;
    public int RequestTimeoutSeconds { get; set; } = 10;

    public ProviderSettings Provider(string name) => name switch
    {
        "IntroDB" => IntroDb,
        "TheIntroDB" => TheIntroDb,
        "SkipDB" => SkipDb,
        _ => throw new ArgumentException("Unknown segment provider.", nameof(name))
    };
    public bool Allows(string kind) => kind switch
    {
        "Intro" => EnableIntro,
        "Recap" => EnableRecap,
        "Outro" => EnableOutro,
        "Preview" => EnablePreview,
        _ => false
    };

    public void Validate()
    {
        if (IntroDb is null || TheIntroDb is null || SkipDb is null || ProviderPriority is null
            || SelectedLibraryIds is null || SelectedItemIds is null || ExcludedLibraryIds is null || ExcludedItemIds is null)
            throw new ValidationException("Settings and lists cannot be null.");
        foreach (var name in new[] { "IntroDB", "TheIntroDB", "SkipDB" })
        {
            var p = Provider(name);
            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ValidationException($"{name}: use an HTTP(S) base URL without credentials, query or fragment.");
            if (p.ApiKey is null || p.ApiKey.Contains('\r') || p.ApiKey.Contains('\n'))
                throw new ValidationException($"{name}: invalid API key.");
            Range(p.MinimumIntervalMs, 100, 60000, "Minimum request interval");
        }
        if (ProviderPriority.Length != 3 || ProviderPriority.Distinct().Count() != 3
            || !ProviderPriority.Contains("IntroDB") || !ProviderPriority.Contains("TheIntroDB") || !ProviderPriority.Contains("SkipDB"))
            throw new ValidationException("Priority must contain IntroDB, TheIntroDB and SkipDB exactly once.");
        if (MergeStrategy is not ("FillGaps" or "MissingTypes"))
            throw new ValidationException("Merge strategy must be FillGaps or MissingTypes.");
        Range(DeduplicationToleranceSeconds, 0, 10, "Deduplication tolerance");
        Range(MinimumSegmentSeconds, 0.1, 60, "Minimum segment duration");
        Range(StartOffsetSeconds, -3600, 3600, "Start offset");
        Range(EndOffsetSeconds, -3600, 3600, "End offset");
        foreach (var keywords in new[] { ChapterIntroKeywords, ChapterOutroKeywords, ChapterRecapKeywords, ChapterPreviewKeywords })
            if (keywords is null || keywords.Length > 4096)
                throw new ValidationException("Chapter keyword lists cannot be null or exceed 4096 characters.");
        Range(ChapterMinDurationSeconds, 0.1, 86400, "Minimum chapter duration");
        Range(ChapterMaxDurationSeconds, ChapterMinDurationSeconds, 86400, "Maximum chapter duration");
        Range(ChapterOverlapThreshold, 0, 1, "Chapter overlap threshold");
        Range(NewItemDelaySeconds, 0, 3600, "New item delay");
        Range(RefreshAfterDays, 0, 3650, "Refresh days");
        Range(NegativeCacheHours, 1, 8760, "Negative cache hours");
        Range(ConfirmedAbsentCacheDays, 1, 3650, "Confirmed absent cache days");
        Range(StaleIfErrorDays, 0, 3650, "Stale retention days");
        Range(MaxCacheEntries, 100, 1000000, "Cache capacity");
        Range(RequestTimeoutSeconds, 1, 60, "Request timeout");
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ValidationException($"{name} must be between {min} and {max}.");
    }
}
