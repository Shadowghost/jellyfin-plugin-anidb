using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// The AniBridge mappings, as read from one downloaded copy of them.
/// </summary>
internal sealed class AniBridgeIndex
{
    /// <summary>
    /// The schema this understands. The download URL pins the same major version, so a mapping
    /// set that has moved on is a set this would read wrongly rather than not at all, and is
    /// worth saying so about.
    /// </summary>
    private const string SchemaMajorVersion = "3";

    private readonly IReadOnlyDictionary<string, AniBridgeEntry> _byAnimeId;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AniBridgeEntry>> _bySeries;
    private readonly IReadOnlyDictionary<string, string> _firstSeasonByTmdb;

    private AniBridgeIndex(
        IReadOnlyDictionary<string, AniBridgeEntry> byAnimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AniBridgeEntry>> bySeries,
        IReadOnlyDictionary<string, string> firstSeasonByTmdb)
    {
        _byAnimeId = byAnimeId;
        _bySeries = bySeries;
        _firstSeasonByTmdb = firstSeasonByTmdb;
    }

    /// <summary>
    /// Gets the placement worked out for every season already asked about, keyed by AniDB
    /// series id and season number, holding an empty list for a season the mappings do not
    /// place. Each of a season's episodes asks the same question, and the answer changes only
    /// when the mappings are read again, which replaces this along with them.
    /// </summary>
    public ConcurrentDictionary<string, IReadOnlyList<AniDbSeasonSegment>> Placements { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets how many AniDB entries the mappings place against a TVDB series.
    /// </summary>
    public int EntryCount => _byAnimeId.Count;

    /// <summary>
    /// Reads a downloaded copy of the mappings.
    /// </summary>
    /// <param name="path">Where the copy is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cachedAtUtc">When the copy was written.</param>
    /// <returns>The mappings.</returns>
    public static AniBridgeIndex Parse(string path, ILogger logger, DateTime cachedAtUtc)
    {
        // Read whole rather than streamed. The file is a single object of some seventy thousand
        // keys, all but the AniDB ones skipped, so a reader over the bytes costs one buffer the
        // size of the file and no bookkeeping, where a document object model over it would cost
        // several times the file and hold every key this throws away.
        var bytes = File.ReadAllBytes(path);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        var spans = new Dictionary<string, List<AniBridgeSpan>>(StringComparer.Ordinal);
        var seriesKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var tmdbCandidates = new Dictionary<string, List<SeasonClaim>>(StringComparer.Ordinal);
        string? schemaVersion = null;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The AniBridge mappings do not begin with an object");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueSpan.SequenceEqual("$meta"u8))
            {
                reader.Read();
                schemaVersion = ReadSchemaVersion(ref reader);

                continue;
            }

            // Only the AniDB side of the mappings is read. Every mapping is written in both
            // directions - all 8,633 AniDB-to-TVDB placements appear as TVDB-to-AniDB ones too
            // - so the shows keyed the other way are the same shows, and skipping them here
            // costs nothing but saves reading some sixty thousand keys.
            if (!reader.ValueSpan.StartsWith("anidb:"u8))
            {
                reader.Skip();

                continue;
            }

            var descriptor = reader.GetString()!;

            reader.Read();

            if (TryReadScope(descriptor, out var animeId, out var isSpecialScope))
            {
                ReadTargets(ref reader, animeId, isSpecialScope, spans, seriesKeys, tmdbCandidates);
            }
            else
            {
                reader.Skip();
            }
        }

        if (schemaVersion != null && !schemaVersion.StartsWith(SchemaMajorVersion + ".", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "The AniBridge mappings are written to schema {SchemaVersion}, and this reads schema {SupportedVersion}. Seasons may be placed wrongly until the plugin is updated",
                schemaVersion,
                SchemaMajorVersion);
        }

        return Build(spans, seriesKeys, tmdbCandidates, schemaVersion, cachedAtUtc, logger);
    }

    /// <summary>
    /// Every entry the mappings file under the same show as the given one.
    /// </summary>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The entries, or <c>null</c> where the mappings do not place that one.</returns>
    public IReadOnlyList<AniBridgeEntry>? Siblings(string animeId)
    {
        if (!_byAnimeId.TryGetValue(animeId, out var self))
        {
            return null;
        }

        return _bySeries.TryGetValue(self.SeriesKey, out var siblings) ? siblings : null;
    }

