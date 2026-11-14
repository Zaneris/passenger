using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>What workspace says of a folder's projects through an agent's own Passenger (<c>--mcp</c>): an Android app, which
/// .NET for Android evaluates as a Library, is the Exe it runs as, and a library stays a Library.</summary>
[Collection(Beside.Name)]
public sealed class McpWorkspaceTests : McpRig
{
    [Fact]
    public async Task Workspace_calls_an_android_app_an_exe_as_it_runs_where_its_evaluation_says_library()
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-android-app-"));
        DeleteAtEnd(folder);
        // An app's evaluation as .NET for Android leaves it, a .NET MAUI app's for its Android framework among them.
        TestFolders.Write(folder, "Phone/Phone.csproj", TestProjects.Library("<AndroidApplication>true</AndroidApplication>"));
        TestFolders.Write(folder, "Phone/MainActivity.cs", "class MainActivity\n{\n}\n");
        TestFolders.Write(folder, "Parts/Parts.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Parts/Part.cs", "class Part\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Expected = "Parts: Library, Phone: Exe; runs Phone";
        string said = string.Empty;
        await Waiting.UntilAsync(async () => (said = Said(await Call(client, McpTools.Workspace))) == Expected, TimeSpan.FromSeconds(60), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(said == Expected, $"workspace did not call the Android app an Exe, the run configuration it lists, and the library a Library: {said}");
    }

    /// <summary>Each project's output type by name, then the run configurations.</summary>
    private static string Said(JsonElement workspace) =>
        string.Join(", ", workspace.GetProperty("projects").EnumerateArray()
            .Select(p => $"{p.GetProperty("name").GetString()}: {(p.TryGetProperty("outputType", out JsonElement type) ? type.GetString() : "none")}").Order(StringComparer.Ordinal))
        + "; runs " + string.Join(", ", workspace.GetProperty("runConfigurations").EnumerateArray().Select(c => c.GetString()));
}
