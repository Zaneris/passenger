using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger whose dotnet is a link on the PATH into the installation, as Linux distributions' packages make
/// /usr/bin/dotnet: an agent's Passenger (<c>--mcp</c>) on a runtime apart from any dotnet, as an installed Passenger's
/// is, finds the link, and what lies beside the installation's dotnet (its SDK's vstest.console, its runtimes' exception
/// types, its packs' reference assemblies) is found there. Its Passengers are processes of their own, so it runs beside
/// the window tests (<see cref="Beside"/>).</summary>
[Collection(Beside.Name)]
public sealed class LinkedDotnetTests(LinkedDotnetTests.Linked linked) : McpRig, IClassFixture<LinkedDotnetTests.Linked>
{
    /// <summary>Why the tests skip on Windows.</summary>
    private const string NoLinks = "Windows' installers put dotnet.exe itself on the PATH, and Windows lets a user make no link without Developer Mode";

    [Fact]
    public async Task An_agent_runs_a_vstest_projects_tests_with_dotnet_a_link_on_the_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoLinks);
        string tests = PathNames.Canonical(TestFolders.Create("linked-vstest-"));
        DeleteAtEnd(tests);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(tests, "Sums.Tests.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n" +
                "  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n" +
                "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\" />\n  </ItemGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "SumTests.cs"),
                "using Xunit;\n\nnamespace Sums.Tests;\n\npublic class SumTests\n{\n    [Fact]\n    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);\n}\n");
            await Restore.RunAsync(tests);
        }, tests);
        await using McpClient client = await Start(["--mcp", tests], environment: linked.Environment, languageServer: false);
        string summary = (await Call(client, McpTools.TestsRun)).GetProperty("summary").GetString()!;
        Assert.True(summary.StartsWith("Tests passed: 1, failed: 0 of 1", StringComparison.Ordinal),
            $"With dotnet a link on the PATH, an agent's run of a VSTest project's passing test answered: {summary}");
    }

    [Fact]
    public async Task Add_clr_exception_breakpoints_offers_the_runtimes_exception_types_with_dotnet_a_link_on_the_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoLinks);
        string folder = PathNames.Canonical(TestFolders.Create("linked-exceptions-"));
        DeleteAtEnd(folder);
        await using McpClient client = await Start(["--mcp", folder], environment: linked.Environment, languageServer: false);
        await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ViewBreakpoints });
        await Call(client, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "BreakpointsWindow/Button.tool[1]" });
        await Call(client, McpTools.AppType, new() { ["app"] = "ide", ["control"] = "BreakpointsWindow/TextBox.search", ["text"] = "ArgumentNull" });
        Dictionary<string, object?> offered = new() { ["app"] = "ide", ["filter"] = "BreakpointsWindow/ListBoxItem/\"ArgumentNullException\"" };
        string found = string.Empty;
        await Waiting.UntilAsync(async () => (found = Text(await Call(client, McpTools.AppTree, offered))).StartsWith("TextBlock", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(found.StartsWith("TextBlock", StringComparison.Ordinal),
            $"With dotnet a link on the PATH, Add CLR Exception Breakpoints offers none of the runtime's exception types for \"ArgumentNull\": {found}");
    }

    [Fact]
    public async Task Xaml_resolves_a_framework_type_with_dotnet_a_link_on_the_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), NoLinks);
        string app = PathNames.Canonical(TestFolders.Create("linked-xaml-"));
        DeleteAtEnd(app);
        string window = "<Window xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "        xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\">\n    <Window.Resources>\n" +
            "        <sys:String x:Key=\"Greeting\">Hello</sys:String>\n    </Window.Resources>\n</Window>\n";
        await TestFolders.PrepareAsync(async () =>
        {
            // The SDK's own framework, whose reference assemblies only its packs hold: no restore downloads them.
            File.WriteAllText(Path.Combine(app, "Linked.csproj"), AvaloniaApp.HeadlessProject().Replace(
                $"<TargetFramework>{TestProjects.Framework}<", $"<TargetFramework>{AvaloniaApp.Metadata("TargetFramework")}<", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(app, "MainWindow.axaml"), window);
            await Restore.RunAsync(app);
        }, app);
        await using McpClient client = await Start(["--mcp", app], environment: linked.Environment, languageServer: false);
        int line = window[..window.IndexOf("<sys:String", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        JsonElement type = await Call(client, McpTools.SymbolInfo, new() { ["path"] = "MainWindow.axaml", ["line"] = line, ["column"] = 15 });
        Assert.True(Text(type).StartsWith("class System.String", StringComparison.Ordinal),
            $"With dotnet a link on the PATH, XAML does not resolve sys:String, the framework's System.String: {Text(type)}");
    }

    /// <summary>A runtime apart from any dotnet, as an installed Passenger's is: the tests' own copied (as APFS clones on
    /// macOS, hard links on Linux where it lets this user make them), and a folder whose dotnet is a link to the
    /// installation's. The agents' Passengers run on the one and find the other first on the PATH.</summary>
    public sealed class Linked : IAsyncLifetime
    {
        private readonly string folder = TestFolders.Unowned("linked-dotnet-");

        /// <summary>The agents' Passengers' environment: the runtime copied for the app's host, the link first on the PATH,
        /// no DOTNET_ROOT, and a terminal's, so no login shell's PATH is read over it.</summary>
        public Dictionary<string, string?> Environment { get; } = [];

        public async ValueTask InitializeAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            await TestFolders.PrepareAsync(async () =>
            {
                string runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
                string installation = Dotnet.Root();
                string own = Path.Combine(folder, "runtime");
                Directory.CreateDirectory(Path.Combine(own, "shared", "Microsoft.NETCore.App"));
                await Copy(Path.Combine(installation, "host"), Path.Combine(own, "host"));
                await Copy(runtime, Path.Combine(own, "shared", "Microsoft.NETCore.App", Path.GetFileName(runtime)));
                string bin = Directory.CreateDirectory(Path.Combine(folder, "bin")).FullName;
                File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), Path.Combine(installation, "dotnet"));
                Environment["DOTNET_ROOT"] = null;
                Environment["DOTNET_HOST_PATH"] = null;
                Environment[$"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}"] = own;
                Environment["PATH"] = bin + Path.PathSeparator + System.Environment.GetEnvironmentVariable("PATH");
                Environment["TERM"] = "dumb";
            }, folder);
        }

        public ValueTask DisposeAsync()
        {
            TestFolders.Delete(folder);
            return ValueTask.CompletedTask;
        }

        /// <summary>A folder copied by cp: cloned or hard-linked, else, where the file system or the files' owner refuses
        /// that, copied whole.</summary>
        private static async Task Copy(string from, string to)
        {
            TimeSpan limit = TimeSpan.FromMinutes(2).Scaled();
            ChildResult copied = await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/cp", [OperatingSystem.IsMacOS() ? "-c" : "-l", "-R", from, to]), limit: limit);
            if (!copied.Succeeded)
            {
                if (Directory.Exists(to))
                {
                    Directory.Delete(to, recursive: true);
                }
                copied = await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/cp", ["-R", from, to]), limit: limit);
            }
            Assert.True(copied.Succeeded, $"The runtime at {from} could not be copied to {to}, for a Passenger to run on apart from any dotnet: {copied.Output}{copied.Error}");
        }
    }
}
