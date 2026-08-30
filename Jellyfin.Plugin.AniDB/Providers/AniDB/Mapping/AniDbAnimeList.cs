using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The community anime list, which records which AniDB entry fills which season of a show.
/// </summary>
internal static class AniDbAnimeList
{
    private const string ListUrl = "https://raw.githubusercontent.com/Anime-Lists/anime-lists/master/anime-list-full.xml";

    /// <summary>
    /// How long a downloaded list is used before it is fetched again. The list gains entries as
    /// shows are announced, and an entry for a show already in a library rarely changes.
    /// </summary>
    private const int MaxAgeDays = 7;

    /// <summary>
    /// How long to wait before trying again once the list could not be read at all. Without a
    /// pause a scan would ask for it once per series and fail every time; without a retry a
    /// server that started while its network was down would never get the list.
    /// </summary>
    private static readonly TimeSpan _retryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the copy in memory is used before the cached file is looked at again. Reading
    /// the file's timestamp is cheap, but a scan asks once per episode, and the file only
    /// changes when this class downloads it or someone replaces it by hand.
    /// </summary>
    private static readonly TimeSpan _recheckInterval = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>
    /// The parsed list. Every lookup is answered from here: the file is read and parsed once,
    /// downloaded only when the copy on disk is missing or has gone stale, and read again once
    /// the copy in memory has.
    /// </summary>
    private static IReadOnlyDictionary<string, AniDbAnimeListEntry>? _byAnimeId;
    private static IReadOnlyDictionary<string, IReadOnlyList<AniDbAnimeListEntry>>? _bySeries;
    private static DateTime _failedAtUtc = DateTime.MinValue;

    /// <summary>
    /// When the cached file was last compared against the copy in memory, and the timestamp
    /// that copy was read from. A server left running for weeks would otherwise keep what it
    /// read at startup for as long as it ran, never learning where the season that started
    /// since belongs, nor noticing a file replaced underneath it.
    /// </summary>
    private static DateTime _checkedAtUtc = DateTime.MinValue;
    private static DateTime _sourceWrittenAtUtc = DateTime.MinValue;

    /// <summary>
    /// The placement worked out for every season already asked about, keyed by series id and
    /// season number, holding an empty list for a season the list does not place. Each of a
    /// season's episodes asks the same question, and the answer changes only when the list is
    /// read again, which replaces this along with it.
    /// </summary>
    private static ConcurrentDictionary<string, IReadOnlyList<AniDbSeasonSegment>> _placements = new(StringComparer.Ordinal);

    /// <summary>
    /// The AniDB entries the given season of the given series is filled from, in the order the
    /// season's episodes run through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when the list does not place that season.</returns>
    public static async Task<IReadOnlyList<AniDbSeasonSegment>?> ResolveSeason(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 1)
        {
            return null;
        }

        var siblings = await GetSiblings(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false);

        // Read after the load above, so that a list read afresh hands out the placements worked
        // out from it rather than the ones its predecessor gave.
        var placements = _placements;
        var key = FormattableString.Invariant($"{seriesId}/{seasonNumber}");

        if (placements.TryGetValue(key, out var known))
        {
            return known.Count == 0 ? null : known;
        }

        IReadOnlyList<AniDbSeasonSegment> segments = siblings == null ? [] : Place(siblings, seasonNumber);

        // One line per season. Every episode of that season asks the same question, and the
        // answer is the same every time, so logging it per episode only buries the rest.
        if (placements.TryAdd(key, segments) && segments.Count > 0)
        {
            logger.LogInformation(
                "The anime list fills season {SeasonNumber} of AniDB series {SeriesId} with {Placement}",
                seasonNumber,
                seriesId,
                string.Join(", ", segments.Select(Describe)));
        }

        return segments.Count == 0 ? null : segments;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from the TVDB id another provider has already
    /// settled on. The list keys its entries by TVDB id, so this identifies a show outright
    /// where matching on the name cannot: where AniDB spells the name differently, and where
    /// two shows share one name and only the id tells them apart.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the list files nothing under that TVDB id.</returns>
    public static async Task<string?> ResolveSeriesId(
        IApplicationPaths appPaths,
        string? tvdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Anything else is a placeholder the list uses for a show TVDB does not carry, and
        // those are not keys of the index below.
        if (string.IsNullOrEmpty(tvdbId) || !tvdbId.All(char.IsAsciiDigit))
        {
            return null;
        }

        await Load(appPaths, logger, cancellationToken).ConfigureAwait(false);

        if (_bySeries == null || !_bySeries.TryGetValue(tvdbId, out var siblings))
        {
            return null;
        }

        return PickFirstSeason(siblings);
    }

