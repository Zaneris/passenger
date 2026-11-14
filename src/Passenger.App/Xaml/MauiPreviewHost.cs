using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Passenger.Core;
using Passenger.MauiPreview;
using Passenger.Text;
using Passenger.Xaml.Features;
using Passenger.Xaml.Projects;

namespace Passenger.App.Xaml;

/// <summary>A .NET MAUI file's design view, drawn by Passenger's own previewer (src/Passenger.MauiPreview, under
/// <c>mauipreview/</c>) with Avalonia's backend for MAUI, headless, on every system: run as a helper on Passenger's runtime
/// from a copy of the project's newest build of any target (<see cref="ChildPreviewHost"/>), a platform's types the
/// previewer cannot load passed over. Each request says the screen, platform and theme the preview is drawn as
/// (<see cref="XamlPreview.Look"/>), and carries the file's and App.xaml's text with MAUI's implicit namespaces written
/// (<see cref="MauiPreviewText"/>). A UI-thread object.</summary>
internal sealed class MauiPreviewHost : ChildPreviewHost
{
    /// <summary>The first MAUI whose controls the previewer draws: its own, which a project's assembly binds to by name.</summary>
    public const int FirstMaui = 11;

    private MauiPreviewHost(XamlPreview preview, XamlProjectBuilder builder, PreviewedBuild build, string assembly, string shadow)
        : base(preview, builder, build, assembly, shadow, "MAUI")
    {
    }

    /// <summary>Where the app carries the previewer.</summary>
    public static string Previewer => Path.Combine(AppContext.BaseDirectory, "mauipreview", "Passenger.MauiPreview.dll");

