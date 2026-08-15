using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace ZipPaster.Data;

/// <summary>One US ZIP code and the place it belongs to.</summary>
public sealed class ZipRow
{
    public required string Zip { get; init; }
    public required string City { get; init; }
    public required string StateCode { get; init; }
    public required string StateName { get; init; }
    public required string County { get; init; }

    /// <summary>
    /// City lowercased with punctuation removed, for forgiving search.
    /// </summary>
    /// <remarks>
    /// GeoNames strips punctuation from place names -- "O'Fallon" is stored as
    /// "O Fallon", "Wilkes-Barre" as "Wilkes Barre". Without this, a user typing
    /// the name the way it is actually spelled finds nothing.
    /// </remarks>
    public required string CitySearchKey { get; init; }
}

/// <summary>
/// The read-only catalog of every US ZIP code, loaded once from the embedded
/// dataset and queried in memory.
/// </summary>
public sealed class ZipCatalog
{
    private const string ResourceName = "ZipPaster.Resources.us_zipcodes.csv.gz";

    private ZipCatalog(IReadOnlyList<ZipRow> rows, IReadOnlyList<StateInfo> states)
    {
        Rows = rows;
        States = states;
    }

    /// <summary>All ZIPs, ordered by ZIP code.</summary>
    public IReadOnlyList<ZipRow> Rows { get; }

    /// <summary>Distinct states/territories, ordered by name.</summary>
    public IReadOnlyList<StateInfo> States { get; }

    public sealed record StateInfo(string Code, string Name)
    {
        public override string ToString() => $"{Name} ({Code})";
    }

    public static ZipCatalog Load()
    {
        using Stream? raw = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded ZIP dataset '{ResourceName}' is missing. Run tools/build_zipdata.py and rebuild.");

        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var rows = new List<ZipRow>(42_000);

        // Header: zip,city,state_code,state_name,county,lat,lon
        string? line = reader.ReadLine();
        if (line is null)
            throw new InvalidOperationException("Embedded ZIP dataset is empty.");

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;

            string[] f = ParseCsvLine(line);
            if (f.Length < 5)
                continue;

            string city = f[1];
            rows.Add(new ZipRow
            {
                Zip = f[0],
                City = city,
                StateCode = f[2],
                StateName = f[3],
                County = f[4],
                CitySearchKey = MakeSearchKey(city),
            });
        }

        var states = rows
            .GroupBy(r => r.StateCode)
            .Select(g => new StateInfo(g.Key, g.First().StateName))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ZipCatalog(rows, states);
    }

    /// <summary>
    /// Normalizes a place name for matching: lowercase, punctuation removed,
    /// whitespace collapsed. "O'Fallon" and "O Fallon" both become "o fallon".
    /// </summary>
    public static string MakeSearchKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool lastWasSpace = false;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace && sb.Length > 0)
            {
                // Any run of punctuation or whitespace collapses to one space, so
                // hyphens and apostrophes match a plain space.
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Minimal RFC-4180 field splitter. The generated dataset only ever quotes
    /// fields containing a comma (a handful of county names), but quoting must
    /// still be honored or those rows shift a column.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>(7);
        var sb = new StringBuilder(32);
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return [.. fields];
    }
}

/// <summary>How the ZIP grid is currently sorted.</summary>
public enum ZipSort
{
    Zip,
    City,
    State,
}

/// <summary>The active filter over the catalog.</summary>
public sealed record ZipFilter(
    string Search = "",
    string StateCode = "",
    string City = "",
    bool HideUsed = false,
    ZipSort Sort = ZipSort.Zip,
    bool Descending = false)
{
    /// <summary>
    /// Applies the filter and sort. <paramref name="used"/> is the active
    /// project's used set, consulted only when <see cref="HideUsed"/> is on.
    /// </summary>
    public List<ZipRow> Apply(IReadOnlyList<ZipRow> rows, IReadOnlySet<string> used)
    {
        IEnumerable<ZipRow> query = rows;

        if (!string.IsNullOrEmpty(StateCode))
            query = query.Where(r => r.StateCode == StateCode);

        if (!string.IsNullOrEmpty(City))
            query = query.Where(r => r.City == City);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string term = Search.Trim();

            // A numeric term is a ZIP prefix; anything else is a city name, and
            // city matching goes through the punctuation-insensitive key.
            if (term.All(char.IsDigit))
            {
                query = query.Where(r => r.Zip.StartsWith(term, StringComparison.Ordinal));
            }
            else
            {
                string key = ZipCatalog.MakeSearchKey(term);
                query = query.Where(r => r.CitySearchKey.Contains(key, StringComparison.Ordinal));
            }
        }

        if (HideUsed && used.Count > 0)
            query = query.Where(r => !used.Contains(r.Zip));

        Comparison<ZipRow> comparison = Sort switch
        {
            ZipSort.City => (a, b) =>
            {
                int c = string.Compare(a.City, b.City, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Zip, b.Zip);
            },
            ZipSort.State => (a, b) =>
            {
                int c = string.Compare(a.StateCode, b.StateCode, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Zip, b.Zip);
            },
            _ => (a, b) => string.CompareOrdinal(a.Zip, b.Zip),
        };

        var result = query.ToList();
        result.Sort(comparison);

        if (Descending)
            result.Reverse();

        return result;
    }
}
