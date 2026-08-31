using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The mapping sources, asked in order.
/// </summary>
/// <remarks>
/// The AniBridge mappings are asked first. They place half again as many AniDB entries as the
/// anime list, state each placement's episode ranges outright rather than leaving them to be
/// worked out from an offset, and are built partly from the anime list itself, so where both
/// place a show they are the later word on it. They also carry TMDB show ids, which the anime
/// list has for films only.
/// <para>
/// The anime list answers what AniBridge does not, which at the time of writing is 152 shows
/// AniBridge maps to no TVDB id and a couple of hundred entries' specials. Each source's answer
/// is self-consistent, and the primary answers whenever it can, so the two are only ever mixed
/// for a show AniBridge places in part. That case is logged, because one of the two is wrong
/// about it.
/// </para>
/// </remarks>
internal static class AniDbMappings
{
    private const string AniBridge = "the AniBridge mappings";
    private const string AnimeList = "the anime list";

    /// <summary>
    /// The AniDB entries the given season of the given series is filled from, in the order the
    /// season's episodes run through them.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when neither source places that season.</returns>
    public static async Task<IReadOnlyList<AniDbSeasonSegment>?> ResolveSeason(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bridged = await AniBridgeMappings.ResolveSeason(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);

        if (bridged != null)
        {
            return bridged;
        }

        var listed = await AniDbAnimeList.ResolveSeason(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);

        if (listed != null)
        {
            await ReportGap(appPaths, seriesId, seasonNumber, logger, cancellationToken).ConfigureAwait(false);
        }

        return listed;
    }

    /// <summary>
    /// The AniDB entry a show begins in, found from whichever ids another provider has already
    /// settled on. The TVDB id is tried first because both sources carry it, and a show placed
    /// by both is placed against TVDB's numbering by both.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="tvdbId">The TVDB id of the series, where it has one.</param>
    /// <param name="tmdbId">The TMDB id of the series, where it has one.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The show, or <c>null</c> when no source files anything under those ids.</returns>
    public static async Task<MappedSeries?> ResolveSeriesId(
        IApplicationPaths appPaths,
        string? tvdbId,
        string? tmdbId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bridged = await AniBridgeMappings.ResolveSeriesId(appPaths, tvdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(bridged))
        {
            return new MappedSeries(bridged, AniBridge, "TVDB", tvdbId!);
        }

        var listed = await AniDbAnimeList.ResolveSeriesId(appPaths, tvdbId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(listed))
        {
            return new MappedSeries(listed, AnimeList, "TVDB", tvdbId!);
        }

        // Only AniBridge answers for TMDB, and it is asked last: a show carrying both ids is
        // better placed by the id its season numbering will be read against.
        var byTmdb = await AniBridgeMappings.ResolveSeriesIdByTmdb(appPaths, tmdbId, logger, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(byTmdb) ? null : new MappedSeries(byTmdb, AniBridge, "TMDB", tmdbId!);
    }

    /// <summary>
    /// The entry a show begins in, given an entry of it that a source files as a later season.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where no source walks it back.</returns>
    public static async Task<string?> ResolveFirstSeason(
        IApplicationPaths appPaths,
        string animeId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bridged = await AniBridgeMappings.ResolveFirstSeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(bridged))
        {
            return bridged;
        }

        // Not walked back by the anime list where AniBridge files the entry in a season of its
        // own and left it there: AniBridge has already said the entry is a show's first season,
        // and the two disagree about which show an entry belongs to often enough that overruling
        // it here would hand the show to the wrong one. An entry AniBridge knows only as another
        // show's specials is not such a statement, and is left to the anime list.
        if (await AniBridgeMappings.FilesInOrdinarySeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await AniDbAnimeList.ResolveFirstSeason(appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episode, or <c>null</c> when no source places it.</returns>
    public static async Task<AniDbAnimeListEpisode?> ResolveSpecial(
        IApplicationPaths appPaths,
        string seriesId,
        int episodeNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bridged = await AniBridgeMappings.ResolveSpecial(appPaths, seriesId, episodeNumber, logger, cancellationToken).ConfigureAwait(false);

        return bridged ?? await AniDbAnimeList.ResolveSpecial(appPaths, seriesId, episodeNumber, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notes a season the anime list places and AniBridge does not, for a show AniBridge places
    /// otherwise. One of the two is wrong about that show, and this is the only point at which
    /// a season is filled from a source other than the one that identified the show.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB id of the series.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task ReportGap(
        IApplicationPaths appPaths,
        string seriesId,
        int seasonNumber,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await AniBridgeMappings.Places(appPaths, seriesId, logger, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug(
                "The AniBridge mappings place AniDB series {SeriesId} but not its season {SeasonNumber}, which {Source} filled instead",
                seriesId,
                seasonNumber,
                AnimeList);
        }
    }
}
