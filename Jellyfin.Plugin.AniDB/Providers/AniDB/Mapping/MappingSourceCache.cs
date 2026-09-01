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
/// One mapping file, and whatever was parsed from it. A file with a URL is fetched when the
/// copy on disk is missing or has gone stale; one without is only ever read, being written by
/// whoever runs the server. Either is parsed once per copy, and what came out of it is kept in
/// memory until that copy changes underneath it.
/// </summary>
/// <typeparam name="TIndex">What the file is parsed into. Placements worked out from one copy belong to it, so anything memoised per lookup belongs on this rather than beside the cache.</typeparam>
/// <param name="fileName">What the copy on disk is called, within the folder named below.</param>
/// <param name="url">Where the file is downloaded from, or <c>null</c> for a file nobody downloads: one written by hand, which is allowed not to be there and is never thrown away.</param>
/// <param name="description">How the file is named in log messages, as a noun phrase: "the anime list".</param>
/// <param name="maxAgeDays">How long a downloaded copy is used before it is fetched again. Means nothing for a file that is not downloaded.</param>
/// <param name="parse">Reads a copy on disk, given its path, a logger and the time it was written.</param>
/// <param name="folder">Where the file lives, given the application paths. Defaults to the plugin's data folder, which is where a downloaded copy belongs.</param>
internal sealed class MappingSourceCache<TIndex>(
    string fileName,
    string? url,
    string description,
    int maxAgeDays,
    Func<string, ILogger, DateTime, TIndex> parse,
    Func<IApplicationPaths, string>? folder = null)
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
    /// Gets how long to wait before looking at a file that could not be read again. A
    /// downloaded one is waited out: nothing here can mend it, and the next attempt is a fresh
    /// download. A file written by hand is mended by editing it, which is a minute's work, so
    /// it is looked at again as often as any other change to it would be noticed.
    /// </summary>
    private TimeSpan RetryPause => url == null ? _recheckInterval : _retryAfterFailure;

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
                // No file at all is the ordinary state of one written by hand, so it is noted
                // as looked at rather than as failed. Noting it is what keeps a scan from
                // asking the file system once per episode, and the recheck interval is what
                // brings a file written since into use without a restart.
                if (url == null)
                {
                    _index = null;
                    _sourceWrittenAtUtc = DateTime.MinValue;
                    _checkedAtUtc = DateTime.UtcNow;

                    return null;
                }

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
                if (url == null)
                {
                    // Not this class's to throw away: it is the only copy there is. It stays
                    // where it is, unread, and whatever it would have said is left to the
                    // sources that are downloaded until it is fixed.
                    logger.LogError(ex, "{Source} at {Path} is not valid JSON, so nothing in it is used. Fix or remove the file", description, path);
                }
                else
                {
                    // A truncated or half-written file would otherwise be read again on every
                    // start until it went stale. Dropping it means the next start downloads afresh.
                    logger.LogWarning(ex, "The cached copy of {Source} at {Path} could not be read and has been discarded", description, path);

                    TryDelete(path);
                }

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

        // Checked against the time of the last look rather than against what it produced: a
        // look that found no file is as good an answer as one that found a whole mapping set.
        return now - _failedAtUtc < RetryPause
            || (_checkedAtUtc != DateTime.MinValue && now - _checkedAtUtc < _recheckInterval);
    }

    /// <summary>
    /// Where the file is. Public so that a page can tell the user where to put one they write
    /// themselves.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <returns>The full path.</returns>
    public string GetPath(IApplicationPaths appPaths)
        => Path.Combine(folder == null ? AniDbTitleDownloader.GetDataPath(appPaths) : folder(appPaths), fileName);

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
        if (url == null)
        {
            return;
        }

        var file = new FileInfo(path);
        var cached = file.Exists && file.Length > 0;

        if (cached && (DateTime.UtcNow - file.LastWriteTimeUtc).TotalDays <= maxAgeDays)
        {
            return;
        }

        try
        {
            await Download(url, path, logger, cancellationToken).ConfigureAwait(false);
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

    private async Task Download(string sourceUrl, string path, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Downloading {Source} from {Url}", description, sourceUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Not paced by the AniDB request gate: this comes from the file's own host, and holding
        // up a scan behind AniDB's rate limit for it would be for nothing.
        var httpClient = Plugin.Instance.GetHttpClient();
        var temporaryFile = path + ".tmp";

        try
        {
            using (var stream = await httpClient.GetStreamAsync(new Uri(sourceUrl), cancellationToken).ConfigureAwait(false))
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
