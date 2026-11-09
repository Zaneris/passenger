using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>The release notes a build carries beside it, never fetched: a Markdown file a version, the repository's
/// <c>docs/release-notes/X.Y.Z.md</c> as <c>release-notes/X.Y.Z.md</c>.</summary>
public sealed record ReleaseNotes(string Folder, string Version)
{
    /// <summary>The folder beside the app that holds them.</summary>
    public static string CarriedFolder { get; } = Path.Combine(AppContext.BaseDirectory, "release-notes");

    /// <summary>This build's: the notes of the version it is.</summary>
    public static ReleaseNotes Carried { get; } = new(CarriedFolder, Product.Version);

    private static readonly Lazy<string> Canonical = new(() => PathNames.Canonical(CarriedFolder));

    /// <summary>The version's file.</summary>
    public string File => Path.Combine(Folder, Version + ".md");

    /// <summary>Whether the build carries notes of this version.</summary>
    public bool Exists => System.IO.File.Exists(File);

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
