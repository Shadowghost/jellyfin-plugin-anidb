using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Puts the segments claiming one season into the order its episodes run through them. Both
/// mapping sources produce their claims entry by entry, in no particular order, and neither
/// always says how long a claim is.
/// </summary>
internal static class SeasonSegments
{
    /// <summary>
    /// Orders the claims and gives each open-ended one the room up to the next.
    /// </summary>
    /// <param name="claims">The segments claiming the season, in any order.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Order(List<AniDbSeasonSegment> claims)
    {
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
    /// A segment written out for a log message.
    /// </summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The description.</returns>
    public static string Describe(AniDbSeasonSegment segment)
        => segment.EpisodeCount > 0
            ? FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber}-{segment.FirstEpisodeNumber + segment.EpisodeCount - 1} from anime {segment.AnimeId} episodes {segment.FirstEpisodeInEntry}-{segment.FirstEpisodeInEntry + segment.EpisodeCount - 1}")
            : FormattableString.Invariant(
                $"episodes {segment.FirstEpisodeNumber} onwards from anime {segment.AnimeId} episode {segment.FirstEpisodeInEntry} onwards");
}
