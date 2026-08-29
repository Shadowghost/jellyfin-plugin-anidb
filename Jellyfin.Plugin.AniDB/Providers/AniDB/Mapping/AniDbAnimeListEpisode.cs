namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// Where a single episode is read from, once the anime list has placed it.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry holding it.</param>
/// <param name="Number">Its number within that entry.</param>
/// <param name="IsSpecial">Whether that number is one of the entry's specials rather than one of its ordinary episodes.</param>
internal sealed record AniDbAnimeListEpisode(string AnimeId, int Number, bool IsSpecial);
