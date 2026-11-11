using Passenger.App.MSBuild;
using Passenger.Core;
using Passenger.Host;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Packages;

/// <summary>What a package change writes itself, as dotnet add package would but each version where the project's
/// evaluation keeps it: under central package management the PackageVersion in force, in whichever file the project
/// imports it from, else a new one in the project's Directory.Packages.props, or a VersionOverride in the project; and a
/// version written as one property alone has that property set where the evaluation last set it, so the packages sharing
/// it move together. Every other character of each file is as it was. The files' texts by path, as read and as moved, or
/// why the change cannot be written.</summary>
internal sealed record CentralWrites(IReadOnlyDictionary<string, (FileText Had, string Text)> Texts, string? Refused)
{
    public static CentralWrites None { get; } = new(new Dictionary<string, (FileText, string)>(), null);

    /// <summary>The packages whose versions the writes move, those sharing a property set among them; null where who else
    /// shares a property set is not known (one a project's own references name, which other projects may name too).</summary>
    public IReadOnlyCollection<string>? Moved { get; init; } = [];

    /// <summary>The packages left to dotnet add package: references whose versions are written as versions.</summary>
    public IReadOnlyList<(string Id, string Version)> Left { get; init; } = [];

    public static CentralWrites Refusing(string why) => None with { Refused = why };

