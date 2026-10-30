using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Run;

/// <summary>Whether a project's inputs changed since a build began, as Rerun of an Aspire service reads it: a source file
/// gone, or come keeping an older time, is a change as one written since is, in the project or one it references, while
/// what a running program writes beside its project is not.</summary>
public sealed class BuiltInputsTests : IDisposable
{
    private static readonly DateTime Before = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_source_file_deleted_since_the_build_began_is_a_change()
    {
        string project = Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Api/Program.cs", "Greeter.Hello();\n");
        string greeter = Write("Api/Greeter.cs", "static class Greeter { public static void Hello() { } }\n");
        IReadOnlyList<ProjectModel> projects = [Model(project)];
        BuiltInputs.Listing listing = BuiltInputs.Listed(project, projects);
        Assert.False(BuiltInputs.ChangedSince(project, listing, projects), "The project's inputs changed though nothing was written, came or went");

        File.Delete(greeter);
        Assert.True(BuiltInputs.ChangedSince(project, listing, projects),
            "A source file deleted since the build began was no change, so Rerun would leave the old build running with the deleted code in it");
    }

    [Fact]
    public void A_source_file_moved_in_keeping_an_older_time_is_a_change()
    {
        string project = Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Api/Program.cs", "System.Console.WriteLine();\n");
        string elsewhere = Write("Elsewhere/Moved.cs", "static class Moved { }\n");
        IReadOnlyList<ProjectModel> projects = [Model(project)];
        BuiltInputs.Listing listing = BuiltInputs.Listed(project, projects);

        File.Move(elsewhere, Path.Join(root, "Api", "Moved.cs"));
        Assert.True(BuiltInputs.ChangedSince(project, listing, projects),
            "A source file moved in since the build began, keeping its older time, was no change, so Rerun would run a build without it");
    }

    [Fact]
    public void A_file_gone_from_a_referenced_project_is_a_change_of_the_project_referencing_it_alone_where_references_count()
    {
        string library = Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        string shared = Write("Lib/Shared.cs", "public static class Shared { }\n");
        string project = Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Api/Program.cs", "System.Console.WriteLine();\n");
        IReadOnlyList<ProjectModel> projects = [Model(project, library), Model(library)];
        BuiltInputs.Listing listing = BuiltInputs.Listed(project, projects);

        File.Delete(shared);
        Assert.True(BuiltInputs.ChangedSince(project, listing, projects),
            "A file deleted from a project the service references was no change, so Rerun would keep the service's old build");
        Assert.False(BuiltInputs.ChangedSince(project, listing, projects, references: false),
            "A file deleted from a referenced project was a change of the project's own files, which an AppHost's own check reads alone");
    }

    [Fact]
    public void What_a_running_program_writes_or_deletes_beside_its_project_is_no_change()
    {
        string project = Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("Api/Program.cs", "System.Console.WriteLine();\n");
        string database = Write("Api/app.db", "data\n");
        IReadOnlyList<ProjectModel> projects = [Model(project)];
        BuiltInputs.Listing listing = BuiltInputs.Listed(project, projects);

        File.Delete(database);
        Write("Api/api.dcp.json", "{}\n");
        Write("Api/bin/Debug/Api.dll", "built\n");
        Assert.False(BuiltInputs.ChangedSince(project, listing, projects),
            "A database gone, DCP's file come or a build's output was taken for a change of the project's code, so Rerun would restart a service that did not change");
    }

    /// <summary>A file under the test's folder, written with a time well before any listing, by its canonical path, as the
    /// IDE's project models name a project.</summary>
    private string Write(string relative, string text)
    {
        string path = Path.Join(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, Before);
        return PathNames.Canonical(path);
    }

    private static ProjectModel Model(string project, params string[] references) =>
        new(project, Path.GetFileNameWithoutExtension(project), "Exe", ["net11.0"], [], references, Exists: true);
}
