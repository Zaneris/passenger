using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>The release notes a build carries beside it, never fetched: a Markdown file a version, the repository's
/// <c>docs/release-notes/X.Y.Z.md</c> as <c>release-notes/X.Y.Z.md</c>, which the release's own notes on GitHub are too. A
/// patch release's What's New shows its minor version's earlier notes beneath its own (<see cref="Earlier"/>). Notes
/// carrying <see cref="SecurityMarker"/> are a security release's, said as one wherever the release is
/// (<see cref="SecurityOf"/>).</summary>
public sealed record ReleaseNotes(string Folder, string Version)
{
    /// <summary>The line, a comment no reader of the notes shows, that makes notes a security release's; their Security
    /// section says what it fixes, its first line said wherever the release is.</summary>
    public const string SecurityMarker = "<!-- Security release -->";

    /// <summary>The folder beside the app that holds them.</summary>
    public static string CarriedFolder { get; } = Path.Combine(AppContext.BaseDirectory, "release-notes");

    /// <summary>This build's: the notes of the version it is.</summary>
    public static ReleaseNotes Carried { get; } = new(CarriedFolder, Product.Version);

    private static readonly Lazy<string> Canonical = new(() => PathNames.Canonical(CarriedFolder));

    /// <summary>The folders a window was given notes from in place of the carried one (a test's), canonical, kept for the
    /// process as the carried one is.</summary>
    private static volatile string[] given = [];

    private static readonly Lock Giving = new();

    /// <summary>The version's file.</summary>
    public string File => Path.Combine(Folder, Version + ".md");

    /// <summary>Whether the build carries notes of this version.</summary>
    public bool Exists => System.IO.File.Exists(File);

    /// <summary>The notes the folder carries of this version's minor version before it, newest first: those a patch release's
    /// What's New shows beneath its own (0.4.0's under 0.4.1's), none for a minor release's.</summary>
    public IReadOnlyList<ReleaseNotes> Earlier()
    {
        try
        {
            return [.. VersionOrder.NewestFirst(Directory.EnumerateFiles(Folder, "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>())
                .Where(version => !VersionOrder.IsPrerelease(version) && VersionOrder.SameMinor(version, Version) && VersionOrder.Instance.Compare(version, Version) < 0)
                .Select(version => this with { Version = version })];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The notes as written, or null where their file can no longer be read.</summary>
    public string? Read()
    {
        try
        {
            return System.IO.File.ReadAllText(File);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Takes a folder a window shows notes from in place of the carried one (a test's), whose files are then notes
    /// as the carried ones are (<see cref="VersionOf"/>).</summary>
    public static void Give(string folder)
    {
        string canonical = PathNames.Canonical(folder);
        lock (Giving)
        {
            if (!PathNames.Same(canonical, Canonical.Value) && !given.Contains(canonical, PathNames.Comparer))
            {
                given = [.. given, canonical];
            }
        }
    }

    /// <summary>What a security release's notes say it fixes, the first line of their Security section in plain words with no
    /// full stop, empty where they have none; null for notes not marked a security release's (<see cref="SecurityMarker"/>).</summary>
    public static string? SecurityOf(string notes)
    {
        if (!notes.ReplaceLineEndings("\n").Split('\n').Any(line => line.Trim() == SecurityMarker))
        {
            return null;
        }
        IReadOnlyList<Editors.Markdown.Block> blocks = Editors.Markdown.Parse(notes);
        int heading = blocks.ToList().FindIndex(b => b is Editors.Markdown.Heading { Text: var text } && text.Trim().Equals("Security", StringComparison.OrdinalIgnoreCase));
        Editors.Markdown.Block? first = heading < 0 || heading + 1 >= blocks.Count ? null : blocks[heading + 1];
        string said = first switch
        {
            Editors.Markdown.Paragraph paragraph => paragraph.Text,
            Editors.Markdown.Listing { Items: [[Editors.Markdown.Paragraph item, ..], ..] } => item.Text,
            _ => string.Empty,
        };
        // Said within a sentence of the notice's own, so without the full stop that ends it.
        return Editors.Markdown.Plain(Editors.Markdown.Inlines(said.Split('\n')[0].Trim())).TrimEnd().TrimEnd('.');
    }

    /// <summary>What the notes of a version are called on their tab.</summary>
    public static string TitleOf(string version) => $"What's New in {version}";

    /// <summary>The heading a version's notes are shown under, at the page's head and above each earlier version's beneath.</summary>
    public static string HeadingOf(string version) => $"What's New in Passenger {version}";

    /// <summary>The version whose notes a file (by its canonical path) is, when it is notes the app carries or a window was
    /// given (<see cref="Give"/>); null for any other file, read without a string made for it but a Markdown file's, as each
    /// key and caret move asks of the file shown.</summary>
    public static string? VersionOf(string path) =>
        Path.GetExtension(path.AsSpan()).Equals(".md", StringComparison.OrdinalIgnoreCase)
        && Path.GetDirectoryName(path) is { } folder && Holds(folder)
            ? Path.GetFileNameWithoutExtension(path)
            : null;

    /// <summary>Whether a folder (canonical) is the carried notes' or one a window was given.</summary>
    private static bool Holds(string folder)
    {
        if (PathNames.Same(folder, Canonical.Value))
        {
            return true;
        }
        foreach (string other in given)
        {
            if (PathNames.Same(folder, other))
            {
                return true;
            }
        }
        return false;
    }
}
