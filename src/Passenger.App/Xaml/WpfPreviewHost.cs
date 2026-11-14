using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Passenger.Core;
using Passenger.Text;
using Passenger.WpfPreview;
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
            await started.RunAsync(inputs);
            return started;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            started.Dispose();
            preview.Say($"The previewer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The previewer started on the copy with the project's runtime configuration and dependencies.</summary>
    private async Task RunAsync(ProjectInputs inputs)
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
        ProcessStartInfo start = ChildProcesses.DotnetCommand(arguments, Shadow);
        start.Environment[RuntimeVariables.XamlDiagnosticsSourceInfo] = "1";
        if (own)
        {
            // Its own configuration rolls forward to the newest runtime, which a project on a preview of .NET needs.
            start.Environment[RuntimeVariables.RollForwardToPrerelease] = "1";
        }
        await StartAsync(start, $"Starting the previewer on {Path.GetFileName(Assembly)}…");
    }

    protected override Func<(string Path, string Xaml)?, string> Request(int sequence, TextSnapshot text)
    {
        string path = Preview.Document.Document.Path;
        double scale = Preview.Scaling;
        return application => JsonSerializer.Serialize(new WpfPreviewRequest
        {
            Sequence = sequence,
            Path = path,
            Xaml = text.ToString(),
            BaseUri = BaseUri(path),
            Application = application is { } app ? new WpfPreviewSource(app.Path, app.Xaml, BaseUri(app.Path)) : null,
            Scale = scale,
        }, WpfPreviewJson.Options);
    }

    protected override async IAsyncEnumerable<PreviewAnswer> AnswersAsync(Stream output, Action<string> unread)
    {
        await foreach (WpfPreviewReply reply in JsonLines.ReadAsync(output, WpfPreviewJson.Reply, (line, _) => unread(line)))
        {
            yield return new PreviewAnswer(reply.Kind switch
            {
                WpfPreviewKinds.Frame => PreviewAnswerKind.Frame,
                WpfPreviewKinds.Error => PreviewAnswerKind.Error,
                _ => PreviewAnswerKind.Nothing,
            }, reply.Sequence)
            {
                Png = reply.Png,
                Scale = reply.Scale,
                Boxes = [.. (reply.Elements ?? []).Select(b => (b.Line, b.Column, new Rect(b.X, b.Y, b.Width, b.Height)))],
                Message = reply.Message,
                Line = reply.Line,
                Column = reply.Column,
                Notes = [.. (reply.Notes ?? []).Select(n => (n.Message, n.Line))],
            };
        }
    }

    /// <summary>A file's pack URI in the project's assembly, which relative URIs in it resolve against as in the build.</summary>
    private string BaseUri(string path)
    {
        ProjectInputs inputs = Builder.Current.Inputs;
        string relative = Path.GetRelativePath(inputs.Folder, path).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal))
        {
            relative = Path.GetFileName(path);
        }
        return $"pack://application:,,,/{Uri.EscapeDataString(inputs.AssemblyName)};component/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}";
    }
}