    /// <summary>
    /// Works out which of a show's entries fill the given season, and which of their episodes
    /// each one contributes.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Place(IReadOnlyList<AniBridgeEntry> siblings, int seasonNumber)
    {
        var claims = new List<AniDbSeasonSegment>();

        foreach (var entry in siblings)
        {
            foreach (var span in entry.Spans)
            {
                // A span of an entry's specials cannot be described as part of an ordinary
                // season: what fills it would be read from the entry's specials rather than
                // its episodes, which a segment has no way of saying.
                if (span.Season != seasonNumber || span.IsSpecialInEntry)
                {
                    continue;
                }

                claims.Add(new AniDbSeasonSegment(entry.AnimeId, span.InSeason.Start, CountOf(span), span.InEntry.Start));
            }
        }

        return SeasonSegments.Order(claims);
    }

    /// <summary>
    /// The entry a show begins in, found from the TVDB id another provider has already settled
    /// on.
    /// </summary>
    /// <param name="tvdbId">The TVDB series id.</param>
    /// <returns>The AniDB id, or <c>null</c> where the mappings place nothing against that id.</returns>
    public string? FirstSeasonByTvdb(string tvdbId)
        => _bySeries.TryGetValue(tvdbId, out var siblings) ? PickFirstSeason(siblings) : null;

    /// <summary>
    /// The entry a show begins in, found from the TMDB id another provider has already settled
    /// on. The anime list cannot answer this: it carries TMDB ids for films only.
    /// </summary>
    /// <param name="tmdbId">The TMDB show id.</param>
    /// <returns>The AniDB id, or <c>null</c> where the mappings place nothing against that id.</returns>
    public string? FirstSeasonByTmdb(string tmdbId)
        => _firstSeasonByTmdb.GetValueOrDefault(tmdbId);

    /// <summary>
    /// Whether the mappings file the given entry in an ordinary season of a show, which makes
    /// their silence about walking it back a statement rather than a gap.
    /// </summary>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <returns><c>true</c> where the mappings place the entry in a season of its own.</returns>
    public bool FilesInOrdinarySeason(string animeId)
        => _byAnimeId.TryGetValue(animeId, out var entry) && SeasonOf(entry) >= 1;

