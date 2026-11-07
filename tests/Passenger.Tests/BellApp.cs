using System.Text.Json.Nodes;
using Passenger.App;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Blazor Web App as <c>dotnet new blazor</c> makes it (Interactive Server, no HTTPS), named Bell, made for a test:
/// listening on a port of the system's choosing, which it says (<see cref="HeardPorts"/>), with no browser opened, with an
/// update handler of its own that writes
/// <see cref="HandlersRan"/> each time the app's update handlers run. It needs no package, so it restores offline.</summary>
internal static class BellApp
{
    /// <summary>What the app's own update handler writes each time the handlers run.</summary>
    public const string HandlersRan = "the update handlers ran";

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    /// <summary>The app made in <paramref name="folder"/> for <paramref name="framework"/> (the run's by default) and
    /// restored, on the SDK a global.json there names when <paramref name="sdk"/> is given.</summary>
    public static void Make(string folder, string? framework = null, string? sdk = null)
    {
        Directory.CreateDirectory(folder);
        if (sdk is not null)
        {
            TestFolders.Write(folder, "global.json", $"{{ \"sdk\": {{ \"version\": \"{sdk}\", \"rollForward\": \"disable\" }} }}\n");
        }
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", "blazor", "--output", folder, "--name", "Bell", "--framework", framework ?? TestProjects.Framework, "--interactivity", "Server", "--no-https",
                    "--no-restore"], folder), limit: Limit);
            Assert.True(made.Succeeded, $"dotnet new blazor did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), "dotnet new blazor to make the test's app", Limit + TestFolders.Spare);
        string settings = Path.Combine(folder, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        launch["profiles"]!["http"]!["applicationUrl"] = "http://127.0.0.1:0";
        launch["profiles"]!["http"]!["launchBrowser"] = false;
        File.WriteAllText(settings, launch.ToJsonString());
        TestFolders.Write(folder, "UpdateProbe.cs", "[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(UpdateProbe))]\n\n"
            + "internal static class UpdateProbe\n{\n    public static void UpdateApplication(Type[]? types) => Console.WriteLine(\"" + HandlersRan + "\");\n}\n");
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        Restore.Run(Path.Combine(folder, "Bell.csproj"));
    }

    /// <summary>A page of the app (the counter's by default), at the port its output (<paramref name="output"/>) last says it
    /// listens on.</summary>
    public static string Page(MainWindow window, Func<string> output, string path = "/counter")
    {
        int port = HeardPorts.Of(output, $"the app ({window.Status})");
        using HttpClient http = new();
        return Ui.Await(http.GetStringAsync(new Uri($"http://127.0.0.1:{port}{path}")), $"{path} from the app", TimeSpan.FromMinutes(1));
    }

    /// <summary>An edit to the file open in the editor, as typed.</summary>
    public static void Edit(MainWindow window, string was, string now)
    {
        TextSnapshot text = window.EditorArea.CodeEditor.Document.Snapshot;
        int at = text.ToString().IndexOf(was, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{Path.GetFileName(window.EditorArea.Active?.Path)} has no '{was}'");
        window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, was.Length, now)]);
    }

    /// <summary>How many times <paramref name="what"/> is in <paramref name="text"/>.</summary>
    public static int Count(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
