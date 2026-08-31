using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// One downloaded mapping file, and whatever was parsed from it. The file is fetched when the
/// copy on disk is missing or has gone stale, parsed once per copy, and what came out of it is
/// kept in memory until that copy changes underneath it.
/// </summary>
/// <typeparam name="TIndex">What the file is parsed into. Placements worked out from one copy belong to it, so anything memoised per lookup belongs on this rather than beside the cache.</typeparam>
/// <param name="fileName">What the copy on disk is called, within the plugin's data folder.</param>
/// <param name="url">Where the file is downloaded from.</param>
/// <param name="description">How the file is named in log messages, as a noun phrase: "the anime list".</param>
/// <param name="maxAgeDays">How long a downloaded copy is used before it is fetched again.</param>
/// <param name="parse">Reads a copy on disk, given its path, a logger and the time it was written.</param>
internal sealed class MappingSourceCache<TIndex>(
    string fileName,
    string url,
    string description,
    int maxAgeDays,
    Func<string, ILogger, DateTime, TIndex> parse)
    : IDisposable
    where TIndex : class
{
    /// <summary>
    /// How long to wait before trying again once the file could not be read at all. Without a
    /// pause a scan would ask for it once per series and fail every time; without a retry a
    /// server that started while its network was down would never get the file.
    /// </summary>
    private readonly TimeSpan _retryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the copy in memory is used before the cached file is looked at again. Reading
    /// the file's timestamp is cheap, but a scan asks once per episode, and the file only
    /// changes when this class downloads it or someone replaces it by hand.
    /// </summary>
    private readonly TimeSpan _recheckInterval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>
    /// What was parsed from the copy on disk. Every lookup is answered from here.
    /// </summary>
    private TIndex? _index;

    private DateTime _failedAtUtc = DateTime.MinValue;

    /// <summary>
    /// When the cached file was last compared against the copy in memory, and the timestamp
    /// that copy was read from. A server left running for weeks would otherwise keep what it
    /// read at startup for as long as it ran, never learning where the season that started
    /// since belongs, nor noticing a file replaced underneath it.
    /// </summary>
    private DateTime _checkedAtUtc = DateTime.MinValue;
    private DateTime _sourceWrittenAtUtc = DateTime.MinValue;

    /// <summary>
    /// Gets how long a downloaded copy is used before it is fetched again.
    /// </summary>
    public int MaxAgeInDays => maxAgeDays;

    /// <summary>
    /// What the file holds, downloading and parsing it where what is in memory will not do.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed file, or <c>null</c> when it could not be read.</returns>
    public async Task<TIndex?> GetIndex(IApplicationPaths appPaths, ILogger logger, CancellationToken cancellationToken)
    {
        // The common path, taken once per lookup: the file is already in memory and current.
        if (IsCurrent())
        {
            return _index;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsCurrent())
            {
                return _index;
            }

            var path = GetPath(appPaths);

            await Refresh(path, logger, cancellationToken).ConfigureAwait(false);

            var file = new FileInfo(path);

            if (!file.Exists || file.Length == 0)
            {
                _failedAtUtc = DateTime.UtcNow;

                return _index;
            }

            // The file is the copy of record. Where it is the one already parsed there is
            // nothing to do; where it is not - downloaded just now, or replaced by hand - what
            // is in memory is out of date whatever its own age.
            if (_index != null && file.LastWriteTimeUtc == _sourceWrittenAtUtc)
            {
                _checkedAtUtc = DateTime.UtcNow;

                return _index;
            }

            try
            {
                _index = parse(path, logger, file.LastWriteTimeUtc);
                _checkedAtUtc = DateTime.UtcNow;
                _sourceWrittenAtUtc = file.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is XmlException or JsonException)
            {
                // A truncated or half-written file would otherwise be read again on every
                // start until it went stale. Dropping it means the next start downloads afresh.
                logger.LogWarning(ex, "The cached copy of {Source} at {Path} could not be read and has been discarded", description, path);

                TryDelete(path);
                _failedAtUtc = DateTime.UtcNow;
            }
        }
        catch (IOException ex)
        {
            _failedAtUtc = DateTime.UtcNow;

            logger.LogWarning(ex, "Could not read {Source}, so whatever it would have placed is placed some other way", description);
        }
        finally
        {
            _loadGate.Release();
        }

        return _index;
    }

    /// <summary>
    /// What is known of the file, for the status the configuration page shows. Reads nothing
    /// but its timestamp, so it costs little to ask often.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>When the cached copy was downloaded, what was parsed from it, and how many days a copy is used for.</returns>
    public (DateTime? CachedAtUtc, TIndex? Index, int MaxAgeInDays) GetStatus(IApplicationPaths appPaths)
    {
        DateTime? cachedAtUtc = null;

        try
        {
            var file = new FileInfo(GetPath(appPaths));

            if (file.Exists && file.Length > 0)
            {
                cachedAtUtc = file.LastWriteTimeUtc;
            }
        }
        catch (IOException)
        {
            // The status is worth less than the page it is shown on.
        }

        return (cachedAtUtc, _index, maxAgeDays);
    }

    /// <summary>
    /// Releases the gate that keeps two lookups from downloading or parsing at once. Each
    /// source is held for as long as the plugin is loaded, so this is for the analyzer's sake
    /// rather than for a caller's.
    /// </summary>
    public void Dispose() => _loadGate.Dispose();

    /// <summary>
    /// Whether a lookup may be answered from what is in memory, either because the cached file
    /// was checked against it recently enough or because reading it failed recently enough to
    /// be worth a pause.
    /// </summary>
    /// <returns><c>true</c> when the cached file need not be looked at.</returns>
    private bool IsCurrent()
    {
        var now = DateTime.UtcNow;

        return now - _failedAtUtc < _retryAfterFailure
            || (_index != null && now - _checkedAtUtc < _recheckInterval);
    }

    private string GetPath(IApplicationPaths appPaths)
        => Path.Combine(AniDbTitleDownloader.GetDataPath(appPaths), fileName);

    /// <summary>
    /// Downloads the file if the copy on disk is missing or has gone stale. A copy that is
    /// merely stale is kept when the download fails: an old mapping for a show already in the
    /// library is almost always still the right one, and is certainly better than none.
    /// </summary>
    /// <param name="path">Where the file is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task Refresh(string path, ILogger logger, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var cached = file.Exists && file.Length > 0;

        if (cached && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalDays <= maxAgeDays)
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
                throw new IOException(FormattableString.Invariant($"{description} could not be downloaded and no copy is cached"), ex);
            }

            logger.LogWarning(
                ex,
                "{Source} could not be downloaded, so the copy cached on {CachedAt} is used instead",
                description,
                file.LastWriteTimeUtc);
        }
    }

    private async Task Download(string path, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading {Source} from {Url}", description, url);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not paced by the AniDB request gate: this comes from the file's own host, and holding
        // up a scan behind AniDB's rate limit for it would be for nothing.
        var httpClient = Plugin.Instance.GetHttpClient();
        var temporaryFile = path + ".tmp";

        try
        {
            using (var stream = await httpClient.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false))
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
}