    /// <summary>
    /// The entry a show begins in, given an entry of it the mappings file as a later season.
    /// </summary>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the mappings do not place the entry or already place it at the show's first season.</returns>
    public string? WalkBackToFirstSeason(string animeId)
    {
        // Only an entry the mappings file as a second season or later is walked back. An entry
        // already at season 1 is the show's own start, and moving it could only hand the show
        // to whatever else shares its TVDB id.
        if (!_byAnimeId.TryGetValue(animeId, out var self) || SeasonOf(self) <= 1)
        {
            return null;
        }

        var siblings = Siblings(animeId);
        var first = siblings == null ? null : PickFirstSeason(siblings);

        return string.Equals(first, animeId, StringComparison.Ordinal) ? null : first;
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <returns>The episode, or <c>null</c> where the mappings do not place it.</returns>
    public static AniDbAnimeListEpisode? PlaceSpecial(IReadOnlyList<AniBridgeEntry> siblings, int episodeNumber)
    {
        AniBridgeEntry? holder = null;
        AniBridgeSpan? narrowest = null;

        foreach (var entry in siblings)
        {
            foreach (var span in entry.Spans)
            {
                if (span.Season != 0 || episodeNumber < span.InSeason.Start || episodeNumber > (span.InSeason.End ?? int.MaxValue))
                {
                    continue;
                }

                var number = span.InEntry.Start + (episodeNumber - span.InSeason.Start);

                // The two sides of a span disagree in length here and there, the mappings
                // reporting some nine thousand such spans themselves. Where they do, an
                // episode past the end of the entry's own run is not placed by this span.
                if (number > (span.InEntry.End ?? int.MaxValue))
                {
                    continue;
                }

                // A span naming this episode outright beats one that merely covers it, which
                // is what places a film that the season numbering files among the specials.
                if (narrowest == null || (span.InSeason.Length ?? int.MaxValue) < (narrowest.InSeason.Length ?? int.MaxValue))
                {
                    holder = entry;
                    narrowest = span;
                }
            }
        }

        if (holder == null || narrowest == null)
        {
            return null;
        }

        return new AniDbAnimeListEpisode(
            holder.AnimeId,
            narrowest.InEntry.Start + (episodeNumber - narrowest.InSeason.Start),
            narrowest.IsSpecialInEntry);
    }

    /// <summary>
    /// Which of the entries filed under one show the show begins in.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <returns>The AniDB id of the earliest entry, or <c>null</c> where none of them fills a season.</returns>
    public static string? PickFirstSeason(IReadOnlyList<AniBridgeEntry> siblings)
    {
        // The show begins in the entry filling its earliest season, and where that season was
        // released in parts, in the part starting at its first episode. Where a season is
        // filled by several entries starting together - a show and the recap or alternate
        // version filed beside it - the oldest of them is the show itself, AniDB having
        // registered it before whatever was made from it.
        return siblings
            .Select(entry => (Entry: entry, Claim: EarliestClaim(entry)))
            .Where(candidate => candidate.Claim != null)
            .OrderBy(candidate => candidate.Claim!.Season)
            .ThenBy(candidate => candidate.Claim!.Start)
            .ThenBy(candidate => NumericId(candidate.Entry.AnimeId))
            .Select(candidate => candidate.Entry.AnimeId)
            .FirstOrDefault();
    }

    /// <summary>
    /// The season an entry fills, which is the earliest where it fills more than one.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The season number, or -1 where the entry fills no ordinary season.</returns>
    public static int SeasonOf(AniBridgeEntry entry) => EarliestClaim(entry)?.Season ?? -1;

    /// <summary>
    /// How many episodes a span contributes, or zero where it runs to the end of the season.
    /// </summary>
    /// <param name="span">The span.</param>
    /// <returns>The episode count.</returns>
    private static int CountOf(AniBridgeSpan span)
    {
        // Where the two sides of a span disagree in length, the shorter is used: reading past
        // the end of the entry would look for episodes it does not have, and reading past the
        // end of the season would take episodes belonging to whatever comes next.
        return (span.InSeason.Length, span.InEntry.Length) switch
        {
            ({ } inSeason, { } inEntry) => Math.Min(inSeason, inEntry),
            ({ } inSeason, null) => inSeason,
            (null, { } inEntry) => inEntry,
            _ => 0,
        };
    }

    /// <summary>
    /// The earliest ordinary season an entry fills, and where in it the entry starts.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The claim, or <c>null</c> where the entry fills no ordinary season.</returns>
    private static SeasonClaim? EarliestClaim(AniBridgeEntry entry)
    {
        SeasonClaim? earliest = null;

        foreach (var span in entry.Spans)
        {
            if (span.Season < 1 || span.IsSpecialInEntry)
            {
                continue;
            }

            if (earliest == null
                || span.Season < earliest.Season
                || (span.Season == earliest.Season && span.InSeason.Start < earliest.Start))
            {
                earliest = new SeasonClaim(span.Season, span.InSeason.Start, entry.AnimeId);
            }
        }

        return earliest;
    }

    private static int NumericId(string animeId)
        => int.TryParse(animeId, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;

    private static AniBridgeIndex Build(
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates,
        string? schemaVersion,
        DateTime cachedAtUtc,
        ILogger logger)
    {
        var byAnimeId = new Dictionary<string, AniBridgeEntry>(StringComparer.Ordinal);
        var bySeries = new Dictionary<string, List<AniBridgeEntry>>(StringComparer.Ordinal);

        foreach (var (animeId, seriesKey) in seriesKeys)
        {
            var entry = new AniBridgeEntry(animeId, seriesKey, spans.GetValueOrDefault(animeId) ?? []);

            byAnimeId[animeId] = entry;

            if (!bySeries.TryGetValue(seriesKey, out var siblings))
            {
                siblings = [];
                bySeries[seriesKey] = siblings;
            }

            siblings.Add(entry);
        }

        var firstSeasonByTmdb = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (tmdbId, claims) in tmdbCandidates)
        {
            var first = claims
                .OrderBy(claim => claim.Season)
                .ThenBy(claim => claim.Start)
                .ThenBy(claim => NumericId(claim.AnimeId))
                .First();

            firstSeasonByTmdb[tmdbId] = first.AnimeId;
        }

        logger.LogInformation(
            "The AniBridge mappings cached on {CachedAt}, written to schema {SchemaVersion}, place {EntryCount} AniDB entries across {SeriesCount} TVDB shows and identify {TmdbCount} TMDB shows",
            cachedAtUtc,
            schemaVersion ?? "an unstated version",
            byAnimeId.Count,
            bySeries.Count,
            firstSeasonByTmdb.Count);

        return new AniBridgeIndex(
            byAnimeId,
            bySeries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AniBridgeEntry>)pair.Value, StringComparer.Ordinal),
            firstSeasonByTmdb);
    }

    private static string? ReadSchemaVersion(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        string? version = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueSpan.SequenceEqual("schema_version"u8))
            {
                reader.Read();

                version = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Skip();
            }
        }

        return version;
    }

    private static void ReadTargets(
        ref Utf8JsonReader reader,
        string animeId,
        bool isSpecialScope,
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isTvdb = reader.ValueSpan.StartsWith("tvdb_show:"u8);

            // TMDB is read for identification only, not for placing seasons: the numbering a
            // season is placed against has to be the one Jellyfin numbered the season by, and
            // that is what the show's own provider settled on. Placing TVDB runs against TMDB
            // seasons would move every episode of a show the two databases split differently.
            if (!isTvdb && !reader.ValueSpan.StartsWith("tmdb_show:"u8))
            {
                reader.Skip();

                continue;
            }

            var descriptor = reader.GetString()!;

            reader.Read();

            if (!TryReadShow(descriptor, out var showId, out var season))
            {
                reader.Skip();

                continue;
            }

            if (isTvdb)
            {
                ReadRanges(ref reader, animeId, isSpecialScope, showId, season, spans, seriesKeys);
            }
            else
            {
                ReadTmdbClaim(ref reader, animeId, isSpecialScope, showId, season, tmdbCandidates);
            }
        }
    }

    private static void ReadRanges(
        ref Utf8JsonReader reader,
        string animeId,
        bool isSpecialScope,
        string showId,
        int season,
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var inEntry = AniBridgeRange.Read(reader.GetString());

            reader.Read();

            // A range the mappings unmap outright is written as null, and one they write in a
            // form this does not read comes back null too.
            var inSeason = reader.TokenType == JsonTokenType.String ? AniBridgeRange.Read(reader.GetString()) : null;

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            if (inEntry == null || inSeason == null)
            {
                continue;
            }

            // The show is keyed by the id its episodes are placed against, so an entry only
            // counts as placed once a span of it has been read.
            seriesKeys.TryAdd(animeId, showId);

            if (!spans.TryGetValue(animeId, out var list))
            {
                list = [];
                spans[animeId] = list;
            }

            list.Add(new AniBridgeSpan(season, inEntry, inSeason, isSpecialScope));
        }
    }

    private static void ReadTmdbClaim(
        ref Utf8JsonReader reader,
        string animeId,
        bool isSpecialScope,
        string showId,
        int season,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        var earliest = int.MaxValue;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            reader.Read();

            var inSeason = reader.TokenType == JsonTokenType.String ? AniBridgeRange.Read(reader.GetString()) : null;

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            if (inSeason != null && inSeason.Start < earliest)
            {
                earliest = inSeason.Start;
            }
        }

        if (isSpecialScope || season < 1 || earliest == int.MaxValue)
        {
            return;
        }

        if (!tmdbCandidates.TryGetValue(showId, out var claims))
        {
            claims = [];
            tmdbCandidates[showId] = claims;
        }

        claims.Add(new SeasonClaim(season, earliest, animeId));
    }

    /// <summary>
    /// Reads a source descriptor, which is "anidb:&lt;id&gt;:&lt;scope&gt;".
    /// </summary>
    /// <param name="descriptor">The descriptor as written.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="isSpecialScope">Whether the scope numbers the entry's specials.</param>
    /// <returns><c>true</c> where the descriptor names a scope this reads.</returns>
    private static bool TryReadScope(string descriptor, out string animeId, out bool isSpecialScope)
    {
        animeId = string.Empty;
        isSpecialScope = false;

        var parts = descriptor.Split(':');

        if (parts.Length != 3 || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))
        {
            return false;
        }

        // "R" numbers the entry's ordinary episodes and "S" its specials. The rest are AniDB's
        // other episode types - credits, trailers, parodies and the like - which a handful of
        // entries are mapped by and which name no file the episode provider reads.
        switch (parts[2])
        {
            case "R":
                break;
            case "S":
                isSpecialScope = true;
                break;
            default:
                return false;
        }

        animeId = parts[1];

        return true;
    }

    /// <summary>
    /// Reads a show descriptor, which is "&lt;provider&gt;:&lt;id&gt;:s&lt;season&gt;".
    /// </summary>
    /// <param name="descriptor">The descriptor as written.</param>
    /// <param name="showId">The show's id with the provider.</param>
    /// <param name="season">The season number, 0 being the specials.</param>
    /// <returns><c>true</c> where the descriptor names a numbered season.</returns>
    private static bool TryReadShow(string descriptor, out string showId, out int season)
    {
        showId = string.Empty;
        season = -1;

        var parts = descriptor.Split(':');

        if (parts.Length != 3
            || parts[1].Length == 0
            || !parts[1].All(char.IsAsciiDigit)
            || parts[2].Length < 2
            || parts[2][0] != 's'
            || !int.TryParse(parts[2].AsSpan(1), CultureInfo.InvariantCulture, out season))
        {
            return false;
        }

        showId = parts[1];

        return true;
    }

    /// <summary>
    /// One entry's claim on a season, used to work out which entry a show begins in.
    /// </summary>
    /// <param name="Season">The season claimed.</param>
    /// <param name="Start">The episode of that season the entry starts at.</param>
    /// <param name="AnimeId">The AniDB id of the entry.</param>
    private sealed record SeasonClaim(int Season, int Start, string AnimeId);
}
