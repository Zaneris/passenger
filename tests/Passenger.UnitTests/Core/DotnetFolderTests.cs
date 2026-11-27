using System.Diagnostics;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Where a dotnet run for a folder works: an SDK's command for a folder not trusted (every folder, in this process),
/// or one under a global.json in a folder not trusted, works outside it, in the person's home, so that global.json never
/// names the SDK that runs; a program's own run (by its path, or after exec) works in the folder, which no global.json
/// chooses anything for. And what a tool's start is told of telemetry: an SDK's command, a helper running the person's
/// MSBuild and Godot's editor send none the person has not asked for, where a program's own run is left with the person's
/// environment; and of its temporary folder, Passenger's own for an SDK's command that builds nothing.</summary>
public sealed class DotnetFolderTests
{
    private static readonly string Host = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "dotnet-folder-tests", "dotnet"));

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string Repository = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "dotnet-folder-tests", "repo"));

    [Fact]
    public void An_SDKs_command_works_inside_a_folder_only_once_it_is_trusted_and_sends_no_telemetry_unasked() => Cases.All(
        ("an sdks command for a folder not trusted works in the persons home", An_sdks_command_for_a_folder_not_trusted_works_in_the_persons_home),
        ("a programs own run works in its folder and an sdks command for a folder holding home on the disks root", A_programs_own_run_works_in_its_folder_and_an_sdks_command_for_a_folder_holding_home_on_the_disks_root),
        ("a trusted folder takes only a global json in a folder trusted too", A_trusted_folder_takes_only_a_global_json_in_a_folder_trusted_too),
        ("a tools start sends no telemetry the person has not asked for and a programs own run is left as the person has it",
            A_tools_start_sends_no_telemetry_the_person_has_not_asked_for_and_a_programs_own_run_is_left_as_the_person_has_it),
        ("an sdks command building nothing of the persons takes passengers own temporary folder and a build or a programs run the persons",
            An_sdks_command_building_nothing_of_the_persons_takes_passengers_own_temporary_folder_and_a_build_or_a_programs_run_the_persons));

    private void An_sdks_command_for_a_folder_not_trusted_works_in_the_persons_home()
    {
        foreach (string[] arguments in (string[][])[["--version"], ["new", "list"], ["msbuild", "App.csproj"], ["workload", "install", "android"], []])
        {
            Assert.True(ChildProcesses.DotnetCommand(Host, arguments, Repository).WorkingDirectory == Home,
                $"dotnet {string.Join(' ', arguments)} for a folder not trusted works in {ChildProcesses.DotnetCommand(Host, arguments, Repository).WorkingDirectory}, where its global.json names the SDK");
        }
        Assert.Equal(Home, ChildProcesses.DotnetQuestion(Host, ["--version"], Repository).WorkingDirectory);
    }

    private void A_programs_own_run_works_in_its_folder_and_an_sdks_command_for_a_folder_holding_home_on_the_disks_root()
    {
        Assert.Equal(Repository, ChildProcesses.DotnetCommand(Host, ["exec", "--runtimeconfig", "App.runtimeconfig.json", "App.dll"], Repository).WorkingDirectory);
        Assert.Equal(Repository, ChildProcesses.DotnetCommand(Host, [Path.Combine(Repository, "Tool.dll"), "-Plugin"], Repository).WorkingDirectory);
        string above = Path.GetDirectoryName(Home)!;
        Assert.Equal(Path.GetPathRoot(above), ChildProcesses.DotnetCommand(Host, ["--version"], above).WorkingDirectory);
        Assert.Equal(string.Empty, ChildProcesses.DotnetCommand(Host, ["--version"], null).WorkingDirectory);
    }

    /// <summary>Each telemetry switch an SDK's command, a question of it, a helper running the person's MSBuild and Godot's
    /// editor, an export's too, start with is the person's own where this process's environment sets it, else on; a question's
    /// the .NET SDK's on whatever is set, so it ends with its answer. A program's own run, a Godot game's too, starts with each
    /// as the person has it, and a value the person gave a tool, or its inherited environment holds, stands.</summary>
    private void A_tools_start_sends_no_telemetry_the_person_has_not_asked_for_and_a_programs_own_run_is_left_as_the_person_has_it()
    {
        ProcessStartInfo question = ChildProcesses.DotnetQuestion(Host, ["--version"], Repository);
        foreach ((string tool, ProcessStartInfo start) in (ReadOnlySpan<(string, ProcessStartInfo)>)[
            ("dotnet build", ChildProcesses.DotnetCommand(Host, ["build", "App.csproj"], Repository)),
            ("dotnet watch", ChildProcesses.DotnetCommand(Host, ["watch", "run", "--project", "App.csproj"], null)),
            ("dotnet --version, asked", question),
            ("a helper running the persons MSBuild", ChildProcesses.NamingDotnet(new ProcessStartInfo(Host), Repository)),
            ("Godot's editor", Godot(["--path", Repository, "--editor"])),
            ("Godot's export", Godot(["--headless", "--path", Repository, "--export-release", "Here", "Game.zip"]))])
        {
            foreach (string variable in TelemetryOptOuts.All)
            {
                string? expected = start == question && variable == TelemetryOptOuts.DotnetCli ? TelemetryOptOuts.On : Environment.GetEnvironmentVariable(variable) ?? TelemetryOptOuts.On;
                Assert.True(start.Environment.TryGetValue(variable, out string? given) && given == expected,
                    $"{tool} started with {variable} {given ?? "unset"}, where {expected} keeps it from sending what the person never asked to send, or keeps the person's own");
            }
        }
        foreach (ProcessStartInfo own in (ReadOnlySpan<ProcessStartInfo>)[
            ChildProcesses.DotnetCommand(Host, ["exec", "--runtimeconfig", "App.runtimeconfig.json", "App.dll"], Repository), Godot(["--path", Repository, "res://main.tscn"])])
        {
            foreach (string variable in TelemetryOptOuts.All)
            {
                string? given = own.Environment.TryGetValue(variable, out string? set) ? set : null;
                Assert.True(given == Environment.GetEnvironmentVariable(variable), $"A program's own run ({own.FileName}) started with {variable} {given}, which the person's environment does not set");
            }
        }

        ProcessStartInfo kept = new(Host);
        kept.Environment[TelemetryOptOuts.Avalonia] = "0";
        kept.Environment[TelemetryOptOuts.TestingPlatform] = string.Empty;
        ChildProcesses.SendingNoTelemetry(kept);
        Assert.True(kept.Environment[TelemetryOptOuts.Avalonia] == "0" && kept.Environment[TelemetryOptOuts.TestingPlatform] == string.Empty,
            "A tool's telemetry switch the person set was overridden, so a person who chose to send Avalonia's or the Testing Platform's telemetry no longer does");
        Dictionary<string, string> over = ChildProcesses.SendingNoTelemetry(new Dictionary<string, string> { [TelemetryOptOuts.Avalonia] = "0" },
            variable => variable == TelemetryOptOuts.DotnetCli ? "true" : null);
        Assert.True(over[TelemetryOptOuts.Avalonia] == "0" && !over.ContainsKey(TelemetryOptOuts.DotnetCli),
            "Variables a tool is started with over its inherited environment lost the person's own value, or gave one over what the environment it inherits sets");
        Assert.True(TelemetryOptOuts.All.Where(v => v is not TelemetryOptOuts.Avalonia and not TelemetryOptOuts.DotnetCli).All(v => over.GetValueOrDefault(v) == TelemetryOptOuts.On),
            $"Variables a tool is started with over its inherited environment lack a telemetry switch neither sets: {string.Join(", ", over.Select(v => $"{v.Key}={v.Value}"))}");

        static ProcessStartInfo Godot(string[] arguments) =>
            ChildProcesses.Command(Path.Combine(Path.GetDirectoryName(Host)!, "godot"), arguments, null, GodotExecutables.EnvironmentOf(arguments, new Dictionary<string, string>()));
    }

    /// <summary>An SDK's command that builds nothing of the person's (a template, a workload, a solution, a package reference)
    /// works in Passenger's own temporary folder, which takes what the .NET CLI leaves there, NuGet's scratch staying the one
    /// every restore shares; a build, a restore, a publish or a question of MSBuild keeps the person's, where its compiler
    /// server meets theirs, and so does a program's run (dotnet run, watch, test, exec, a tool), where a debugger and the
    /// diagnostics tools look for its runtime's socket and pipes.</summary>
    private void An_sdks_command_building_nothing_of_the_persons_takes_passengers_own_temporary_folder_and_a_build_or_a_programs_run_the_persons()
    {
        foreach (string[] arguments in (string[][])[["new", "console"], ["workload", "install", "android"], ["sln", "add", "App.csproj"], ["add", "package", "Humanizer"], ["package", "list"], ["--info"]])
        {
            Assert.True(SdkTemporary.Takes(arguments), $"dotnet {string.Join(' ', arguments)} builds nothing of the person's but keeps the person's temporary folder, where it leaves a folder as it ends");
        }
        foreach (string[] arguments in (string[][])[["build", "App.csproj"], ["restore"], ["publish"], ["pack"], ["clean"], ["msbuild", "App.csproj", "-getProperty:TargetPath"], ["format"]])
        {
            Assert.False(SdkTemporary.Takes(arguments), $"dotnet {string.Join(' ', arguments)} runs MSBuild over the person's projects but takes Passenger's own temporary folder, where on macOS its compiler server would take over the person's builds' pipe");
        }
        foreach (string[] arguments in (string[][])[["run", "--project", "App.csproj"], ["watch", "run"], ["test"], ["exec", "App.dll"], [Path.Combine(Repository, "Tool.dll")], ["tool", "run", "dotnet-ef"], ["ef", "database", "update"], []])
        {
            Assert.False(SdkTemporary.Takes(arguments), $"dotnet {string.Join(' ', arguments)} runs a program of the person's, whose runtime a debugger or the diagnostics tools look for in the person's temporary folder, but takes Passenger's own");
        }
        string own = Path.Combine(Path.DirectorySeparatorChar + "dotnet-folder-tests", "own");
        ProcessStartInfo given = SdkTemporary.Give(new ProcessStartInfo(Host), own);
        foreach (string variable in OperatingSystem.IsWindows() ? (string[])["TMP", "TEMP"] : ["TMPDIR"])
        {
            Assert.True(given.Environment.TryGetValue(variable, out string? named) && named == own, $"An SDK's command given Passenger's own temporary folder has {variable} {named ?? "unset"}, not {own}");
        }
        Assert.True(given.Environment.TryGetValue(NuGetScratch.Variable, out string? scratch) && scratch == NuGetScratch.Shared(),
            $"An SDK's command given Passenger's own temporary folder has NuGet's scratch {scratch ?? "unset"}, where its restore would take extraction locks no other restore sees");
    }

    /// <summary>A trusted folder takes what the host reads there only where the nearest global.json over it, whose sdk.paths
    /// name the SDK that runs, is in a folder trusted too: one in a folder above it not trusted sends its dotnet elsewhere,
    /// one in the folder itself does not.</summary>
    private void A_trusted_folder_takes_only_a_global_json_in_a_folder_trusted_too()
    {
        string repository = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string app = Directory.CreateDirectory(Path.Join(repository, "src", "App")).FullName;
            bool Trusts(string folder) => PathNames.Within(folder, app);
            Assert.True(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder with no global.json over it was found to take what a folder not trusted chose");
            Assert.False(Dotnet.TakesOnlyTrusted(repository, Trusts), "A folder not trusted was found to take only what trusted folders chose");

            File.WriteAllText(Path.Join(repository, "global.json"), """{ "sdk": { "paths": [ ".sdk" ] } }""");
            Assert.False(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder takes the SDK a global.json in the folder above it, not trusted, names");
            Assert.True(Dotnet.TakesOnlyTrusted(app, folder => PathNames.Within(folder, repository)), "A folder whose global.json's folder is trusted was found not to take it");

            File.WriteAllText(Path.Join(app, "global.json"), """{ "sdk": { "rollForward": "latestMajor" } }""");
            Assert.True(Dotnet.TakesOnlyTrusted(app, Trusts), "A trusted folder holding its own global.json was found to take the one above it");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }
}
