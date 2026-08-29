using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AniDB.Providers.AniDB.Identity;

namespace Jellyfin.Plugin.AniDB.Providers;

/// <summary>
/// Fuzzy matching helpers used to map a series name onto an AniDB id.
/// </summary>
internal static partial class Equals_check
{
    /// <summary>
    /// Cut p(%) away from the string.
    /// </summary>
    /// <param name="input">The string to shorten.</param>
    /// <param name="minLength">The minimum length of the result.</param>
    /// <param name="p">The percentage to cut away.</param>
    /// <returns>The shortened string.</returns>
    public static string ShortenString(string input, int minLength = 0, int p = 50)
    {
        if (input.Length <= minLength)
        {
            return input;
        }

        int newLength = (int)(input.Length - ((input.Length / 100f) * p));

        if (newLength < minLength)
        {
            newLength = minLength;
        }

        return input[..newLength];
    }

    /// <summary>
    /// Escape string for regex, but fuzzy.
    /// </summary>
    /// <param name="a">The string to escape.</param>
    /// <returns>The fuzzy regex pattern.</returns>
    public static string FuzzyRegexEscape(string a)
    {
        a = Regex.Escape(a);

        // Make the escaped characters fuzzy.
        a = a.Replace(@"\\", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\*", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\+", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\?", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\|", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\{", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\[", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\(", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\)", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\^", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\$", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\.", ".?", StringComparison.Ordinal);
        a = a.Replace(@"\#", ".?", StringComparison.Ordinal);

        // Whitespace.
        a = a.Replace(@"\ ", ".?.?.?", StringComparison.Ordinal);
        a = WhitespaceRegex().Replace(a, ".?.?.?");

        // Other characters.
        a = SpecialCharacterRegex().Replace(a, ".?");

        // Words.
        a = SAtEndBoundaryRegex().Replace(a, ".?s");
        a = a.Replace("Gekijyouban", "Gekijouban", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("Mahoutsukai", "Mahou ?tsukai", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("to aru", "to ?aru", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("re", "re.?", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("OVA", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("OAD", "((OVA)|(OAD))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("wo", "w?o", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("c", "(c|k)", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("k", "(c|k)", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("n", "n`?", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("&", "(&|(and))", StringComparison.OrdinalIgnoreCase);
        a = a.Replace("and", "(&|(and))", StringComparison.OrdinalIgnoreCase);

        return a;
    }

    /// <summary>
    /// simple regex.
    /// </summary>
    /// <param name="regex">The regex to match with.</param>
    /// <param name="input">The input to match against.</param>
    /// <param name="group">The capture group to return.</param>
    /// <param name="matchInt">The index of the match to return.</param>
    /// <returns>The captured value, or an empty string when there is no match.</returns>
    public static string OneLineRegex(Regex regex, string input, int group = 1, int matchInt = 0)
    {
        int x = 0;
        foreach (Match match in regex.Matches(input))
        {
            if (x == matchInt)
            {
                return match.Groups[group].Value;
            }

            x++;
        }

        return string.Empty;
    }

    /// <summary>
    /// Searches for possible AniDB IDs for name.
    /// </summary>
    /// <param name="name">The name to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="x_">The current attempt; the titles file is downloaded once when it cannot be read.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation, containing the matching AniDB IDs.</returns>
    public static async Task<List<string>> XmlSearch(string name, CancellationToken cancellationToken, int x_ = 0)
    {
        string? xml = await ReadTitlesXml(x_, cancellationToken).ConfigureAwait(false);

        return xml is null ? [] : SearchTitlesXml(xml, name);
    }

    /// <summary>
    /// Finds an AniDB ID for name.
    /// </summary>
    /// <param name="name">The name to search for.</param>
    /// <param name="year">The year the series is known to be from, used to tell apart two shows of the same name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="x_">The current attempt; the titles file is downloaded once when it cannot be read.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation, containing the best matching AniDB ID.</returns>
    public static async Task<string> XmlFindId(string name, int? year, CancellationToken cancellationToken, int x_ = 0)
    {
        // Read once and reuse for both the search and the comparison below; the file is
        // several megabytes.
        string? xml = await ReadTitlesXml(x_, cancellationToken).ConfigureAwait(false);
        if (xml is null)
        {
            return string.Empty;
        }

        var strippedName = StripYearRegex().Replace(name, string.Empty).Trim();

        // Index every entry once with a constant pattern rather than compiling a fresh regex
        // per candidate id. The first entry for an id wins.
        var entriesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match entry in AnimeEntryRegex().Matches(xml))
        {
            entriesById.TryAdd(entry.Groups[1].Value, entry.Groups[2].Value);
        }

        // A title AniDB spells as the library does settles the question, and settles it without
        // the fuzzy search, which reduces a name to its first few letters and so returns every
        // show whose name begins alike.
        //
        // The name is tried with the year first. Two shows made years apart under one name are
        // told apart by nothing else, and AniDB gives the later one a title carrying its year,
        // which is what this matches. The year is put back because the scanner has already
        // taken it out of the name and into its own field. Each spelling is tried as written
        // before being reduced to its letters and digits, so that a title differing only in
        // punctuation - a sequel marked with an apostrophe, say - cannot take the original's
        // place.
        foreach (var (candidate, loose) in GetNameCandidates(name, strippedName, year))
        {
            var matches = FindByTitle(entriesById, candidate, loose);

            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        var results = SearchTitlesXml(xml, name);

        if (results.Count == 1)
        {
            return results[0];
        }

        int lowestDistance = Plugin.Instance.Configuration.TitleSimilarityThreshold;
        string currentId = string.Empty;

        foreach (string id in results)
        {
            if (!entriesById.TryGetValue(id, out string? nameXmlFromId))
            {
                continue;
            }

            string[] lines = nameXmlFromId.Split(
                ["\r\n", "\r", "\n"],
                StringSplitOptions.None);

            foreach (string line in lines)
            {
                string nameFromId = OneLineRegex(TitleRegex(), line);

                if (!string.IsNullOrEmpty(nameFromId))
                {
                    // Compared without the year, which AniDB writes into a title only where it
                    // has to. Leaving it in counts every one of its characters as a difference,
                    // which is enough to lose the right entry to a longer name that happens to
                    // carry digits.
                    int stringDistance = LevenshteinDistance(strippedName, nameFromId);
                    if (lowestDistance > stringDistance)
                    {
                        lowestDistance = stringDistance;
                        currentId = id;
                    }
                }
            }
        }

        return currentId;
    }

    /// <summary>
    /// The spellings of a name to look for, in the order they settle a match. A spelling
    /// carrying the year comes before one without, and an exact spelling before a reduced one.
    /// </summary>
    /// <param name="name">The name as the library holds it.</param>
    /// <param name="strippedName">The same name with any trailing year removed.</param>
    /// <param name="year">The year the series is known to be from.</param>
    /// <returns>Each spelling, and whether to compare it reduced to letters and digits.</returns>
    private static IEnumerable<(string Candidate, bool Loose)> GetNameCandidates(string name, string strippedName, int? year)
    {
        var withYear = year.HasValue
            ? FormattableString.Invariant($"{strippedName} ({year.Value})")
            : null;

        foreach (var loose in new[] { false, true })
        {
            if (withYear != null)
            {
                yield return (withYear, loose);
            }

            if (!string.Equals(name, withYear, StringComparison.Ordinal))
            {
                yield return (name, loose);
            }

            if (!string.Equals(strippedName, name, StringComparison.Ordinal)
                && !string.Equals(strippedName, withYear, StringComparison.Ordinal))
            {
                yield return (strippedName, loose);
            }
        }
    }

    /// <summary>
    /// The ids of every entry AniDB gives the given title to, matched whole rather than fuzzily.
    /// </summary>
    /// <param name="entriesById">Every entry of the titles file, by AniDB id.</param>
    /// <param name="name">The name to look for.</param>
    /// <param name="loose">Whether to compare the names reduced to their letters and digits, rather than as written.</param>
    /// <returns>The matching AniDB ids.</returns>
    private static List<string> FindByTitle(IReadOnlyDictionary<string, string> entriesById, string name, bool loose)
    {
        var matches = new List<string>();
        var wanted = NormalizeTitle(name, loose);

        if (wanted.Length == 0)
        {
            return matches;
        }

        foreach (var entry in entriesById)
        {
            foreach (Match title in TitleRegex().Matches(entry.Value))
            {
                if (string.Equals(NormalizeTitle(title.Groups[1].Value, loose), wanted, StringComparison.Ordinal))
                {
                    matches.Add(entry.Key);

                    break;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Reduces a title for comparison. Case and runs of whitespace never distinguish two
    /// spellings of a name; punctuation is dropped only when asked, because an apostrophe or a
    /// full stop is sometimes the whole difference between a show and its sequel. A year is
    /// always kept: it is what tells a remake from the show it is named after.
    /// </summary>
    /// <param name="value">The title to reduce.</param>
    /// <param name="loose">Whether to drop everything that is not a letter or a digit.</param>
    /// <returns>The reduced title.</returns>
    private static string NormalizeTitle(string value, bool loose)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;

                continue;
            }

            if (loose && !char.IsLetterOrDigit(character))
            {
                continue;
            }

            if (pendingSpace && !loose)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Calculates the Levenshtein distance - a metric for measuring the difference between two strings.
    /// The higher the number, the more different the two strings are.
    /// </summary>
    /// <param name="str1">The first string.</param>
    /// <param name="str2">The second string.</param>
    /// <returns>The Levenshtein distance between both strings.</returns>
    private static int LevenshteinDistance(string str1, string str2)
    {
        var str1Length = str1.Length;
        var str2Length = str2.Length;

        if (str1Length == 0)
        {
            return str2Length;
        }

        if (str2Length == 0)
        {
            return str1Length;
        }

        var matrix = new int[str1Length + 1][];

        for (var i = 0; i <= str1Length; i++)
        {
            matrix[i] = new int[str2Length + 1];
            matrix[i][0] = i;
        }

        for (var j = 0; j <= str2Length; j++)
        {
            matrix[0][j] = j;
        }

        for (var i = 1; i <= str1Length; i++)
        {
            for (var j = 1; j <= str2Length; j++)
            {
                var cost = (str2[j - 1] == str1[i - 1]) ? 0 : 1;
                matrix[i][j] = Math.Min(
                    Math.Min(matrix[i - 1][j] + 1, matrix[i][j - 1] + 1),
                    matrix[i - 1][j - 1] + cost);
            }
        }

        return matrix[str1Length][str2Length];
    }

    /// <summary>
    /// Reads the AniDB titles file, downloading it once if it cannot be read.
    /// </summary>
    /// <param name="attempt">The current attempt; the file is downloaded only on the first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file contents, or <see langword="null"/> when it could not be read.</returns>
    private static async Task<string?> ReadTitlesXml(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(GetAnidbXml(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (attempt != 0)
            {
                return null;
            }
        }

        await Task.Run(() => AniDbTitleDownloader.LoadStatic(cancellationToken), cancellationToken).ConfigureAwait(false);

        return await ReadTitlesXml(1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects the AniDB ids whose entry fuzzily matches a name.
    /// </summary>
    /// <param name="xml">The contents of the AniDB titles file.</param>
    /// <param name="name">The name to search for.</param>
    /// <returns>The matching AniDB ids.</returns>
    private static List<string> SearchTitlesXml(string xml, string name)
    {
        var results = new List<string>();
        string strippedName = StripYearRegex().Replace(name, string.Empty);

        // The pattern embeds the search term, so it cannot be a [GeneratedRegex]. Compiled
        // earns its build cost: the atomic group backtracks heavily across a multi-megabyte
        // document.
        var searchRegex = new Regex(
            @"<anime aid=""([0-9]+)"">(?>[^<>]+|<(?!\/anime>)[^<>]*>)*?.*" + FuzzyRegexEscape(ShortenString(strippedName, 6, 20)),
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Enumerate once. Indexing in one at a time rescans the whole document per result,
        // which is quadratic in the match count for a common name.
        foreach (Match match in searchRegex.Matches(xml))
        {
            string id = match.Groups[1].Value;
            if (string.IsNullOrEmpty(id))
            {
                break;
            }

            results.Add(id);
        }

        return results;
    }

    /// <summary>
    /// Gets the path of the AniDB titles.xml file.
    /// </summary>
    /// <returns>The path of the AniDB titles.xml file.</returns>
    private static string GetAnidbXml()
    {
        return AniDbTitleDownloader.StaticTitlesFilePath;
    }

    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[!,–—_=~'`‚‘’„“”:;␣#@<>}\]\/\-]")]
    private static partial Regex SpecialCharacterRegex();

    [GeneratedRegex(@"s\b")]
    private static partial Regex SAtEndBoundaryRegex();

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\s*\([0-9]{4}\)\s*$")]
    private static partial Regex StripYearRegex();

    [GeneratedRegex(@"<anime aid=""([0-9]+)""((?s).*?)</anime>")]
    private static partial Regex AnimeEntryRegex();
}
