using System.Diagnostics;
using System.Text.Json;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;
using Passenger.WpfPreview;
using Passenger.Xaml;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>A WPF file's design view, drawn by Passenger's own previewer (src/Passenger.WpfPreview, under
/// <c>wpfpreview/</c> on Windows) run by the machine's dotnet on the project's runtime configuration (the previewer's
/// own, rolling forward, for a class library or a project before .NET 8) and dependencies, from a copy of its last build
/// (<see cref="ChildPreviewHost"/>). Each request carries the pack URIs the file and App.xaml have in the project's
/// assembly. A UI-thread object.</summary>
internal sealed class WpfPreviewHost : ChildPreviewHost
{
    private WpfPreviewHost(XamlPreview preview, XamlProjectBuilder builder, PreviewedBuild build, string assembly, string shadow)
        : base(preview, builder, build, assembly, shadow, "WPF")
    {
    }

    /// <summary>Where the app carries the previewer.</summary>
    public static string Previewer => Path.Combine(AppContext.BaseDirectory, "wpfpreview", "Passenger.WpfPreview.dll");

    /// <summary>Starts the previewer on a copy of the project's last build; null, having said why, when there is none to
    /// start.</summary>
    public static async Task<WpfPreviewHost?> StartAsync(XamlPreview preview, XamlProjectBuilder builder)
    {
        ProjectInputs inputs = builder.Current.Inputs;
        if (XamlPreview.Output(inputs, configured: false) is not { } built)
        {
            preview.SayUnbuilt(inputs);
            return null;
        }
        if (!File.Exists(Previewer))
        {
            preview.Say("WPF's previewer is missing from Passenger's folder (wpfpreview/)");
            return null;
        }
        if (await CopyAsync(preview, built) is not { } copied)
        {
            return null;
        }
        WpfPreviewHost started = new(preview, builder, copied.Build, Path.Combine(copied.Shadow, Path.GetFileName(built)), copied.Shadow);
        try
        {
            if (await started.RunAsync(inputs) is { } refused)
            {
                started.Dispose();
                preview.Say($"The previewer could not start: {refused}");
                return null;
            }
            return started;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            started.Dispose();
            preview.Say($"The previewer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The previewer started on the copy with the project's runtime configuration and dependencies, on the x86 .NET
    /// for a build for 32-bit x86 (<see cref="X86Programs"/>), whose assembly no 64-bit process loads; null once started,
    /// else why not.</summary>
    private async Task<string?> RunAsync(ProjectInputs inputs)
    {
        string name = Path.GetFileNameWithoutExtension(Assembly);
        string configuration = Path.Combine(Shadow, name + ".runtimeconfig.json");
        // A class library has no runtime configuration, and a build for one older than .NET 8 runs no previewer built for 8.
        string assembly = Assembly;
        string? runs = await BackgroundWork.Run(() => RuntimeConfig.Read(assembly)?.TargetFramework);
        bool own = FrameworkNames.Major(runs) is not >= 8;
        if (own)
        {
            configuration = Path.ChangeExtension(Previewer, ".runtimeconfig.json");
        }
        string? x86 = null;
        if (OperatingSystem.IsWindows() && await BackgroundWork.Run(() => X86Programs.Requires(assembly)))
        {
            x86 = X86Programs.Root(new Dictionary<string, string>());
            string root = x86;
            string configured = own ? Previewer : assembly;
            if (await BackgroundWork.Run(() => X86Programs.Lacking(root, configured, new Dictionary<string, string>())) is { Count: > 0 } lacking)
            {
                return Refusals.X86RuntimeMissing(root, lacking);
            }
        }
        List<string> arguments = ["exec", "--runtimeconfig", configuration];
        string dependencies = Path.Combine(Shadow, name + ".deps.json");
        if (File.Exists(dependencies))
        {
            arguments.AddRange(["--depsfile", dependencies]);
        }
        // A class library's build leaves its packages where restore put them, which its dependencies name as a cache does.
        foreach (string cache in inputs.PackageFolders.Values.Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f))).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            arguments.AddRange(["--additionalprobingpath", cache]);
        }
        arguments.AddRange([Previewer, "--assembly", Assembly]);
        // The previewer, built for any processor, runs as the x86 dotnet runs it.
        ProcessStartInfo start = x86 is null ? ChildProcesses.DotnetCommand(arguments, Shadow) : ChildProcesses.DotnetCommand(Path.Combine(x86, Dotnet.HostName), arguments, Shadow);
        start.Environment[RuntimeVariables.XamlDiagnosticsSourceInfo] = "1";
        if (own)
        {
            // Its own configuration rolls forward to the newest runtime, which a project on a preview of .NET needs.
            start.Environment[RuntimeVariables.RollForwardToPrerelease] = "1";
        }
        await StartAsync(start, $"Starting the previewer on {Path.GetFileName(Assembly)}…");
        return null;
    }

    protected override Func<(string Path, string Xaml)?, string> Request(int sequence, TextSnapshot text)
    {
        string path = Preview.Document.Document.Path;
        double scale = Preview.Scaling;
        XamlProject project = Builder.Current;
        return application => JsonSerializer.Serialize(new WpfPreviewRequest
        {
            Sequence = sequence,
            Path = path,
            Xaml = text.ToString(),
            BaseUri = BaseUri(project, path),
            Application = application is { } app ? new PreviewSource(app.Path, app.Xaml, BaseUri(project, app.Path)) : null,
            Scale = scale,
        }, PeerJson.Options);
    }

    /// <summary>A file's pack URI in the project's assembly, which relative URIs in it resolve against as in the build: a WPF
    /// project's files all have one.</summary>
    private static string BaseUri(XamlProject project, string path) => project.UriOf(path)!;
}
