using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>The release notes a build carries beside it, never fetched: a Markdown file a version, the repository's
/// <c>docs/release-notes/X.Y.Z.md</c> as <c>release-notes/X.Y.Z.md</c>, which the release's own notes on GitHub are too. Notes
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

    /// <summary>The version's file.</summary>
    public string File => Path.Combine(Folder, Version + ".md");

    /// <summary>Whether the build carries notes of this version.</summary>
    public bool Exists => System.IO.File.Exists(File);

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

    /// <summary>What the notes of a version are called, on their tab and at their head.</summary>
    public static string TitleOf(string version) => $"What's New in {version}";

    /// <summary>The version whose notes a file (by its canonical path) is, when it is notes the app carries; null for any
    /// other file, read without a string made for it, as each key and caret move asks of the file shown.</summary>
    public static string? VersionOf(string path) =>
        Path.GetExtension(path.AsSpan()).Equals(".md", StringComparison.OrdinalIgnoreCase)
        && Path.GetDirectoryName(path) is { } folder && PathNames.Same(folder, Canonical.Value)
            ? Path.GetFileNameWithoutExtension(path)
            : null;
}