    /// <summary>Starts the previewer on a copy of the project's newest build; null, having said why, when there is none to
    /// start or the project's MAUI is older than the previewer's.</summary>
    public static async Task<MauiPreviewHost?> StartAsync(XamlPreview preview, XamlProjectBuilder builder)
    {
        ProjectInputs inputs = builder.Current.Inputs;
        if (MauiOf(inputs) is { } older && older.Major < FirstMaui)
        {
            preview.Say($"MAUI previews need .NET MAUI {FirstMaui}: {Path.GetFileNameWithoutExtension(inputs.ProjectPath)} is on MAUI {older.Major}");
            return null;
        }
        if (await BackgroundWork.Run(() => Newest(inputs)) is not { } built)
        {
            preview.SayUnbuilt(inputs);
            return null;
        }
        if (!File.Exists(Previewer))
        {
            preview.Say("MAUI's previewer is missing from Passenger's folder (mauipreview/)");
            return null;
        }
        if (await CopyAsync(preview, built) is not { } copied)
        {
            return null;
        }
        preview.BuiltPlatform = PlatformOf(built);
        MauiPreviewHost started = new(preview, builder, copied.Build, Path.Combine(copied.Shadow, Path.GetFileName(built)), copied.Shadow);
        try
        {
            await started.RunAsync(inputs);
            return started;
        }
        catch (Exception error) when (ChildFailures.Of(error) || error is FileNotFoundException)
        {
            started.Dispose();
            preview.Say($"The previewer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The version of MAUI's controls the project's restore resolved; null where it names none.</summary>
    internal static Version? MauiOf(ProjectInputs inputs) =>
        (inputs.PackageVersions.GetValueOrDefault("Microsoft.Maui.Controls") ?? inputs.PackageVersions.GetValueOrDefault("Microsoft.Maui.Controls.Core")) is { } named
            && Version.TryParse(named.Split('-', '+')[0], out Version? version) ? version : null;

    /// <summary>The project's newest build: the one an app's or a library's preview draws (<see cref="XamlPreview.Output"/>),
    /// else the newest build of its assembly for any target under its bin/ (a platform's has no .deps.json). Read off the
    /// window's thread.</summary>
    internal static string? Newest(ProjectInputs inputs)
    {
        if (XamlPreview.Output(inputs, configured: false) is { } found)
        {
            return found;
        }
        string bin = Path.Combine(inputs.Folder, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }
        string reference = $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(bin, inputs.AssemblyName + ".dll", SearchOption.AllDirectories)
            .Where(f => !f.Contains(reference, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>The platform a build is for, by its target framework's folder (<c>net11.0-ios</c>): Android for a plain one,
    /// the platform MAUI's template targets first.</summary>
    internal static string PlatformOf(string built)
    {
        string framework = Path.GetFileName(Path.GetDirectoryName(built)) ?? string.Empty;
        return framework switch
        {
            _ when framework.Contains("-ios", StringComparison.OrdinalIgnoreCase) => MauiPreviewDevices.IOS,
            _ when framework.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase) => MauiPreviewDevices.MacCatalyst,
            _ when framework.Contains("-windows", StringComparison.OrdinalIgnoreCase) => MauiPreviewDevices.Windows,
            _ => MauiPreviewDevices.Android,
        };
    }

    /// <summary>The previewer started on the copy, given where the project keeps its fonts and images and where its
    /// restore put its packages.</summary>
    private async Task RunAsync(ProjectInputs inputs)
    {
        List<string> arguments = ["--assembly", Assembly];
        string fonts = Path.Combine(inputs.Folder, "Resources", "Fonts");
        string images = Path.Combine(inputs.Folder, "Resources", "Images");
        if (await BackgroundWork.Run(() => Directory.Exists(fonts)))
        {
            arguments.AddRange(["--fonts", fonts]);
        }
        if (await BackgroundWork.Run(() => Directory.Exists(images)))
        {
            arguments.AddRange(["--images", images]);
        }
        foreach (string cache in inputs.PackageFolders.Values.Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f))).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            arguments.AddRange(["--packages", cache]);
        }
        await StartAsync(ChildProcesses.HelperCommand(Previewer, arguments, Shadow), $"Starting the previewer on {Path.GetFileName(Assembly)}…");
    }

    protected override Func<(string Path, string Xaml)?, string> Request(int sequence, TextSnapshot text)
    {
        string path = Preview.Document.Document.Path;
        double scale = Preview.Scaling;
        string platform = Preview.Look.Platform ?? Preview.BuiltPlatform;
        MauiScreen screen = Preview.Look.Screen ?? MauiLook.ScreenFor(platform);
        string theme = Preview.Look.Theme ?? (Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? MauiPreviewThemes.Dark : MauiPreviewThemes.Light);
        return application => JsonSerializer.Serialize(new MauiPreviewRequest
        {
            Sequence = sequence,
            Path = path,
            Xaml = MauiPreviewText.Declared(text.ToString()),
            Application = application is { } app ? new MauiPreviewSource(app.Path, MauiPreviewText.Declared(app.Xaml)) : null,
            Scale = scale,
            Width = screen.Width,
            Height = screen.Height,
            Platform = platform,
            Idiom = screen.Idiom,
            Theme = theme,
        }, MauiPreviewJson.Options);
    }

    protected override async IAsyncEnumerable<PreviewAnswer> AnswersAsync(Stream output, Action<string> unread)
    {
        await foreach (MauiPreviewReply reply in JsonLines.ReadAsync(output, MauiPreviewJson.Reply, (line, _) => unread(line)))
        {
            yield return new PreviewAnswer(reply.Kind switch
            {
                MauiPreviewKinds.Frame => PreviewAnswerKind.Frame,
                MauiPreviewKinds.Error => PreviewAnswerKind.Error,
                _ => PreviewAnswerKind.Nothing,
            }, reply.Sequence)
            {
                Png = reply.Png,
                Scale = reply.Scale,
                Boxes = [.. (reply.Elements ?? []).Select(b => (b.Line, b.Column, new Rect(b.X, b.Y, b.Width, b.Height)))],
                Message = reply.Message,
                Line = reply.Line,
                Column = reply.Column,
                Notes = [.. (reply.Notes ?? []).Select(n => (n, (int?)null))],
            };
        }
    }
}

/// <summary>A screen a MAUI preview is drawn on: its size in device-independent units and the idiom OnIdiom reads there.</summary>
public sealed record MauiScreen(string Name, double Width, double Height, string Idiom)
{
    /// <summary>The screen as the chooser names it.</summary>
    public string Shown => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Name} ({Width:0} × {Height:0})");
}

/// <summary>What a MAUI preview is drawn as: its screen, the platform OnPlatform reads (as MAUI names it) and the theme
/// AppThemeBinding reads; null for each where none is chosen, which is the platform of the build drawn, that platform's
/// own kind of screen, and the IDE's theme.</summary>
public sealed record MauiLook(MauiScreen? Screen, string? Platform, string? Theme)
{
    public static MauiLook AsBuilt { get; } = new(null, null, null);

    /// <summary>The screens offered, in the chooser's order.</summary>
    public static IReadOnlyList<MauiScreen> Screens { get; } =
    [
        new("Phone", 390, 844, MauiPreviewDevices.Phone),
        new("Tablet", 820, 1180, MauiPreviewDevices.Tablet),
        new("Desktop", 1280, 800, MauiPreviewDevices.Desktop),
    ];

    /// <summary>The platforms offered, each as the chooser names it and as MAUI does.</summary>
    public static IReadOnlyList<(string Shown, string Platform)> Platforms { get; } =
    [
        ("Android", MauiPreviewDevices.Android),
        ("iOS", MauiPreviewDevices.IOS),
        ("Mac Catalyst", MauiPreviewDevices.MacCatalyst),
        ("Windows", MauiPreviewDevices.Windows),
    ];

    /// <summary>A platform's own kind of screen: a desktop's for Mac Catalyst's and Windows', else a phone's.</summary>
    public static MauiScreen ScreenFor(string platform) =>
        platform is MauiPreviewDevices.MacCatalyst or MauiPreviewDevices.Windows ? Screens[2] : Screens[0];
}
