using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace BmwCoder.Core.Diagnostics;

public sealed record IstaFaultText(string Description, string DatabasePath, string DocumentId);

public sealed class IstaFaultLookup
{
    public string DatabasePath { get; }

    public IstaFaultLookup(string istaDirectory)
    {
        string directory = Path.GetFullPath(istaDirectory);
        if (Directory.Exists(Path.Combine(directory, "SQLiteDBs")))
            directory = Path.Combine(directory, "SQLiteDBs");
        DatabasePath = Path.Combine(directory, "xmlvalueprimitive_ENGB.sqlite");
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("ISTA English fault-text database not found. Select ISTA or its SQLiteDBs folder.", DatabasePath);
    }

    public IReadOnlyList<IstaFaultText> Lookup(string code, CancellationToken cancellationToken = default)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A BMW fault code must contain exactly six hex digits.", nameof(code));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        var clock = Stopwatch.StartNew();
        var handle = connection.Handle ?? throw new InvalidOperationException("SQLite connection has no handle.");
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000,
            _ => cancellationToken.IsCancellationRequested || clock.Elapsed > TimeSpan.FromSeconds(2) ? 1 : 0, null);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT x.id, x.data FROM fts
                JOIN xmlvalueprimitive x ON x.id = CAST(fts.id AS INTEGER)
                WHERE fts MATCH $code AND x.deleted = 0
                AND x.modified = (SELECT MAX(latest.modified) FROM xmlvalueprimitive latest WHERE latest.id = x.id)
                ORDER BY x.modified DESC LIMIT 8
                """;
            command.Parameters.AddWithValue("$code", $"\"{code}\" OR \"0x{code}\"");
            var matches = new List<IstaFaultText>();
            using var results = command.ExecuteReader();
            while (results.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string xml = results.GetString(1);
                using var input = new StringReader(xml);
                using var reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 4_000_000
                });
                var document = XDocument.Load(reader);
                var descriptions = document.Descendants()
                    .Where(e => e.Name.LocalName.Equals("LISTENTRY", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.LocalName.Equals("PARAGRAPH", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.LocalName.Equals("P", StringComparison.OrdinalIgnoreCase))
                    .Select(e => Regex.Replace(e.Value, @"\s+", " ").Trim())
                    .Where(s => s.Length > 7 && s.Length <= 350 &&
                        Regex.IsMatch(s, $@"^(?:0x)?{Regex.Escape(code)}(?:\s|[:;,=-])", RegexOptions.IgnoreCase))
                    .Select(s => Regex.Replace(s, $@"^(?:0x)?{Regex.Escape(code)}[\s:;,=-]+", "", RegexOptions.IgnoreCase))
                    .Where(s => s.Length > 0);
                foreach (string description in descriptions)
                    matches.Add(new IstaFaultText(description, DatabasePath,
                        Convert.ToString(results.GetValue(0), CultureInfo.InvariantCulture)!));
            }
            return matches.DistinctBy(m => m.Description, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("ISTA indexed fault-text lookup exceeded its two-second query budget.", ex);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
        }
    }
}
