using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;
using Passenger.Core;
using Passenger.Terminal;

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

    /// <summary>A folder whose global.json asks for the SDK only the home's .dotnet holds, while DOTNET_ROOT names another
    /// installation found first: its terminal's login shell (zsh or bash), whose startup files may put the system's .NET
    /// first (macOS's path_helper), runs the home's dotnet, at the version asked, and names it as DOTNET_ROOT.</summary>
    [Theory]
    [InlineData("zsh")]
    [InlineData("bash")]
    public async Task A_folders_terminal_runs_the_dotnet_its_global_json_asks_for_where_another_installation_is_found_first(string shell)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' own place for the install script's SDK, %LOCALAPPDATA%\\Microsoft\\dotnet, follows no variable a test can set");
        Assert.SkipUnless(File.Exists($"/bin/{shell}"), $"/bin/{shell} is not installed here");
        string installation = Dotnet.Root();
        string sdk = DotnetInstallation.Read(installation).NewestSdk ?? throw new InvalidOperationException($"The tests' .NET in {installation} has no SDK");
        (string home, string other) = Homes();
        string folder = PathNames.Canonical(TestFolders.Create("home-terminal-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{sdk}}", "rollForward": "disable" } }""");
        try
        {
            await using McpClient client = await Start(["--mcp", folder], environment: Apart(home, other, $"/bin/{shell}"), languageServer: false);
            string answer = Text(await Call(client, McpTools.TerminalRun, new() { ["command"] = "echo \"root=$DOTNET_ROOT\"; dotnet --version", ["waitSeconds"] = 60 }));
            Assert.True(answer.Contains($"root={installation}", StringComparison.Ordinal) && answer.Split('\n').Any(line => line.Trim() == sdk),
                $"In a folder whose global.json asks for the SDK {sdk}, which only {home}/.dotnet holds, the terminal's {shell} ran another dotnet, DOTNET_ROOT naming {other} first:\n{answer}");
        }
        finally
        {
            File.Delete(Path.Combine(home, ".dotnet"));
        }
    }

    /// <summary>A folder the installation found first serves (its global.json asks for that one's SDK) keeps its terminal's
    /// environment as Passenger was given it: a shell reading no startup file has the PATH and DOTNET_ROOT given, and
    /// nothing of Passenger's.</summary>
    [Fact]
    public async Task A_folder_the_first_installation_serves_keeps_its_terminals_path_and_dotnet_root_as_given()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' own place for the install script's SDK, %LOCALAPPDATA%\\Microsoft\\dotnet, follows no variable a test can set");
        (string home, string other) = Homes();
        string folder = PathNames.Canonical(TestFolders.Create("home-terminal-"));
        DeleteAtEnd(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), """{ "sdk": { "version": "1.0.100", "rollForward": "disable" } }""");
        try
        {
            Dictionary<string, string?> environment = Apart(home, other, "/bin/sh");
            await using McpClient client = await Start(["--mcp", folder], environment: environment, languageServer: false);
            await Call(client, McpTools.SettingsSet, new() { ["changes"] = new Dictionary<string, object> { ["terminalShell"] = "/bin/sh" } });
            string answer = Text(await Call(client, McpTools.TerminalRun, new() { ["command"] = $"echo \"path=$PATH root=$DOTNET_ROOT\"; env | grep -c -e '^{FirstOnPath.FolderVariable}=' -e '^{FirstOnPath.AgainVariable}=' -e '^{FirstOnPath.ZdotdirVariable}='", ["waitSeconds"] = 30 }));
            string[] lines = answer.Split('\n', StringSplitOptions.TrimEntries);
            Assert.True(lines.Contains($"path={environment["PATH"]} root={other}") && lines.Contains("0"),
                $"With no global.json, which the installation {other} found first serves, the terminal's environment was changed:\n{answer}");
        }
        finally
        {
            File.Delete(Path.Combine(home, ".dotnet"));
        }
    }

    /// <summary>A folder whose global.json's sdk paths name an installation of its own beside its projects, as a build
    /// script's install puts one, while every installation found lacks the SDK it asks for: an agent's Passenger loads its
    /// project with that SDK and builds it there.</summary>
    [Fact]
    public async Task A_folder_whose_global_json_paths_name_an_installation_of_its_own_loads_and_builds_with_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows lets a user make no link without Developer Mode");
        string installation = Dotnet.Root();
        string sdk = DotnetInstallation.Read(installation).NewestSdk ?? throw new InvalidOperationException($"The tests' .NET in {installation} has no SDK");
        (string home, string other) = Homes(linked: false);
        string folder = PathNames.Canonical(TestFolders.Create("paths-sdk-"));
        DeleteAtEnd(folder);
        string own = Directory.CreateDirectory(Path.Combine(folder, ".dotnet")).FullName;
        // The installation's own dotnet, copied so the folder is its root; the rest linked, its sdk folder holding the SDK alone.
        foreach (string entry in Directory.EnumerateFileSystemEntries(installation))
        {
            string name = Path.GetFileName(entry);
            if (name == Dotnet.HostName)
            {
                File.Copy(entry, Path.Combine(own, name));
            }
            else if (name != "sdk")
            {
                File.CreateSymbolicLink(Path.Combine(own, name), entry);
            }
        }
        Directory.CreateSymbolicLink(Path.Combine(Directory.CreateDirectory(Path.Combine(own, "sdk")).FullName, sdk), Path.Combine(installation, "sdk", sdk));
        File.WriteAllText(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{sdk}}", "rollForward": "disable", "paths": [ ".dotnet" ] } }""");
        string framework = $"net{sdk.Split('.')[0]}.0";
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), TestProjects.Library().Replace($"<TargetFramework>{TestProjects.Framework}<", $"<TargetFramework>{framework}<", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "Greeter.cs"), "namespace Lib;\n\npublic class Greeter\n{\n}\n");
        await using McpClient client = await Start(["--mcp", folder], environment: Apart(home, other, "/bin/sh"), languageServer: false);

        JsonElement workspace = await Call(client, McpTools.Workspace);
        JsonElement project = Assert.Single(workspace.GetProperty("projects").EnumerateArray());
        Assert.True(!workspace.TryGetProperty("sdkMissing", out _) && project.GetProperty("targetFrameworks").EnumerateArray().Select(f => f.GetString()).SequenceEqual([framework]),
            $"With the SDK {sdk} that global.json asks for in the .dotnet its paths name alone, workspace answered: {workspace}");
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean() && File.Exists(Path.Combine(folder, "bin", "Debug", framework, "Lib.dll")),
            $"With the SDK {sdk} in the .dotnet global.json's paths name alone, the project did not build there: {built}");
    }

    /// <summary>A folder whose global.json's sdk paths name no folder holding the SDK it asks for, and whose errorMessage
    /// says what to do: workspace says that message, as dotnet does.</summary>
    [Fact]
    public async Task A_folder_whose_global_json_paths_hold_no_sdk_it_takes_says_its_error_message()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' own place for the install script's SDK, %LOCALAPPDATA%\\Microsoft\\dotnet, follows no variable a test can set");
        (string home, string other) = Homes(linked: false);
        string folder = PathNames.Canonical(TestFolders.Create("paths-sdk-"));
        DeleteAtEnd(folder);
        const string Said = "Run ./restore.sh to install the SDK this repository builds with.";
        File.WriteAllText(Path.Combine(folder, "global.json"), $$"""{ "sdk": { "version": "{{TestProjects.Framework["net".Length..]}}.100", "paths": [ ".dotnet" ], "errorMessage": "{{Said}}" } }""");
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), TestProjects.Library());
        await using McpClient client = await Start(["--mcp", folder], environment: Apart(home, other, "/bin/sh"), languageServer: false);
        string workspace = Text(await Call(client, McpTools.Workspace));
        Assert.True(workspace.Contains(Said, StringComparison.Ordinal), $"With no SDK where global.json's paths look, workspace did not say its errorMessage: {workspace}");
    }

    /// <summary>A home of the test's own, its .dotnet a link to the tests' .NET where <paramref name="linked"/>, and another
    /// installation, holding an SDK no global.json asks for, whose dotnet names that SDK and nothing else.</summary>
    private (string Home, string Other) Homes(bool linked = true)
    {
        string home = PathNames.Canonical(TestFolders.Create("home-dotnet-"));
        DeleteAtEnd(home);
        if (linked)
        {
            File.CreateSymbolicLink(Path.Combine(home, ".dotnet"), Dotnet.Root());
        }
        string other = Path.Combine(home, "other-dotnet");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(other, "sdk", "1.0.100")).FullName, "dotnet.dll"), string.Empty);
        string dotnet = Path.Combine(other, "dotnet");
        File.WriteAllText(dotnet, "#!/bin/sh\necho 1.0.100\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (home, other);
    }

    /// <summary>launchd's bare PATH, the home given, the shell, and DOTNET_ROOT naming the other installation, found first.</summary>
    private Dictionary<string, string?> Apart(string home, string other, string shell) => new(linked.Environment)
    {
        ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin",
        ["HOME"] = home,
        ["SHELL"] = shell,
        [RuntimeVariables.Root] = other,
    };

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
