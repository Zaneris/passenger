using Passenger.App.MSBuild;
using Passenger.Core;
using Passenger.Host;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Packages;

/// <summary>What a package change under central package management writes, as dotnet add package would but each version
/// where the project's evaluation keeps it: the PackageVersion in force, in whichever file the project imports it from,
/// the property it names when written as one alone (so the packages sharing it move together), else a new one in the
/// project's Directory.Packages.props; a VersionOverride in the project. Every other character of each file is as it was.
/// The files' texts by path, as read and as moved, or why the change cannot be written.</summary>
internal sealed record CentralWrites(IReadOnlyDictionary<string, (FileText Had, string Text)> Texts, string? Refused)
{
    public static CentralWrites None { get; } = new(new Dictionary<string, (FileText, string)>(), null);

    public static CentralWrites Refusing(string why) => None with { Refused = why };

    /// <summary>The packages moved in a project as its evaluation (<paramref name="insight"/>) has them, read off the
    /// window's thread; <paramref name="alongside"/>, which the project need not reference, move only where a central
    /// version of theirs is below the one given.</summary>
    public static CentralWrites Of(MSBuildInsight insight, string project, string props, IReadOnlyList<(string Id, string Version)> packages,
        IReadOnlyList<(string Id, string Version)> alongside)
    {
        Dictionary<string, (FileText Had, string Text)> texts = new(PathNames.Comparer);
        Dictionary<string, (string Id, string Version)> properties = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, (string Version, MSBuildPlace Place)> central = insight.CentralVersions();
        try
        {
            foreach ((string id, string version) in packages)
            {
                (string text, bool overridden) = ProjectEdits.WithCentralPackage(Text(project), id, version);
                Set(project, text);
                if (!overridden && Central(id, version) is { } why)
                {
                    return Refusing(why);
                }
            }
            foreach ((string id, string version) in alongside)
            {
                if (central.TryGetValue(id, out (string Version, MSBuildPlace) held) && InstalledVersion.Move(version, [new InstalledVersion(null, held.Version)]) == PackageMove.Update
                    && Central(id, InstalledVersion.Written(version, [new InstalledVersion(null, held.Version)])) is { } why)
                {
                    return Refusing(why);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refusing(error.Message);
        }
        return new CentralWrites(texts.Where(t => t.Value.Text != t.Value.Had.Text).ToDictionary(t => t.Key, t => t.Value, PathNames.Comparer), null);

        string Text(string path)
        {
            if (!texts.TryGetValue(path, out (FileText Had, string Text) held))
            {
                FileText had = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
                texts[path] = held = (had, had.Text);
            }
            return held.Text;
        }

        void Set(string path, string text) => texts[path] = (texts[path].Had, text);

        string? Central(string id, string version)
        {
            if (!central.TryGetValue(id, out (string Version, MSBuildPlace Place) held) || held.Place.Path.Length == 0)
            {
                Set(props, ProjectEdits.WithPackageVersion(Text(props), id, version) ?? CentralPackages.Pinned(Text(props), [(id, version)]));
                return null;
            }
            string file = held.Place.Path;
            string? written = ProjectEdits.PackageVersionWritten(Text(file), id, held.Place.Line);
            if (written is null)
            {
                return $"{id}'s version in {Path.GetFileName(file)} could not be read";
            }
            if (ProjectEdits.PropertyNamed(written) is { } property)
            {
                return Property(id, property, version);
            }
            if (written.Contains("$(", StringComparison.Ordinal) || written.Contains("@(", StringComparison.Ordinal) || written.Contains("%(", StringComparison.Ordinal))
            {
                return $"{id}'s version in {Path.GetFileName(file)} is written as {written}, which Passenger moves only where it is a version or one property alone";
            }
            Set(file, ProjectEdits.WithPackageVersion(Text(file), id, version, held.Place.Line)!);
            return null;
        }

        // Set once for all the packages sharing it, which a second asking another version would split.
        string? Property(string id, string name, string version)
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
    }
}
