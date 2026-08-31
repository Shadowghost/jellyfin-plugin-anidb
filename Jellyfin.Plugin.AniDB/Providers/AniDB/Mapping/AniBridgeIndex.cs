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

    /// <summary>
    /// Where AniDB's other episodes begin in the single numbering the specials scope uses.
    /// </summary>
    private const int OtherEpisodeBand = 400;

    /// <summary>
    /// The longest season this will lay out episode by episode. One Piece's longest is under
    /// two hundred, so anything past this is a mapping to be taken whole rather than walked.
    /// </summary>
    private const int MaxEpisodesPerSeason = 2000;

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

            if (TryReadScope(descriptor, out var animeId, out var kind))
            {
                ReadTargets(ref reader, animeId, kind, spans, seriesKeys, tmdbCandidates);
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
        // Kept apart because one entry can describe the same season under two of its own
        // numberings: anime 13473 maps a single ordinary episode onto season 2 and all twelve of
        // its other episodes onto the same twelve, the season being three films that the season
        // numbering breaks into television episodes. Mixing the two would leave the season
        // claimed twice over, so the fuller numbering is taken whole and the other dropped.
        var claims = new Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>>();

        foreach (var entry in siblings)
        {
            foreach (var span in entry.Spans)
            {
                // A span of an entry's specials cannot be described as part of an ordinary
                // season: a season filled from them would have to be read from the entry's
                // specials, and that is what the specials season is for.
                if (span.Season != seasonNumber || span.Kind == AniDbEpisodeKind.Special)
                {
                    continue;
                }

                if (!claims.TryGetValue(span.Kind, out var byKind))
                {
                    byKind = [];
                    claims[span.Kind] = byKind;
                }

                byKind.Add(new AniDbSeasonSegment(entry.AnimeId, span.InSeason.Start, CountOf(span), span.InEntry.Start, span.Kind));
            }
        }

        if (claims.Count == 0)
        {
            return [];
        }

        return SeasonSegments.Order(claims.Count == 1 ? claims.First().Value : Merge(claims));
    }

    /// <summary>
    /// Lays the claims of several numberings over one season, episode by episode.
    /// </summary>
    /// <remarks>
    /// Two numberings of one entry usually describe different parts of the season rather than
    /// the same part: anime 162's other episodes fill season 1's episodes 2, 3, 13, 17, 18 and
    /// 34, and its ordinary episodes fill the rest, the two not meeting anywhere. Taking one
    /// numbering and dropping the other left those six episodes to be read from ordinary
    /// episodes that are not what they hold.
    /// <para>
    /// Where two numberings do claim the same episode, one of them is a stub: anime 13473 maps a
    /// single ordinary episode onto a season its other episodes describe in full, and anime
    /// 11350 maps a single other episode onto a season its ordinary episodes describe in full.
    /// The numbering that covers more of the season is the one describing it, so it wins the
    /// episodes they disagree about, and ordinary episodes win an even tie.
    /// </para>
    /// </remarks>
    /// <param name="claims">The claims on the season, by the numbering that made them.</param>
    /// <returns>The segments, one per run of consecutive episodes read the same way.</returns>
    private static List<AniDbSeasonSegment> Merge(Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>> claims)
    {
        var ordered = claims
            .OrderByDescending(pair => pair.Value.Sum(segment => segment.EpisodeCount == 0 ? int.MaxValue : segment.EpisodeCount))
            .ThenBy(pair => pair.Key == AniDbEpisodeKind.Regular ? 0 : 1)
            .ToList();

        // A run with no end cannot be laid out episode by episode, and neither can a season of
        // implausible length. Neither occurs in the set; where one did, the numbering covering
        // most of the season answers for the whole of it, as it did before this.
        if (ordered.Exists(pair => pair.Value.Exists(segment => segment.EpisodeCount <= 0 || segment.EpisodeCount > MaxEpisodesPerSeason)))
        {
            return ordered[0].Value;
        }

        var placed = new Dictionary<int, AniDbSeasonSegment>();

        foreach (var (_, segments) in ordered)
        {
            foreach (var segment in segments)
            {
                for (var offset = 0; offset < segment.EpisodeCount; offset++)
                {
                    var episode = segment.FirstEpisodeNumber + offset;

                    // The first numbering to claim an episode keeps it.
                    if (!placed.ContainsKey(episode))
                    {
                        placed[episode] = new AniDbSeasonSegment(
                            segment.AnimeId,
                            episode,
                            1,
                            segment.FirstEpisodeInEntry + offset,
                            segment.Kind);
                    }
                }
            }
        }

        var merged = new List<AniDbSeasonSegment>();

        foreach (var episode in placed.Keys.Order())
        {
            var one = placed[episode];

            // Episodes read consecutively from the same run of the same entry are one segment
            // again, so that a season no numbering interrupts is described as plainly as before.
            if (merged.Count > 0
                && merged[^1] is { } last
                && string.Equals(last.AnimeId, one.AnimeId, StringComparison.Ordinal)
                && last.Kind == one.Kind
                && last.FirstEpisodeNumber + last.EpisodeCount == one.FirstEpisodeNumber
                && last.FirstEpisodeInEntry + last.EpisodeCount == one.FirstEpisodeInEntry)
            {
                merged[^1] = last with { EpisodeCount = last.EpisodeCount + 1 };

                continue;
            }

            merged.Add(one);
        }

        return merged;
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
            narrowest.Kind);
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
            if (span.Season < 1 || span.Kind == AniDbEpisodeKind.Special)
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
        AniDbEpisodeKind kind,
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
                ReadRanges(ref reader, animeId, kind, showId, season, spans, seriesKeys);
            }
            else
            {
                ReadTmdbClaim(ref reader, animeId, kind, showId, season, tmdbCandidates);
            }
        }
    }

    private static void ReadRanges(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
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

            if (inEntry == null || inSeason == null || ReadBand(kind, inEntry) is not { } banded)
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

            var span = new AniBridgeSpan(season, banded.InEntry, inSeason, banded.Kind);

            // The specials and other scopes of one entry say the same thing twice once the band
            // above is read for what it is.
            if (!list.Contains(span))
            {
                list.Add(span);
            }
        }
    }

    private static void ReadTmdbClaim(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
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

        if (kind != AniDbEpisodeKind.Regular || season < 1 || earliest == int.MaxValue)
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
    /// <param name="kind">Which of the entry's numberings the scope counts.</param>
    /// <returns><c>true</c> where the descriptor names a scope this reads.</returns>
    private static bool TryReadScope(string descriptor, out string animeId, out AniDbEpisodeKind kind)
    {
        animeId = string.Empty;
        kind = AniDbEpisodeKind.Regular;

        var parts = descriptor.Split(':');

        if (parts.Length != 3 || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))
        {
            return false;
        }

        // "R" numbers the entry's ordinary episodes, "S" its specials and "O" its other
        // episodes. Anything else is a scope this does not read.
        switch (parts[2])
        {
            case "R":
                break;
            case "S":
                kind = AniDbEpisodeKind.Special;
                break;
            case "O":
                kind = AniDbEpisodeKind.Other;
                break;
            default:
                return false;
        }

        animeId = parts[1];

        return true;
    }

    /// <summary>
    /// Rewrites a span of the specials scope that is really one of the other episodes.
    /// </summary>
    /// <remarks>
    /// The specials scope numbers every episode that is not an ordinary one in a single run,
    /// AniDB's type deciding where in it a number falls: specials from 1, and the other
    /// episodes from 401. Every number in the set is in one band or the other, and where an
    /// entry carries both scopes they say the same thing twice - anime 13473's specials scope
    /// maps 401-412 onto the same season episodes its other scope maps 1-12 onto - so reading
    /// the band as what it is makes the two agree instead of compete, and gives the six entries
    /// that carry only the specials scope an answer that names a document on disk.
    /// </remarks>
    /// <param name="kind">The kind the scope named.</param>
    /// <param name="inEntry">The episodes of the entry the span covers.</param>
    /// <returns>The kind and range to record, or <c>null</c> where the range falls in neither band.</returns>
    private static (AniDbEpisodeKind Kind, AniBridgeRange InEntry)? ReadBand(AniDbEpisodeKind kind, AniBridgeRange inEntry)
    {
        if (kind != AniDbEpisodeKind.Special || inEntry.Start < OtherEpisodeBand)
        {
            // A special numbered into a later band alongside one that is not cannot be read as
            // either, and no such span exists in the set.
            return inEntry.End >= OtherEpisodeBand && kind == AniDbEpisodeKind.Special
                ? null
                : (kind, inEntry);
        }

        return (
            AniDbEpisodeKind.Other,
            new AniBridgeRange(inEntry.Start - OtherEpisodeBand, inEntry.End is { } end ? end - OtherEpisodeBand : null));
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