    /// <summary>The packages moved in a project under central package management as its evaluation (<paramref name="insight"/>)
    /// has them, read off the window's thread; <paramref name="alongside"/>, which the project need not reference, move
    /// only where a central version of theirs moves the way the packages' own do (up, unless they all go down).</summary>
    public static CentralWrites Of(MSBuildInsight insight, string project, string props, IReadOnlyList<(string Id, string Version)> packages,
        IReadOnlyList<(string Id, string Version)> alongside)
    {
        Writer writer = new(insight);
        IReadOnlyDictionary<string, (string Version, MSBuildPlace Place)> central = insight.CentralVersions();
        PackageMove[] own = [.. packages.Select(p => central.TryGetValue(p.Id, out (string Version, MSBuildPlace) held) ? Move(p.Version, held.Version) : PackageMove.None)
            .Where(m => m != PackageMove.None)];
        PackageMove way = own.Length > 0 && own.All(m => m == PackageMove.Downgrade) ? PackageMove.Downgrade : PackageMove.Update;
        try
        {
            foreach ((string id, string version) in packages)
            {
                (string text, bool overridden) = ProjectEdits.WithCentralPackage(writer.Text(project), id, version);
                writer.Set(project, text);
                if (!overridden && Central(id, version) is { } why)
                {
                    return Refusing(why);
                }
            }
            foreach ((string id, string version) in alongside)
            {
                if (central.TryGetValue(id, out (string Version, MSBuildPlace) held) && Move(version, held.Version) == way
                    && Central(id, InstalledVersion.Written(version, [new InstalledVersion(null, held.Version)])) is { } why)
                {
                    return Refusing(why);
                }
            }
            // A property moves every package whose central version names it, whichever project references each.
            foreach ((string id, (string _, MSBuildPlace place)) in central)
            {
                if (place.Path.Length > 0 && ProjectEdits.VersionWritten(writer.Text(place.Path), "PackageVersion", id, place.Line) is { } written
                    && ProjectEdits.PropertyNamed(written) is { } property && writer.Sets(property))
                {
                    writer.Moved.Add(id);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refusing(error.Message);
        }
        return writer.Written() with { Moved = writer.Moved };

        string? Central(string id, string version)
        {
            writer.Moved.Add(id);
            if (!central.TryGetValue(id, out (string Version, MSBuildPlace Place) held) || held.Place.Path.Length == 0)
            {
                writer.Set(props, ProjectEdits.WithPackageVersion(writer.Text(props), id, version) ?? CentralPackages.Pinned(writer.Text(props), [(id, version)]));
                return null;
            }
            string file = held.Place.Path;
            string? written = ProjectEdits.VersionWritten(writer.Text(file), "PackageVersion", id, held.Place.Line);
            if (written is null)
            {
                return $"{id}'s version in {Path.GetFileName(file)} could not be read";
            }
            if (ProjectEdits.PropertyNamed(written) is { } property)
            {
                return writer.Property(id, property, version);
            }
            if (Expression(written))
            {
                return $"{id}'s version in {Path.GetFileName(file)} is written as {written}, which Passenger moves only where it is a version or one property alone";
            }
            writer.Set(file, ProjectEdits.WithPackageVersion(writer.Text(file), id, version, held.Place.Line)!);
            return null;
        }
    }

    /// <summary>The packages moved in a project without central package management, as its evaluation has them, read off
    /// the window's thread: each reference whose version is one property alone has that property set; the rest are left
    /// to dotnet add package (<see cref="Left"/>), which writes a version in place of a property.</summary>
    public static CentralWrites OfReferences(MSBuildInsight insight, IReadOnlyList<(string Id, string Version)> packages)
    {
        Writer writer = new(insight);
        List<(string Id, string Version)> left = [];
        try
        {
            foreach ((string id, string version) in packages)
            {
                InsightItem? reference = insight.Raw.Items.LastOrDefault(i => i.Type.Equals("PackageReference", StringComparison.OrdinalIgnoreCase)
                    && i.Includes.Contains(id, StringComparer.OrdinalIgnoreCase));
                MSBuildPlace place = reference is null ? default : insight.Place(reference.Element);
                if (place.Path is not { Length: > 0 } || ProjectEdits.VersionWritten(writer.Text(place.Path), "PackageReference", id, place.Line) is not { } written
                    || ProjectEdits.PropertyNamed(written) is not { } property)
                {
                    left.Add((id, version));
                    continue;
                }
                if (writer.Property(id, property, version) is { } why)
                {
                    return Refusing(why);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refusing(error.Message);
        }
        return writer.Written() with { Moved = left.Count == packages.Count ? None.Moved : null, Left = left };
    }

    /// <summary>What moving a central version held to <paramref name="version"/> is.</summary>
    private static PackageMove Move(string version, string held) => InstalledVersion.Move(version, [new InstalledVersion(null, held)]);

    private static bool Expression(string written) =>
        written.Contains("$(", StringComparison.Ordinal) || written.Contains("@(", StringComparison.Ordinal) || written.Contains("%(", StringComparison.Ordinal);

    /// <summary>The files a change reads and writes, each read once, and the properties it sets.</summary>
    private sealed class Writer(MSBuildInsight insight)
    {
        private readonly Dictionary<string, (FileText Had, string Text)> texts = new(PathNames.Comparer);
        private readonly Dictionary<string, (string Id, string Version)> properties = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Moved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Text(string path)
        {
            if (!texts.TryGetValue(path, out (FileText Had, string Text) held))
            {
                FileText had = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
                texts[path] = held = (had, had.Text);
            }
            return held.Text;
        }

        public void Set(string path, string text) => texts[path] = (texts[path].Had, text);

        public bool Sets(string property) => properties.ContainsKey(property);

        /// <summary>A property set to a version where the evaluation last set it, once for all the packages sharing it,
        /// which a second asking another version would split: null once set, else why not.</summary>
        public string? Property(string id, string name, string version)
        {
            if (properties.TryGetValue(name, out (string Id, string Version) moved))
            {
                return moved.Version == version ? null : $"{moved.Id} and {id} share $({name}), but would move to {moved.Version} and {version}";
            }
            if (insight.Property(name) is not { Origin: PropertyOrigin.Set, Set: [InsightPlace at, ..] } || insight.Place(at) is not { Path.Length: > 0 } place)
            {
                return $"{id}'s version is $({name}), which no file of the project's sets";
            }
            (string? text, string? why) = ProjectEdits.WithPropertyAt(Text(place.Path), name, place.Line, version);
            if (text is null)
            {
                return $"{id}'s version is $({name}), which {Path.GetFileName(place.Path)} sets at line {place.Line}, but {why}: move it there";
            }
            Set(place.Path, text);
            properties[name] = (id, version);
            return null;
        }

        public CentralWrites Written() => new(texts.Where(t => t.Value.Text != t.Value.Had.Text).ToDictionary(t => t.Key, t => t.Value, PathNames.Comparer), null);
    }
}
