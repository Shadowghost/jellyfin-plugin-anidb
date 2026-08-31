using System.Collections.Generic;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One mapping source's account of how a season is filled, and which source gave it.
/// </summary>
/// <param name="Segments">The entries the season is filled from, in the order its episodes run through them.</param>
/// <param name="Source">Which source placed it, as a noun phrase for a log message.</param>
internal sealed record SeasonPlacement(IReadOnlyList<AniDbSeasonSegment> Segments, string Source);
