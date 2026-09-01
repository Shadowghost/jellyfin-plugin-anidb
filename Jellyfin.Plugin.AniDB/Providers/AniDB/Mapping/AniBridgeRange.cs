using System;
using System.Globalization;

namespace Jellyfin.Plugin.AniDB.Providers.AniDB.Mapping;

/// <summary>
/// A run of consecutive episode numbers, as the AniBridge mappings write one.
/// </summary>
/// <param name="Start">The first episode of the run.</param>
/// <param name="End">The last episode, or <c>null</c> where the run has no end written and so goes on to the end of whatever holds it, as the season now airing does.</param>
internal sealed record AniBridgeRange(int Start, int? End)
{
    /// <summary>
    /// Gets how many episodes the run holds, or <c>null</c> where it has no end.
    /// </summary>
    public int? Length => End is { } end ? Math.Max(end - Start + 1, 0) : null;

    /// <summary>
    /// Reads a run as the mappings write one: "5" for a single episode, "42-63" for a range, or
    /// "14-" for one that goes on.
    /// </summary>
    /// <param name="value">The run as written.</param>
    /// <returns>The run, or <c>null</c> where it is not one of those three forms.</returns>
    public static AniBridgeRange? Read(string? value)
    {
        // The schema also allows a run listing several ranges ("1-3,5-6") and one weighting its
        // episodes against the other side ("14-|2"). No AniDB-to-TVDB mapping in the set uses
        // either, and a season placed by a guess at one would be placed worse than by the
        // source consulted next, so such a run is refused rather than approximated.
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny(',', '|') >= 0)
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var separator = span.IndexOf('-');

        if (separator < 0)
        {
            return int.TryParse(span, CultureInfo.InvariantCulture, out var only) ? new AniBridgeRange(only, only) : null;
        }

        if (!int.TryParse(span[..separator], CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        var rest = span[(separator + 1)..];

        if (rest.IsEmpty)
        {
            return new AniBridgeRange(start, null);
        }

        return int.TryParse(rest, CultureInfo.InvariantCulture, out var end) ? new AniBridgeRange(start, end) : null;
    }
}
