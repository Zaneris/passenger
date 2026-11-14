using System.Reflection;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An Avalonia app as <c>dotnet new avalonia.app</c> makes it, from Avalonia's own templates at the Avalonia
/// version Passenger uses (so its packages are the ones already restored), installed once per run into the run's own
/// template hive, never the person's, from the run's own local feed (<see cref="Feed"/>), never nuget.org. The template's
/// reference to Avalonia's paid developer tools is taken out, as nothing of Avalonia Accelerate may be used.</summary>
internal static class AvaloniaApp
{
    private static readonly Lazy<Task<string>> Hive = new(InstallAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> FeedFolder = new(MakeFeed, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The Avalonia version the tests run on, which the template is asked for.</summary>
    public static string Version { get; } =
        typeof(Avalonia.Controls.Button).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>Avalonia.Templates' version, the one the test project downloads (Directory.Packages.props).</summary>
    public static string TemplatesVersion { get; } = Metadata("AvaloniaTemplatesVersion");

    /// <summary>The run's local feed: a folder of packages holding Avalonia.Templates (and WindowsDesktop's reference pack,
    /// once <see cref="WpfApp"/> adds it), copied from the package cache the test project's restore downloaded them into.</summary>
    public static string Feed => Path.Combine(FeedFolder.Value, "packages");

    /// <summary>Writes a nuget.config into a folder that names the run's local feed and no other, so a dotnet new install
    /// or a restore under it reaches nothing else; the package cache is still the run's.</summary>
    public static void UseFeed(string folder) => File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="feed" value="{System.Security.SecurityElement.Escape(Feed)}" />
          </packageSources>
        </configuration>
        """);

    private static string MakeFeed()
    {
        string folder = Directory.CreateDirectory(Path.Combine(TestRun.Root, "feed")).FullName;
        string packages = Directory.CreateDirectory(Path.Combine(folder, "packages")).FullName;
        string version = TemplatesVersion.ToLowerInvariant();
        string downloaded = Path.Combine(Metadata("NuGetPackageRoot"), "avalonia.templates", version, $"avalonia.templates.{version}.nupkg");
        Assert.True(File.Exists(downloaded), $"Avalonia.Templates {TemplatesVersion} is not in the package cache at {downloaded}: restore the tests first");
        File.Copy(downloaded, Path.Combine(packages, Path.GetFileName(downloaded)), overwrite: true);
        return folder;
    }

    private static async Task<string> InstallAsync()
    {
        string hive = Directory.CreateDirectory(Path.Combine(TestRun.Root, "templates")).FullName;
        UseFeed(FeedFolder.Value);
        ChildResult installed = await Run(["new", "install", $"Avalonia.Templates::{TemplatesVersion}", "--debug:custom-hive", hive], FeedFolder.Value);
        Assert.True(installed.Succeeded, $"Avalonia's templates did not install into the run's own hive (exit code {installed.ExitCode}):\n{installed.Output}{installed.Error}");
        return hive;
    }

    /// <summary>An item group referencing packages, on the Avalonia the tests run.</summary>
    public static string Packages(params string[] names) =>
        $"  <ItemGroup>\n{string.Concat(names.Select(p => $"    <PackageReference Include=\"{p}\" Version=\"{Version}\" />\n"))}  </ItemGroup>\n";

    /// <summary>The project file of a headless app of tests/Debuggees (LiveTreeApp, BindingErrorsApp, AgentApp), which have
    /// none of their own: on the Avalonia the tests run, so their restore already has its packages.</summary>
    public static string HeadlessProject()
    {
        string packages = string.Concat(new[] { "Avalonia", "Avalonia.Themes.Fluent", "Avalonia.Fonts.Inter", "Avalonia.Headless", "Avalonia.HarfBuzz", "Avalonia.Skia" }
            .Select(p => $"    <PackageReference Include=\"{p}\" Version=\"{Version}\" />\n"));
        return TestProjects.Console("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>").With($"  <ItemGroup>\n{packages}  </ItemGroup>\n");
    }

    /// <summary>The project file of an app of tests/Debuggees made a desktop app as a person's is: on the desktop's own
    /// platforms (Avalonia.Desktop), with no headless platform of its own unless it takes that package too.</summary>
    public static string DesktopProject(params string[] more)
    {
        string packages = string.Concat(new[] { "Avalonia", "Avalonia.Themes.Fluent", "Avalonia.Fonts.Inter", "Avalonia.Desktop" }.Concat(more)
            .Select(p => $"    <PackageReference Include=\"{p}\" Version=\"{Version}\" />\n"));
        return TestProjects.Console("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>").With($"  <ItemGroup>\n{packages}  </ItemGroup>\n");
    }

    /// <summary>A headless app of tests/Debuggees copied into a folder, its project written (<see cref="HeadlessProject"/>,
    /// else the one given): the project's path, for its restore.</summary>
    public static string Debuggee(string folder, string app, string? project = null)
    {
        foreach (string file in Directory.GetFiles(Passenger.Tests.Debugging.Helpers.Debuggees.Source(app)))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
        string written = Path.Combine(folder, $"{app}.csproj");
        File.WriteAllText(written, project ?? HeadlessProject());
        return written;
    }

    /// <summary><see cref="CreateAsync"/> from the UI thread (a window test's body), run on the thread pool and waited for
    /// with the window's loop running (<see cref="Ui.AwaitPumped{T}"/>), never for its work to land.</summary>
    public static void Create(string folder, string name) =>
        Ui.AwaitPumped(Waiting.Apart(() => CreateAsync(folder, name)), $"dotnet new avalonia.app to make {name}", (Limit * 2) + TestFolders.Spare);

    /// <summary>Makes the app in a folder, its project and namespace named <paramref name="name"/>, without restoring it.</summary>
    public static async Task CreateAsync(string folder, string name)
    {
        ChildResult made = await Run(["new", "avalonia.app", "--output", folder, "--name", name, "--framework", TestProjects.Framework, "--avalonia-version", Version,
            "--no-restore", "--no-update-check", "--debug:custom-hive", await Hive.Value], null);
        Assert.True(made.Succeeded, $"dotnet new avalonia.app did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
        string project = Path.Combine(folder, name + ".csproj");
        string text = File.ReadAllText(project);
        int diagnostics = text.IndexOf("<PackageReference Include=\"AvaloniaUI.DiagnosticsSupport\"", StringComparison.Ordinal);
        if (diagnostics >= 0)
        {
            int end = text.IndexOf("</PackageReference>", diagnostics, StringComparison.Ordinal);
            end = end < 0 ? text.IndexOf("/>", diagnostics, StringComparison.Ordinal) + 2 : end + "</PackageReference>".Length;
            text = text[..diagnostics] + text[end..];
        }
        File.WriteAllText(project, text.Replace("</Project>", "  <ItemGroup>\n    <AvaloniaResource Include=\"Assets\\**\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
        string program = Path.Combine(folder, "Program.cs");
        string code = File.ReadAllText(program);
        File.WriteAllText(program, string.Join('\n', code.Split('\n').Where(line => !line.Contains("WithDeveloperTools", StringComparison.Ordinal)
            && line.Trim() is not ("#if DEBUG" or "#endif"))));
    }

    /// <summary>Makes Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>) in a folder, its projects named
    /// after <paramref name="name"/> (<c>name.Android</c> beside the shared <c>name</c>), and takes Avalonia Accelerate's
    /// developer tools out of it as New Solution does; nothing restored.</summary>
    public static async Task CreateCrossPlatformAsync(string folder, string name)
    {
        ChildResult made = await Run(["new", "avalonia.xplat", "--output", folder, "--name", name, "--avalonia-version", Version, "--no-update-check",
            "--debug:custom-hive", await Hive.Value], null);
        Assert.True(made.Succeeded, $"dotnet new avalonia.xplat did not make the test's solution (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
        Passenger.App.Templates.DeveloperTools.TakeOut(folder);
    }

    /// <summary>Avalonia's cross-platform solution made in a folder (<see cref="CreateCrossPlatformAsync"/>), its Android
    /// head (<c>name.Android</c>, its application <c>com.CompanyName.name</c>) taken to the first .NET whose Android apps run
    /// on CoreCLR and restored from the package cache alone: the head's project, and why it did not restore, or null.</summary>
    public static async Task<(string Project, string? Unrestored)> CreateAndroidHeadAsync(string folder, string name)
    {
        await CreateCrossPlatformAsync(folder, name);
        string project = Path.Combine(folder, $"{name}.Android", $"{name}.Android.csproj");
        // The template's head is .NET 10's, on Mono; on CoreCLR from .NET 11, whose Android workload takes API 24 at least.
        File.WriteAllText(project, File.ReadAllText(project).Replace("net10.0-android", $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android", StringComparison.Ordinal)
            .Replace("<SupportedOSPlatformVersion>23<", "<SupportedOSPlatformVersion>24<", StringComparison.Ordinal));
        ChildResult restored = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], Path.GetDirectoryName(project)!), limit: Limit * 2);
        return (project, restored.Succeeded ? null
            : $"The app's Android head does not restore here, from the package cache alone (Avalonia.Android {Version} and its AndroidX packages are there once an Avalonia "
                + $"Android app was restored): {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
    }

    /// <summary>A value the test project's build put in the assembly (Passenger.Tests.csproj).</summary>
    internal static string Metadata(string key) =>
        typeof(AvaloniaApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"The test assembly was built without its {key}");

    /// <summary>The longest dotnet new may take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static Task<ChildResult> Run(IReadOnlyList<string> arguments, string? folder) =>
        ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(arguments, folder), limit: Limit);
}
