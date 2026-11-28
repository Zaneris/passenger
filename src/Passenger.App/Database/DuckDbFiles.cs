using Passenger.Core;

namespace Passenger.App.Database;

/// <summary>The open folder's Parquet and CSV files as a DuckDB connection reads them: listed under a schema of their own,
/// each read by its path from the folder, which the connection takes as where a relative path starts, so a console reads
/// one as <c>SELECT * FROM 'sales/2026.parquet'</c> too.</summary>
public static class DuckDbFiles
{
    /// <summary>The schema the folder's files are listed under, a name no DuckDB catalog gives its own.</summary>
    public const string Schema = "Folder files";

    /// <summary>The most files listed, the nearest first.</summary>
    public const int Most = 500;

    /// <summary>Whether a file is one DuckDB reads as a table: Parquet, CSV or tab-separated.</summary>
    public static bool Reads(string path) => PathNames.HasExtension(path, [".parquet", ".csv", ".tsv"]);

    /// <summary>The folder's files DuckDB reads, by their paths from it in forward slashes, past build output, dependencies
    /// and hidden folders.</summary>
    public static IReadOnlyList<string> Under(string folder) => !Directory.Exists(folder) ? []
        : [.. PathNames.FilesUnder(folder, ProjectFolders.Enters).SelectMany(f => f.Files).Where(Reads).Take(Most)
            .Select(file => PathNames.Relative(file, folder)).OfType<string>()];

    /// <summary>The folder's DuckDB database files, by their full paths, and whether it holds a file DuckDB reads as a table,
    /// from one walk past build output, dependencies and hidden folders.</summary>
    public static (IReadOnlyList<string> Databases, bool Files) In(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return ([], false);
        }
        List<string> databases = [];
        bool files = false;
        foreach (string file in PathNames.FilesUnder(folder, ProjectFolders.Enters).SelectMany(f => f.Files).Take(Most * 20))
        {
            if (PathNames.HasExtension(file, [".duckdb"]) && databases.Count < Most)
            {
                databases.Add(file);
            }
            files |= Reads(file);
        }
        return (databases, files);
    }

    /// <summary>A listed file as a query reads it: its path as a string, which DuckDB reads by its extension.</summary>
    public static string Read(string relative) => "'" + relative.Replace("'", "''", StringComparison.Ordinal) + "'";
}
