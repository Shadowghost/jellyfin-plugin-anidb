using System;
using System.Net.Mime;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AniDB.Api;

/// <summary>
/// Reports what the plugin is doing with AniDB, so that the configuration page can say
/// whether requests are flowing, queued or paused by a ban.
/// </summary>
/// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("AniDB")]
[Produces(MediaTypeNames.Application.Json)]
public class AniDbStatusController(IApplicationPaths applicationPaths) : ControllerBase
{
    private readonly IApplicationPaths _applicationPaths = applicationPaths;

    /// <summary>
    /// Gets the plugin's current standing with AniDB.
    /// </summary>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AniDbStatus> GetStatus()
    {
        var (banRemaining, queued, untilNext, sent, lastSentUtc) = AniDbSeriesProvider.GetRequestStatus();
        var (cachedAtUtc, entryCount, maxAgeInDays) = AniDbAnimeList.GetStatus(_applicationPaths);
        var (bridgeCachedAtUtc, bridgeEntryCount, bridgeMaxAgeInDays) = AniBridgeMappings.GetStatus(_applicationPaths);
        var (overridesPath, overridesWrittenAtUtc, overridesEntryCount) = AniDbMappingOverrides.GetStatus(_applicationPaths);

        return new AniDbStatus
        {
            IsBanned = banRemaining > TimeSpan.Zero,
            BanRemainingSeconds = banRemaining.TotalSeconds,
            QueuedRequests = queued,
            NextRequestInSeconds = untilNext.TotalSeconds,
            RequestsSent = sent,
            LastRequestUtc = lastSentUtc,
            RequestIntervalMs = Plugin.Instance.Configuration.RequestIntervalMs,
            AnimeListCachedAtUtc = cachedAtUtc,
            AnimeListEntryCount = entryCount,
            AnimeListMaxAgeDays = maxAgeInDays,
            AniBridgeCachedAtUtc = bridgeCachedAtUtc,
            AniBridgeEntryCount = bridgeEntryCount,
            AniBridgeMaxAgeDays = bridgeMaxAgeInDays,
            AniBridgeEnabled = Plugin.Instance.Configuration.UseAniBridgeMappings,
            OverridesPath = overridesPath,
            OverridesWrittenAtUtc = overridesWrittenAtUtc,
            OverridesEntryCount = overridesEntryCount
        };
    }
}
