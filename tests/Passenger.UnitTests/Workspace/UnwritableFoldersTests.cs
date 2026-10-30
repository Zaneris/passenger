using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A load's or restore's error blames a folder that cannot be written only where its words say a write was
/// refused, and only a folder it names or the project's own: a missing local source under a folder the system owns is
/// said in its own words, never as that folder being read-only. Over a file system given as its folders, each writable or
/// not; the runtime quotes such paths from / on macOS and Linux alone, where alone the IDE asks.</summary>
public sealed class UnwritableFoldersTests
{
    private const string Project = "/work/Shop/App/App.csproj";

    private const string Open = "/work/Shop";

    [Fact]
    public void A_missing_local_source_under_a_folder_the_system_owns_blames_no_folder()
    {
        OnUnix();
        Dictionary<string, bool> folders = Folders(("/", false), ("/Users", false), ("/opt", false), (Open, true), ("/work/Shop/App", true));
        string[] errors = [
            "The local source '/Users/alice/feeds' doesn't exist.",
            "Restore failed: The local source '/opt/feeds' doesn't exist.",
            "Could not find a part of the path '/Users/alice/feeds/Foo.Bar.1.0.0.nupkg'."];
        foreach (string error in errors)
        {
            string? blamed = Blamed(error, folders);
            Assert.True(blamed is null, $"\"{error}\" is said as {blamed} being read-only, a folder the error never asked to write");
        }
    }

    [Fact]
    public void A_write_refused_under_a_read_only_project_folder_blames_that_folder()
    {
        OnUnix();
        Dictionary<string, bool> folders = Folders(("/", false), ("/work", true), (Open, true), ("/work/Shop/App", false));
        string[] errors = [
            "Could not find a part of the path '/work/Shop/App/obj/Debug/net11.0/App.GeneratedMSBuildEditorConfig.editorconfig'.",
            "Restore failed: Access to the path '/work/Shop/App/obj' is denied.",
            "Read-only file system : '/work/Shop/App/obj/project.assets.json'"];
        foreach (string error in errors)
        {
            Assert.True(Blamed(error, folders) == "/work/Shop/App", $"\"{error}\" does not say App is read-only, so nothing says why the project loads no C#");
        }
        Assert.True(Blamed("The local source '/work/Shop/App/feeds' doesn't exist.", folders) is null,
            "A missing local source inside the read-only project is said as the project being read-only, not as missing");
    }

    [Fact]
    public void A_write_refused_under_a_read_only_open_folder_blames_it_where_the_project_writes_there()
    {
        OnUnix();
        Dictionary<string, bool> folders = Folders(("/", false), ("/work", true), (Open, false), ("/work/Shop/App", false));
        const string Error = "Could not find a part of the path '/work/Shop/artifacts/obj/App/debug/App.GeneratedMSBuildEditorConfig.editorconfig'.";
        Assert.True(Blamed(Error, folders) == Open, $"An artifacts folder the read-only open folder could not hold is blamed on {Blamed(Error, folders)}, not on {Open}");
    }

    [Fact]
    public void A_write_refused_outside_the_project_blames_only_the_folder_the_error_names()
    {
        OnUnix();
        Dictionary<string, bool> folders = Folders(("/", false), ("/opt", false), ("/opt/nuget", false), ("/opt/nuget/packages", false), (Open, true), ("/work/Shop/App", true));
        Assert.Equal("/opt/nuget/packages", Blamed("Access to the path '/opt/nuget/packages/foo.bar' is denied.", folders));
        Assert.Equal("/opt/nuget/packages", Blamed("Access to the path '/opt/nuget/packages' is denied.", folders));
        string? above = Blamed("Access to the path '/opt/nuget/packages/foo.bar/1.0.0' is denied.", folders);
        Assert.True(above is null, $"A refused write two folders below {above} blames it, a folder the error does not name");
    }

    [Fact]
    public void A_folder_that_can_be_written_is_never_blamed()
    {
        OnUnix();
        Dictionary<string, bool> folders = Folders(("/", false), ("/work", true), (Open, true), ("/work/Shop/App", true));
        Assert.Null(Blamed("Could not find a part of the path '/work/Shop/App/obj/Debug/net11.0/App.GeneratedMSBuildEditorConfig.editorconfig'.", folders));
        Assert.Null(Blamed("Access to the path '/work/Shop/App/obj' is denied.", folders));
    }

    private static string? Blamed(string error, Dictionary<string, bool> folders) =>
        UnwritableFolders.Of(error, Project, Open, folders.ContainsKey, at => folders[at]);

    private static Dictionary<string, bool> Folders(params (string Path, bool Writable)[] each) => each.ToDictionary(f => f.Path, f => f.Writable);

    private static void OnUnix() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' paths are no runtime's words quoted from /, and the IDE asks of no folder there");
}
