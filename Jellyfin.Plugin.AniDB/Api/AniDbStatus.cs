using System;

namespace Jellyfin.Plugin.AniDB.Api;

/// <summary>
/// How the plugin currently stands with AniDB, as the configuration page shows it.
/// </summary>
public class AniDbStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether AniDB is currently refusing requests, so that
    /// none will be sent until the pause has run out.
    /// </summary>
    public bool IsBanned { get; set; }

    /// <summary>
    /// Gets or sets how many seconds are left of that pause.
    /// </summary>
    public double BanRemainingSeconds { get; set; }

    /// <summary>
    /// Gets or sets how many requests are waiting for a slot behind the rate limit.
    /// </summary>
    public int QueuedRequests { get; set; }

    /// <summary>
    /// Gets or sets how many seconds are left before the next request may be sent.
    /// </summary>
    public double NextRequestInSeconds { get; set; }

    /// <summary>
    /// Gets or sets how many requests have been sent to AniDB since the server started.
    /// </summary>
    public long RequestsSent { get; set; }

    /// <summary>
    /// Gets or sets when the last request was sent, or <c>null</c> when none has been.
    /// </summary>
    public DateTime? LastRequestUtc { get; set; }

    /// <summary>
    /// Gets or sets the configured gap between two requests, in milliseconds.
    /// </summary>
    public int RequestIntervalMs { get; set; }

    /// <summary>
    /// Gets or sets when the cached copy of the anime list was downloaded, or <c>null</c>
    /// when none has been.
    /// </summary>
    public DateTime? AnimeListCachedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets how many AniDB entries have been read from that copy. Zero until the
    /// first season is looked up, which is when the list is first read.
    /// </summary>
    public int AnimeListEntryCount { get; set; }

    /// <summary>
    /// Gets or sets how many days the cached copy is used for before it is downloaded again.
    /// </summary>
    public int AnimeListMaxAgeDays { get; set; }

    /// <summary>
    /// Gets or sets when the cached copy of the AniBridge mappings was downloaded, or
    /// <c>null</c> when none has been.
    /// </summary>
    public DateTime? AniBridgeCachedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets how many AniDB entries have been read from that copy. Zero until the
    /// first season is looked up, which is when the mappings are first read.
    /// </summary>
    public int AniBridgeEntryCount { get; set; }

    /// <summary>
    /// Gets or sets how many days the cached copy is used for before it is downloaded again.
    /// </summary>
    public int AniBridgeMaxAgeDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AniBridge mappings are consulted at all.
    /// </summary>
    public bool AniBridgeEnabled { get; set; }

    /// <summary>
    /// Gets or sets where the mapping overrides are read from, whether or not a file is there.
    /// Nothing downloads that file, so the page has to say where to put one.
    /// </summary>
    public string OverridesPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the override file was last written, or <c>null</c> where there is no
    /// such file, which is the ordinary state of an installation.
    /// </summary>
    public DateTime? OverridesWrittenAtUtc { get; set; }

    /// <summary>
    /// Gets or sets how many AniDB entries have been read from it. Zero until the first season
    /// is looked up, which is when the file is first read.
    /// </summary>
    public int OverridesEntryCount { get; set; }
}
