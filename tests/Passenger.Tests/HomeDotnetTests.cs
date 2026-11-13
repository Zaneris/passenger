using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An SDK installed into the home's own .dotnet folder, as the install script and Passenger put one, that neither
/// the PATH nor DOTNET_ROOT names: an agent's Passenger on a runtime apart from any dotnet, started with launchd's bare PATH,
/// loads a project whose global.json asks for it. The home is the test's own, its .dotnet a link to the tests' .NET; a
/// .NET the system's installers put elsewhere, lacking that SDK, is passed over. Its Passengers are processes of their
/// own, so it runs beside the window tests (<see cref="Beside"/>).</summary>
[Collection(Beside.Name)]
public sealed class HomeDotnetTests(LinkedDotnetTests.Linked linked) : McpRig, IClassFixture<LinkedDotnetTests.Linked>
{
    [Fact]
    public async Task An_agents_Passenger_loads_a_project_whose_global_json_asks_for_the_SDK_only_the_homes_dotnet_folder_holds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' own place for the install script's SDK, %LOCALAPPDATA%\\Microsoft\\dotnet, follows no variable a test can set");
        string installation = Dotnet.Root();
        string sdk = DotnetInstallation.Read(installation).NewestSdk ?? throw new InvalidOperationException($"The tests' .NET in {installation} has no SDK");
        string home = PathNames.Canonical(TestFolders.Create("home-dotnet-"));
        DeleteAtEnd(home);
        string link = Path.Combine(home, ".dotnet");
        File.CreateSymbolicLink(link, installation);
        try
        {
            await LoadsAsync(home, sdk);
        }
        finally
        {
            // The link alone, before its folder goes: nothing deleting that folder may reach the tests' .NET through it.
            File.Delete(link);
        }
    }

    private async Task LoadsAsync(string home, string sdk)
    {
        string folder = PathNames.Canonical(TestFolders.Create("home-sdk-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{sdk}}", "rollForward": "disable" } }""");
        // The SDK's own framework, whose reference assemblies its packs hold: no restore downloads them.
        string framework = $"net{sdk.Split('.')[0]}.0";
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), TestProjects.Library().Replace($"<TargetFramework>{TestProjects.Framework}<", $"<TargetFramework>{framework}<", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "Greeter.cs"), "namespace Lib;\n\npublic class Greeter\n{\n}\n");
        Dictionary<string, string?> environment = new(linked.Environment)
        {
            ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
            ["HOME"] = home,
        };
        await using McpClient client = await Start(["--mcp", folder], environment: environment);

        JsonElement workspace = await Call(client, McpTools.Workspace);
        JsonElement project = Assert.Single(workspace.GetProperty("projects").EnumerateArray());
        Assert.True(!workspace.TryGetProperty("sdkMissing", out _) && project.GetProperty("targetFrameworks").EnumerateArray().Select(f => f.GetString()).SequenceEqual([framework]),
            $"With the SDK {sdk} that global.json asks for in {home}/.dotnet alone, workspace answered: {workspace}");
        JsonElement found = Assert.Single((await Call(client, McpTools.FindSymbol, new() { ["query"] = "Greeter" })).EnumerateArray());
        Assert.True(found.GetProperty("path").GetString() == "Greeter.cs",
            $"With the SDK {sdk} in {home}/.dotnet alone, the C# language server did not load the project's Greeter: {found}");
    }
}