    /// <summary>
    /// The entry a show begins in, given an entry of it that the list files as a later season.
    /// AniDB titles a second season "&lt;name&gt; (&lt;year&gt;)" as readily as it titles a
    /// remake that way, so a name match on a show whose seasons all aired in one year lands on
    /// the sequel rather than on the show. The list records which season each entry fills, so
    /// it can walk that back without asking AniDB anything.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the list does not place the entry or already places it at the show's first season.</returns>
    public static async Task<string?> ResolveFirstSeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(animeId))
        {
            return null;
        }

        await Load(appPaths, logger, cancellationToken).ConfigureAwait(false);

        // Only an entry the list files as a second season or later is walked back. An entry it
        // already files at season 1 is the show's own start, and moving it could only hand the
        // show to whatever else shares its TVDB id: the list groups a handful of unrelated
        // shows under one id, and two adaptations of one book sit that way under season 1.
        if (_byAnimeId == null || _bySeries == null
            || !_byAnimeId.TryGetValue(animeId, out var self)
            || SeasonOf(self) <= 1)
        {
            return null;
        }

        if (!_bySeries.TryGetValue(self.SeriesKey, out var siblings))
        {
            return null;
        }

        var first = PickFirstSeason(siblings);

        return string.Equals(first, animeId, StringComparison.Ordinal) ? null : first;
    }

    /// <summary>
    /// Which of the entries filed under one show the show begins in.
    /// </summary>
    /// <param name="siblings">Every entry the list files under the same show.</param>
    /// <returns>The AniDB id of the earliest entry, or <c>null</c> when none of them fills a season.</returns>
    private static string? PickFirstSeason(IReadOnlyList<AniDbAnimeListEntry> siblings)
    {
        // The show begins in the entry filling its earliest season, and where that season was
        // released in parts, in the part starting at its first episode. Where a season is
        // filled by several entries starting together - a show and the recap or alternate
        // version filed beside it - the oldest of them is the show itself, AniDB having
        // registered it before whatever was made from it.
        //
        // Season 0 is an entry holding nothing but specials, which is never where a show
        // begins, and a season that will not parse is one the list cannot place at all.
        return siblings
            .Select(entry => (Entry: entry, Season: SeasonOf(entry)))
            .Where(candidate => candidate.Season >= 1)
            .OrderBy(candidate => candidate.Season)
            .ThenBy(candidate => candidate.Entry.EpisodeOffset)
            .ThenBy(candidate => int.TryParse(candidate.Entry.AnimeId, CultureInfo.InvariantCulture, out var id) ? id : int.MaxValue)
            .Select(candidate => candidate.Entry.AnimeId)
            .FirstOrDefault();
    }

    /// <summary>
    /// The season an entry fills, as a number. An entry numbered straight through the whole
    /// show carries "a" rather than a season, and covers that show from its first episode.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The season number, or -1 where the entry names no season this can read.</returns>
    private static int SeasonOf(AniDbAnimeListEntry entry)
    {
        if (string.Equals(entry.DefaultSeason, "a", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return int.TryParse(entry.DefaultSeason, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }

    /// <summary>
    /// Works out which of a series' AniDB entries fill the given season, and which of their
    /// episodes each one contributes.
    /// </summary>
    /// <param name="siblings">Every entry the list files under the same series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    private static IReadOnlyList<AniDbSeasonSegment> Place(IReadOnlyList<AniDbAnimeListEntry> siblings, int seasonNumber)
    {
        var claims = new List<AniDbSeasonSegment>();

        foreach (var entry in siblings)
        {
            var placed = false;

            foreach (var mapping in entry.Mappings)
            {
                // A rule naming episodes one by one places specials, not a run of a season.
                if (mapping.TvdbSeason != seasonNumber
                    || mapping.AnidbSeason == 0
                    || mapping.Start is not { } start
                    || mapping.End < start)
                {
                    continue;
                }

                // A rule with no end runs to the end of the entry. That is how the list places
                // the season now airing, whose last episode nobody knows yet, and dropping such
                // a rule left that season with no placement at all.
                var count = mapping.End is { } end ? end - start + 1 : 0;

                claims.Add(new AniDbSeasonSegment(entry.AnimeId, start + mapping.Offset, count, start));
                placed = true;
            }

            // The season an entry names is where the rest of it goes, unless a rule above has
            // already placed part of it in this same season.
            if (!placed
                && int.TryParse(entry.DefaultSeason, CultureInfo.InvariantCulture, out var defaultSeason)
                && defaultSeason == seasonNumber)
            {
                // The list does not say how long an entry is, so the claim runs to the end of
                // the season. It stops early only where a rule hands the rest of the entry to
                // another season, as a series split across two of them does.
                var handedOver = entry.Mappings
                    .Where(mapping => mapping.TvdbSeason != seasonNumber && mapping.AnidbSeason != 0 && mapping.Start.HasValue)
                    .Select(mapping => mapping.Start!.Value)
                    .DefaultIfEmpty(0)
                    .Min();

                claims.Add(new AniDbSeasonSegment(entry.AnimeId, entry.EpisodeOffset + 1, Math.Max(handedOver - 1, 0), 1));
            }
        }

        if (claims.Count == 0)
        {
            return [];
        }

        // A segment with no count has to come last among those starting together, or it would
        // swallow the one after it.
        var segments = claims
            .OrderBy(segment => segment.FirstEpisodeNumber)
            .ThenBy(segment => segment.EpisodeCount == 0 ? 1 : 0)
            .ToList();

        // An entry only runs to the end of the season if no other entry starts later in it. A
        // season released in parts is one entry per part, each saying where it starts and none
        // of them how long it is, so without this the first part would answer for the whole
        // season and every episode past it would be looked up in the entry before its own.
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var room = segments[index + 1].FirstEpisodeNumber - segments[index].FirstEpisodeNumber;

            if (segments[index].EpisodeCount == 0 && room > 0)
            {
                segments[index] = segments[index] with { EpisodeCount = room };
            }
        }

        return segments;
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> when the list does not place it.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveSpecial(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var siblings = await GetSiblings(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false);

        if (siblings == null)
        {
            return null;
        }

        // A rule naming this episode outright beats anything worked out from an offset.
        foreach (var entry in siblings)
        {
            foreach (var mapping in entry.Mappings)
            {
                if (mapping.TvdbSeason != 0)
                {
                    continue;
                }

                foreach (var pair in mapping.Pairs)
                {
                    // A season number of 0 says the episode has no counterpart to name.
                    if (pair.Value == episodeNumber && pair.Value != 0)
                    {
                        return new AniDbAnimeListEpisode(entry.AnimeId, pair.Key, mapping.AnidbSeason == 0);
                    }
                }

                if (mapping.Start is { } start)
                {
                    var number = episodeNumber - mapping.Offset;

                    if (number >= start && number <= (mapping.End ?? int.MaxValue))
                    {
                        return new AniDbAnimeListEpisode(entry.AnimeId, number, mapping.AnidbSeason == 0);
                    }
                }
            }
        }

        // Otherwise the entry that starts closest below this episode is the one holding it. An
        // entry that has a rule for the specials has already had its say above.
        AniDbAnimeListEntry? holder = null;

        foreach (var entry in siblings)
        {
            if (!string.Equals(entry.DefaultSeason, "0", StringComparison.Ordinal)
                || entry.EpisodeOffset >= episodeNumber
                || entry.Mappings.Any(mapping => mapping.TvdbSeason == 0))
            {
                continue;
            }

            if (holder == null || entry.EpisodeOffset > holder.EpisodeOffset)
            {
                holder = entry;
            }
        }

        if (holder == null)
        {
            return null;
        }

        logger.LogDebug(
            "The anime list places special {EpisodeNumber} of AniDB series {SeriesId} in anime {AnimeId}",
            episodeNumber,
            seriesId,
            holder.AnimeId);

        return new AniDbAnimeListEpisode(holder.AnimeId, episodeNumber - holder.EpisodeOffset, false);
    }

    private static string Describe(AniDbSeasonSegment segment)
        => segment.EpisodeCount > 0
            ? FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber}-{segment.FirstEpisodeNumber + segment.EpisodeCount - 1} from anime {segment.AnimeId} episodes {segment.FirstEpisodeInEntry}-{segment.FirstEpisodeInEntry + segment.EpisodeCount - 1}")
            : FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber} onwards from anime {segment.AnimeId} episode {segment.FirstEpisodeInEntry} onwards");

    private static async Task<IReadOnlyList<AniDbAnimeListEntry>?> GetSiblings(
        IApplicationPaths appPaths,
        string seriesId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await Load(appPaths, logger, cancellationToken).ConfigureAwait(false);

        if (_byAnimeId == null || _bySeries == null || !_byAnimeId.TryGetValue(seriesId, out var self))
        {
            return null;
        }

        return _bySeries.TryGetValue(self.SeriesKey, out var siblings) ? siblings : null;
    }

    private static async Task Load(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
    {
        // The common path, taken once per lookup: the list is already in memory and current.
        if (IsCurrent())
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsCurrent())
            {
                return;
            }

            var path = Path.Combine(AniDbTitleDownloader.GetDataPath(appPaths), "anime-list.xml");

            await Refresh(path, logger, cancellationToken).ConfigureAwait(false);

            var file = new FileInfo(path);

            if (!file.Exists || file.Length == 0)
            {
                _failedAtUtc = DateTime.UtcNow;

                return;
            }

            // The file is the copy of record. Where it is the one already parsed there is
            // nothing to do; where it is not - downloaded just now, or replaced by hand - what
            // is in memory is out of date whatever its own age.
            if (_byAnimeId != null && file.LastWriteTimeUtc == _sourceWrittenAtUtc)
            {
                _checkedAtUtc = DateTime.UtcNow;

                return;
            }

            try
            {
                Parse(path, logger, file.LastWriteTimeUtc);
            }
            catch (System.Xml.XmlException ex)
            {
                // A truncated or half-written file would otherwise be read again on every
                // start until it went stale. Dropping it means the next start downloads afresh.
                logger.LogWarning(ex, "The cached anime list at {Path} could not be read and has been discarded", path);

                TryDelete(path);
                _failedAtUtc = DateTime.UtcNow;
            }
        }
        catch (IOException ex)
        {
            _failedAtUtc = DateTime.UtcNow;

            logger.LogWarning(ex, "The anime list could not be read, so seasons are mapped from AniDB's own relations instead");
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Whether a lookup may be answered from what is in memory, either because the cached file
    /// was checked against it recently enough or because reading it failed recently enough to
    /// be worth a pause.
    /// </summary>
    /// <returns><c>true</c> when the cached file need not be looked at.</returns>
    private static bool IsCurrent()
    {
        var now = DateTime.UtcNow;

        return now - _failedAtUtc < _retryAfterFailure
            || (_byAnimeId != null && now - _checkedAtUtc < _recheckInterval);
    }

    /// <summary>
    /// What is known of the list, for the status the configuration page shows. Reads nothing
    /// but the timestamp of the cached file, so it costs little to ask often.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>When the cached copy was downloaded, how many entries have been read from it, and how many days a copy is used for.</returns>
    internal static (DateTime? CachedAtUtc, int EntryCount, int MaxAgeInDays) GetStatus(IApplicationPaths appPaths)
    {
        DateTime? cachedAtUtc = null;

        try
        {
            var file = new FileInfo(Path.Combine(AniDbTitleDownloader.GetDataPath(appPaths), "anime-list.xml"));

            if (file.Exists && file.Length > 0)
            {
                cachedAtUtc = file.LastWriteTimeUtc;
            }
        }
        catch (IOException)
        {
            // The status is worth less than the page it is shown on.
        }

        return (cachedAtUtc, _byAnimeId?.Count ?? 0, MaxAgeDays);
    }

    /// <summary>
    /// Downloads the list if the copy on disk is missing or has gone stale. A copy that is
    /// merely stale is kept when the download fails: an old mapping for a show already in the
    /// library is almost always still the right one, and is certainly better than none.
    /// </summary>
    /// <param name="path">Where the list is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task Refresh(string path, ILogger logger, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var cached = file.Exists && file.Length > 0;

        if (cached && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalDays <= MaxAgeDays)
        {
            return;
        }

        try
        {
            await Download(path, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (!cached)
            {
                throw new IOException("The anime list could not be downloaded and none is cached", ex);
            }

            logger.LogWarning(
                ex,
                "The anime list could not be downloaded, so the copy cached on {CachedAt} is used instead",
                file.LastWriteTimeUtc);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start tries again.
        }
    }

    private static async Task Download(string path, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading the anime list from {Url}", ListUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not paced by the AniDB request gate: this comes from the list's own host, and holding
        // up a scan behind AniDB's rate limit for it would be for nothing.
        var httpClient = Plugin.Instance.GetHttpClient();
        var temporaryFile = path + ".tmp";

        try
        {
            using (var stream = await httpClient.GetStreamAsync(new Uri(ListUrl), cancellationToken).ConfigureAwait(false))
            using (var writer = File.Open(temporaryFile, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFile, path, true);
        }
        catch
        {
            TryDelete(temporaryFile);

            throw;
        }
    }

    private static void Parse(string path, ILogger logger, DateTime cachedAtUtc)
    {
        var byAnimeId = new Dictionary<string, AniDbAnimeListEntry>(StringComparer.Ordinal);
        var bySeries = new Dictionary<string, List<AniDbAnimeListEntry>>(StringComparer.Ordinal);

        foreach (var element in XDocument.Load(path).Root?.Elements("anime") ?? [])
        {
            var animeId = element.Attribute("anidbid")?.Value;
            var seriesKey = element.Attribute("tvdbid")?.Value;

            // A film or an OVA the list files under no series has nothing to place it against.
            if (string.IsNullOrEmpty(animeId) || string.IsNullOrEmpty(seriesKey) || !seriesKey.All(char.IsAsciiDigit))
            {
                continue;
            }

            var entry = new AniDbAnimeListEntry(
                animeId,
                seriesKey,
                element.Attribute("defaulttvdbseason")?.Value,
                ReadInt(element.Attribute("episodeoffset")?.Value),
                [.. element.Descendants("mapping").Select(ReadMapping).OfType<AniDbAnimeListMapping>()]);

            byAnimeId[animeId] = entry;

            if (!bySeries.TryGetValue(seriesKey, out var siblings))
            {
                siblings = [];
                bySeries[seriesKey] = siblings;
            }

            siblings.Add(entry);
        }

        _byAnimeId = byAnimeId;
        _bySeries = bySeries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AniDbAnimeListEntry>)pair.Value, StringComparer.Ordinal);

        // Placements worked out from the previous copy belong to it, not to this one.
        _placements = new ConcurrentDictionary<string, IReadOnlyList<AniDbSeasonSegment>>(StringComparer.Ordinal);
        _checkedAtUtc = DateTime.UtcNow;
        _sourceWrittenAtUtc = cachedAtUtc;

        logger.LogInformation(
            "The anime list cached on {CachedAt} places {EntryCount} AniDB entries across {SeriesCount} shows. It is kept in memory, compared against the cached file every {RecheckMinutes} minutes and downloaded again once that file is {MaxAgeDays} days old",
            cachedAtUtc,
            byAnimeId.Count,
            bySeries.Count,
            _recheckInterval.TotalMinutes,
            MaxAgeDays);
    }

    private static AniDbAnimeListMapping? ReadMapping(XElement element)
    {
        if (element.Attribute("tvdbseason") == null)
        {
            return null;
        }

        var pairs = new List<KeyValuePair<int, int>>();

        foreach (var pair in (element.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('-', StringComparison.Ordinal);

            if (separator > 0
                && int.TryParse(pair[..separator], CultureInfo.InvariantCulture, out var inEntry)
                && int.TryParse(pair[(separator + 1)..], CultureInfo.InvariantCulture, out var inSeason))
            {
                pairs.Add(new KeyValuePair<int, int>(inEntry, inSeason));
            }
        }

        return new AniDbAnimeListMapping(
            ReadInt(element.Attribute("anidbseason")?.Value),
            ReadInt(element.Attribute("tvdbseason")?.Value),
            ReadNullableInt(element.Attribute("start")?.Value),
            ReadNullableInt(element.Attribute("end")?.Value),
            ReadInt(element.Attribute("offset")?.Value),
            pairs);
    }

    private static int ReadInt(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int? ReadNullableInt(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
