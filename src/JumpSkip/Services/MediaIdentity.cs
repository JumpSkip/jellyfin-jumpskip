using System.Text.RegularExpressions;
using JumpSkip.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace JumpSkip.Services;

public sealed class MediaIdentity(ILibraryManager library)
{
    private static readonly Regex Imdb = new(@"\btt\d{7,10}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Numbers = new(@"\bS(?<s>\d{1,3})E(?<e>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public MediaLookup? Resolve(BaseItem item)
    {
        if (item is not Video) return null;
        var movie = item is Movie;
        var episode = item as Episode;
        if (episode is not null && episode.IndexNumberEnd > episode.IndexNumber) return null;
        var series = episode?.SeriesId is { } seriesId && seriesId != Guid.Empty ? library.GetItemById(seriesId) as Series : null;
        var identity = (BaseItem?)series ?? item;
        var imdb = Id(identity, "Imdb");
        if (string.IsNullOrWhiteSpace(imdb) && series is not null) imdb = Id(item, "Imdb");
        if (imdb is null || !Imdb.IsMatch(imdb) || Imdb.Match(imdb).Value.Length != imdb.Length)
        {
            var match = Imdb.Match(item.Path ?? string.Empty);
            imdb = match.Success ? match.Value : null;
        }
        var (mapping, unsupported) = DisplayOrder(series?.DisplayOrder);
        int? season = movie ? null : mapping is null && !unsupported
            ? episode?.AiredSeasonNumber ?? item.ParentIndexNumber : item.ParentIndexNumber;
        int? number = movie ? null : item.IndexNumber;
        if (!movie && (season is null || number is null))
        {
            var match = Numbers.Match(item.Path ?? string.Empty);
            if (match.Success)
            {
                season ??= int.Parse(match.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture);
                number ??= int.Parse(match.Groups["e"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return new(movie, imdb?.ToLowerInvariant(), Numeric(identity, "Tvdb"), Numeric(identity, "Tmdb"),
            season, number, mapping, unsupported, episode is null ? null : Numeric(item, "Tvdb"), item.RunTimeTicks);
    }

    internal static (string? Mapping, bool Unsupported) DisplayOrder(string? value)
    {
        var key = (value ?? "").Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
        return key switch
        {
            "" or "default" or "defaultorder" or "aired" or "airedorder" or "airdate" or "airdateorder" or "official" or "officialorder" => (null, false),
            "dvd" or "dvdorder" or "altdvd" => ("dvd", false),
            "absolute" or "absoluteorder" or "abso" => ("absolute", false),
            "alternate" or "alternateorder" or "alttwo" => ("alternate", false),
            "regional" or "regionalorder" => ("regional", false),
            _ => (null, true)
        };
    }
    private static string? Id(BaseItem item, string provider) => item.ProviderIds.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
    private static long? Numeric(BaseItem item, string provider) => long.TryParse(Id(item, provider), out var id) && id > 0 ? id : null;
}
