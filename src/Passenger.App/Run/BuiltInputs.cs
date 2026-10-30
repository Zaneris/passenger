using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Whether a running program's project changed since the build it runs began, read before anything is built, so
/// Rerun stops only what it builds again and never builds over a program's running files (on Windows they are locked while
/// it runs).</summary>
public static class BuiltInputs
{
    /// <summary>What a build's inputs were as it began, read just before it: the time, and each project's input files then
    /// (<see cref="ChangedSince"/>'s walk, from <paramref name="project"/>). Reads the disk: off the UI thread.</summary>
    public static Listing Listed(string project, IReadOnlyList<ProjectModel> projects, bool references = true)
    {
        DateTime began = DateTime.UtcNow;
        Dictionary<string, IReadOnlySet<string>> files = new(PathNames.Comparer);
        Walk(project, projects, references, (current, inputs) =>
        {
            files[current] = new HashSet<string>(inputs, PathNames.Comparer);
            return false;
        });
        return new Listing(began, files);
    }

    /// <summary>Whether a project's inputs changed since <paramref name="listing"/>: a file written after its build began, or
    /// one come or gone since (deleted, renamed, moved in keeping an older time), under the project's folder (another
    /// project's folder under it not entered, nor bin, obj or a hidden folder) or, with <paramref name="references"/>, under
    /// the folders of the projects it references, in turn; a project the listing does not hold by its times alone. On any
    /// thread.</summary>
    public static bool ChangedSince(string project, Listing listing, IReadOnlyList<ProjectModel> projects, bool references = true) =>
        Walk(project, projects, references, (current, inputs) =>
        {
            HashSet<string> now = new(PathNames.Comparer);
            foreach (string file in inputs)
            {
                if (File.GetLastWriteTimeUtc(file) > listing.Began)
                {
                    return true;
                }
                now.Add(file);
            }
            return listing.Files.TryGetValue(current, out IReadOnlySet<string>? then) && !then.SetEquals(now);
        });

    /// <summary>Each project of the walk (canonical) with its input files, until <paramref name="stops"/> answers true;
    /// whether it did.</summary>
    private static bool Walk(string project, IReadOnlyList<ProjectModel> projects, bool references, Func<string, IEnumerable<string>, bool> stops)
    {
        HashSet<string> others = new(projects.Select(p => PathNames.Canonical(p.Folder)), PathNames.Comparer);
        HashSet<string> seen = new(PathNames.Comparer);
        Stack<string> next = new([PathNames.Canonical(project)]);
        while (next.TryPop(out string? current))
        {
            if (!seen.Add(current))
            {
                continue;
            }
            string folder = PathNames.Canonical(Path.GetDirectoryName(current)!);
            if (stops(current, FolderWalk.Files(folder, built: true, enters: inner => PathNames.Same(inner, folder) || !others.Contains(inner)).Where(IsInput)))
            {
                return true;
            }
            if (references)
            {
                foreach (string reference in projects.FirstOrDefault(p => PathNames.Same(p.Path, current))?.ProjectReferences ?? [])
                {
                    next.Push(PathNames.Canonical(reference));
                }
            }
        }
        return false;
    }

    /// <summary>A build's inputs as it began (<see cref="Listed"/>): when, and each project's input files by its canonical path.</summary>
    public sealed record Listing(DateTime Began, IReadOnlyDictionary<string, IReadOnlySet<string>> Files);

    /// <summary>A build's output as it stands, which a run of services compares to know which service's build changed since it
    /// started: each assembly's, symbols' and settings' name, length and time. A file gone as it is read is left out, and a
    /// folder gone, never made or unreadable stamps empty. On any thread.</summary>
    public static string Stamp(string? output)
    {
        if (output is null)
        {
            return string.Empty;
        }
        try
        {
            return string.Join('|', Directory.EnumerateFiles(output)
                .Where(file => PathNames.HasExtension(file, [".dll", ".pdb", ".json"]))
                .Order(StringComparer.Ordinal)
                .Select(file => new FileInfo(file))
                // Exists reads the file's state once, which Length then gives, whatever comes of the file after.
                .Where(file => file.Exists)
                .Select(file => $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>What a build reads that its program could not take in while it runs: code, project files, resources, XAML,
    /// settings and the static files the build makes into others (scoped CSS, <see cref="BuildInputs.IsProcessedStatic"/>).
    /// Not what a running program writes beside its project (DCP's <c>.dcp.json</c>, a SQLite database), nor other static
    /// web assets, served from where they are.</summary>
    private static bool IsInput(string file) => PathNames.HasExtension(file, [".json"])
        ? !file.EndsWith(".dcp.json", StringComparison.OrdinalIgnoreCase)
        : PathNames.HasExtension(file, [".cs", ".vb", ".fs", ".fsi", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".razor", ".cshtml",
            ".vbhtml", ".resx", ".xaml", ".axaml", ".paml", ".config", ".proto"]) || BuildInputs.IsProcessedStatic(file);
}
