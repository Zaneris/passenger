using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A WPF app as <c>dotnet new wpf</c> makes it (or a Windows Forms app as <c>dotnet new winforms</c> does), for
/// the test project's own target (the SDK's), from a template hive of the run's own, restoring from the run's local feed
/// alone (<see cref="AvaloniaApp.Feed"/>), never nuget.org. Both build for Windows alone; elsewhere the SDK builds them with
/// <c>EnableWindowsTargeting</c>, so their restore takes WindowsDesktop's reference pack (WPF's and Windows Forms' both),
/// which the test project downloads at the version Directory.Packages.props pins, from that feed, and MarkupCompile still
/// runs in a WPF app's build. The app's folder says it for the tests that restore it themselves; as dotnet new makes it,
/// it does not, and Passenger gives it.</summary>
internal static class WpfApp
{
    private static readonly Lazy<string> Hive = new(() => Directory.CreateDirectory(Path.Combine(TestRun.Root, "wpf-templates")).FullName,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The target the app is made for, the test project's own.</summary>
    private static string Framework => AvaloniaApp.Metadata("TargetFramework");

    /// <summary><see cref="CreateAsync"/> from the UI thread (a window test's body), run on the thread pool and waited for
    /// with the window's loop running (<see cref="Ui.AwaitPumped{T}"/>), never for its work to land.</summary>
    public static void Create(string folder, string name) =>
        Ui.AwaitPumped(Waiting.Apart(() => CreateAsync(folder, name)), $"dotnet new wpf to make {name}", Limit + TestFolders.Spare);

    /// <summary>Makes the app in a folder, its project and namespace named <paramref name="name"/>, without restoring it;
    /// off Windows its folder says EnableWindowsTargeting unless it is to be as dotnet new makes it.</summary>
    public static Task CreateAsync(string folder, string name, bool asMade = false) => Make("wpf", folder, name, asMade);

    /// <summary>A Windows Forms app as dotnet new winforms makes it, without restoring it, on the same reference pack.</summary>
    public static Task CreateWindowsFormsAsync(string folder, string name) => Make("winforms", folder, name, asMade: true);

    /// <summary>The project file of a WPF app of tests/Debuggees (WpfTreeApp), which has none of its own, written into its
    /// folder: a Windows app for the test project's own target, restoring from the run's local feed alone. Its path.</summary>
    public static string Project(string folder, string name)
    {
        AddToFeed(Pinned());
        AvaloniaApp.UseFeed(folder);
        string project = Path.Combine(folder, name + ".csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{Framework}-windows</TargetFramework>
                <UseWPF>true</UseWPF>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>

            """);
        return project;
    }

    /// <summary>The reference pack's version Directory.Packages.props pins, checked against the one this SDK's WPF takes.</summary>
    private static string Pinned()
    {
        string pinned = AvaloniaApp.Metadata("WindowsDesktopRefVersion");
        string needed = AvaloniaApp.Metadata("SdkWindowsDesktopRefVersion");
        Assert.True(pinned == needed, $"This SDK's WPF for {Framework} takes Microsoft.WindowsDesktop.App.Ref {needed}, but Directory.Packages.props "
            + $"pins WindowsDesktopRefVersion {pinned}, so the WPF app's restore would find no pack in the run's local feed: pin {needed} and restore "
            + "(dotnet restore Passenger.slnx --force-evaluate)");
        return pinned;
    }

    private static async Task Make(string template, string folder, string name, bool asMade)
    {
        string pinned = Pinned();
        ChildResult made = await Run(["new", template, "--output", folder, "--name", name, "--framework", Framework, "--no-restore", "--no-update-check",
            "--debug:custom-hive", Hive.Value]);
        Assert.True(made.Succeeded, $"dotnet new {template} did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
        AddToFeed(pinned);
        AvaloniaApp.UseFeed(folder);
        if (!asMade && !OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <EnableWindowsTargeting>true</EnableWindowsTargeting>
                  </PropertyGroup>
                </Project>

                """);
        }
    }

    /// <summary>Copies WindowsDesktop's reference pack into the run's local feed from the package cache the test project's
    /// restore downloaded it into.</summary>
    private static void AddToFeed(string version)
    {
        string lower = version.ToLowerInvariant();
        string downloaded = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "microsoft.windowsdesktop.app.ref", lower, $"microsoft.windowsdesktop.app.ref.{lower}.nupkg");
        Assert.True(File.Exists(downloaded), $"Microsoft.WindowsDesktop.App.Ref {version} is not in the package cache at {downloaded}: restore the tests first");
        string copy = Path.Combine(AvaloniaApp.Feed, Path.GetFileName(downloaded));
        // Two fixtures (WpfXamlTests' and XamlEditingTests' galleries) can make their apps at once: one copies, the other waits.
        lock (FeedGate)
        {
            if (!File.Exists(copy))
            {
                File.Copy(downloaded, copy);
            }
        }
    }

    private static readonly Lock FeedGate = new();

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static Task<ChildResult> Run(IReadOnlyList<string> arguments) =>
        ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments), limit: Limit);
}
