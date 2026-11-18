using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Updates;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Passenger on a machine whose .NET is none of its own: with none found, by Passenger's search or on the PATH
/// its helpers are given, Roslyn's server still starts and colours a lone C# file; with only the .NET 8 SDK, a .NET 8
/// project loads, completes, builds, runs and debugs, Passenger's helpers running on the runtime Passenger runs on. It
/// changes what Passenger finds and the process's PATH and DOTNET_ROOT, so it runs alone.</summary>
[Collection(Alone.Name)]
public sealed class MachineDotnetTests : IDisposable
{
    /// <summary>A folder holding the .NET 8 SDK alone (Microsoft's archive unpacked), for the test of a machine with it only.</summary>
    [IdentifierHome]
    private const string Dotnet8Variable = "PASSENGER_TEST_DOTNET8";

    private static readonly string DotnetName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("machine-dotnet-"));
    private readonly string? path = Environment.GetEnvironmentVariable("PATH");
    private readonly string? root = Environment.GetEnvironmentVariable(RuntimeVariables.Root);

    public void Dispose()
    {
        Dotnet.HostSearch = null;
        Dotnet.PlacesStandIn = null;
        Dotnet.TranslatedStandIn = null;
        Dotnet.ForgetSdks();
        Environment.SetEnvironmentVariable("PATH", path);
        Environment.SetEnvironmentVariable(RuntimeVariables.Root, root);
        TestFolders.Delete(folder);
    }

    /// <summary>Passenger's search finding <paramref name="found"/> (none, throwing as the search does), the PATH holding no
    /// dotnet but it, and DOTNET_ROOT its folder.</summary>
    private void Find(string? found)
    {
        Dotnet.HostSearch = () => found ?? throw new FileNotFoundException("No .NET command line found: install the .NET SDK, or set DOTNET_ROOT to where it is.");
        IEnumerable<string> others = (path ?? string.Empty).Split(Path.PathSeparator).Where(at => at.Length > 0 && !File.Exists(Path.Combine(at, DotnetName)));
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, found is null ? others : others.Prepend(Path.GetDirectoryName(found)!)));
        Environment.SetEnvironmentVariable(RuntimeVariables.Root, found is null ? null : Path.GetDirectoryName(found));
    }

    [AvaloniaFact]
    public void With_no_dotnet_found_the_language_server_starts_and_colours_a_lone_cs_file()
    {
        Find(null);
        TestFolders.Write(folder, "Hello.cs", "var greeting = \"hi\";\n\n");
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile("Hello.cs");
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to start with no .NET ({window.CSharpService?.State})", TimeSpan.FromMinutes(2));
        int server = window.CSharpService!.Session!.Server.ProcessId;
        Ui.WaitFor(() => Ui.IdeLog().Contains($"(process {server}) gave an open file its colours", StringComparison.Ordinal), "the lone file's colours from the server");
        window.Close();
    }

    [AvaloniaFact]
    public void With_only_the_dotnet_8_sdk_a_net_8_console_project_loads_completes_builds_runs_and_stops_at_a_breakpoint()
    {
        string? eight = Environment.GetEnvironmentVariable(Dotnet8Variable);
        Assert.SkipWhen(eight is not { Length: > 0 } || !File.Exists(Path.Combine(eight, DotnetName)),
            $"{Dotnet8Variable} names no folder holding the .NET 8 SDK alone (Microsoft's archive of it, unpacked)");
        Find(Path.Combine(PathNames.Canonical(eight), DotnetName));
        TestFolders.Write(folder, "Eight.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net8.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Program.cs", "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine($\"total {total}\");\n\n");
        // From its own folder: the repository's global.json asks for an SDK the .NET 8 one alone can't be.
        Restore.Run(folder, from: folder);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile("Program.cs");
            Ui.WaitFor(() => window.CSharpService is { IsReady: true }, () => $"the language server to load Eight.csproj ({window.CSharpService?.State})", TimeSpan.FromMinutes(2));
            Assert.True(window.Projects.Single().IsEvaluated, $"Eight.csproj was not evaluated with the .NET 8 SDK: {Ui.Said(window)}");
            Completes(window, "Console.WriteLine($\"total {total}\");\n");

            Ui.Launched(window, window.RunAsync(), "Eight to build and run with the .NET 8 SDK", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "Eight to finish", TimeSpan.FromMinutes(2));
            Assert.Contains("total 6", Output(window), StringComparison.Ordinal);

            window.Debugger.Breakpoints.Toggle(Path.Combine(folder, "Program.cs"), 3);
            Ui.Launched(window, window.DebugAsync(), "Eight to start under the debugger", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Debugger.Stop is not null || window.Debugger.Session?.State == DebugState.Ended, "Eight to stop at its breakpoint", TimeSpan.FromMinutes(1));
            Assert.True(window.Debugger.Stop?.Top is { Line: 4 }, $"Eight did not stop at line 4 but at {window.Debugger.Stop?.Top?.Source?.Path}:{window.Debugger.Stop?.Top?.Line} ({window.Status})");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
            window.Close();
        }
    }

    /// <summary>An x64 Passenger the system translates (Rosetta, Windows' emulation) whose only .NET SDK is built for the
    /// system's own processor, whose MSBuild its project host would end loading, says in the banner and the status bar that
    /// it needs an x64 SDK and that the system's own build of Passenger suits the computer.</summary>
    [AvaloniaFact]
    public void A_translated_passenger_whose_only_sdk_is_the_systems_own_says_in_the_window_that_it_needs_an_x64_one()
    {
        string arm = Installation("arm64", 0x0100000C, "11.0.100");
        Find(Path.Combine(arm, DotnetName));
        Dotnet.TranslatedStandIn = true;
        string app = Path.Combine(folder, "app");
        TestFolders.Write(app, "App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(app, "Program.cs", "System.Console.WriteLine(1);\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        try
        {
            window.OpenFolder(app);
            Ui.WaitFor(() => window.DotnetSaid == Refusals.OnlyNativeSdk && window.Status == Refusals.OnlyNativeSdk,
                () => $"the banner and the status bar to say an x64 SDK is needed; the banner says \"{window.DotnetSaid}\" and the status bar \"{window.Status}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An x64 Passenger the system translates whose x64 .NET beside the system's own holds a runtime alone, where
    /// the system's own holds an SDK, says that it needs an x64 SDK and that Passenger for this computer's processor suits
    /// it, in the banner, the status bar and the refusals of what needs an SDK, never that only a runtime is installed; the
    /// banner's link and a click on the status bar download that Passenger.</summary>
    [AvaloniaFact]
    public void A_translated_passenger_whose_x64_dotnet_holds_a_runtime_alone_beside_the_systems_own_sdk_offers_this_computers_download()
    {
        string x64 = Installation("x64", 0x01000007);
        string systems = Installation("arm64", 0x0100000C, "11.0.100");
        // Found first, as the x64 installation is beside the system's own; the system's own where the installers put it.
        Find(Path.Combine(x64, DotnetName));
        Dotnet.HostSearch = null;
        Dotnet.PlacesStandIn = [Path.Combine(systems, DotnetName)];
        Dotnet.TranslatedStandIn = true;
        Dotnet.ForgetSdks();
        string app = Path.Combine(folder, "app");
        TestFolders.Write(app, "App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(app, "Program.cs", "System.Console.WriteLine(1);\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        try
        {
            window.OpenFolder(app);
            Ui.WaitFor(() => window.DotnetSaid == Refusals.OnlyNativeSdk && window.Status == Refusals.OnlyNativeSdk,
                () => $"the banner and the status bar to say an x64 SDK is needed; the banner says \"{window.DotnetSaid}\" and the status bar \"{window.Status}\"");
            Assert.True(window.DotnetWant is { Lack: DotnetLack.OtherProcessor, Install: null } want && want.SystemsOwn == systems,
                $"The window took the want for {window.DotnetWant.Lack}, the system's own SDK in {window.DotnetWant.SystemsOwn}, with {window.DotnetWant.Install} to install");
            Assert.True(window.Session.Models.MissingSdk is { ForOtherProcessor: true } refused && refused.Said.Contains(x64, StringComparison.Ordinal),
                $"The project host refused the folder saying: {window.Session.Models.MissingSdk?.Said}");
            Assert.Equal(Refusals.OnlyNativeSdk, window.Actions.WhyNot(AppActions.Build, window.Actions.Context()));
            Assert.Equal(Refusals.OnlyNativeSdk, window.Actions.WhyNot(AppActions.InstallDotnetSdk, window.Actions.Context()));
            string? told = Ui.Await(new App.Mcp.PassengerTools(window).GetWorkspace(), "an agent's workspace").SdkMissing;
            Assert.True(told is not null && told.StartsWith(Refusals.OnlyNativeSdk, StringComparison.Ordinal) && told.Contains(Releases.ThisSystemsDownload.AbsoluteUri, StringComparison.Ordinal),
                $"An agent's workspace said of .NET: {told}");

            Ui.ClickButton(window, OperatingSystem.IsMacOS() ? "Download Passenger for Apple Silicon" : "Download Passenger for Arm64");
            Assert.Equal(Releases.ThisSystemsDownload, Assert.Single(window.Browsed));
            Assert.Equal("download the Passenger built for this computer", window.StatusPlace?.Name);
            window.StatusPlace!.Go();
            Assert.Equal([Releases.ThisSystemsDownload, Releases.ThisSystemsDownload], window.Browsed);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An installation in the test's folder whose dotnet is a Mach-O program of <paramref name="cpu"/>, holding
    /// the runtime 11.0.0 and these SDKs; its folder.</summary>
    private string Installation(string name, int cpu, params string[] sdks)
    {
        string root = Path.Combine(folder, name);
        byte[] program = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(program, 0xFEEDFACF);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(program.AsSpan(4), cpu);
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "11.0.0"));
        File.WriteAllBytes(Path.Combine(root, DotnetName), program);
        foreach (string sdk in sdks)
        {
            TestFolders.Write(root, $"sdk/{sdk}/dotnet.dll", string.Empty);
            TestFolders.Write(root, $"sdk/{sdk}/dotnet.runtimeconfig.json",
                "{ \"runtimeOptions\": { \"tfm\": \"net11.0\", \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"11.0.0\" } } }\n");
        }
        return root;
    }

    /// <summary>Console's members are offered after its dot, typed on a line of its own after <paramref name="after"/>, the
    /// line then made a whole statement, so the program still builds.</summary>
    private static void Completes(MainWindow window, string after)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.KeyTextInput("System.Console");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("WriteLine"),
            () => $"Console's members after its dot, where the list shows [{string.Join(", ", window.Assist.CompletionLabels.Take(8))}]");
        Ui.Press(window, "ESCAPE");
        Typing.Replace(window, "System.Console.", "System.Console.WriteLine(\"completed\");");
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
