using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Avalonia;
using Avalonia.Controls.Maui;
using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Passenger.Core;
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace Passenger.MauiPreview;

/// <summary>The previewer's start: the project's assembly loaded (its other dependencies from its build's folder and the
/// package folders its restore names), Avalonia headless with Skia, MAUI's app built on Avalonia's backend with the
/// project's App, fonts and images, and requests read from standard input on a thread of their own until it closes, which
/// is Passenger letting go, each drawn on this one. Standard output is the protocol's alone: whatever else writes to the
/// console (the project's own code included) goes to standard error.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The whole of it, then one line that says it, which is what Passenger shows of a previewer that ended.
            Console.Error.WriteLine(e.ExceptionObject);
            Console.Error.WriteLine(e.ExceptionObject is Exception error ? $"The previewer failed: {error.GetType().Name}: {error.Message}" : "The previewer failed");
        };
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        TextWriter protocol = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        TextReader input = new StreamReader(Console.OpenStandardInput(), utf8);
        Console.SetOut(Console.Error);
        if (Options.Read(args) is not { } options)
        {
            Console.Error.WriteLine("Usage: Passenger.MauiPreview --assembly PROJECT.dll [--fonts FOLDER]... [--images FOLDER]... [--packages FOLDER]...");
            return 2;
        }
        // Each element made from XAML keeps its line and column only with MAUI's diagnostics on: what its box is placed by.
        AppContext.SetSwitch("Microsoft.Maui.RuntimeFeature.EnableMauiDiagnostics", true);
        ProjectDependencies dependencies = ProjectDependencies.Of(options.Assembly, options.Packages);
        AssemblyLoadContext.Default.Resolving += (context, name) => dependencies.Find(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
        Assembly project;
        try
        {
            project = AssemblyLoadContext.Default.LoadFromAssemblyPath(options.Assembly);
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException)
        {
            Console.Error.WriteLine($"The project's assembly {options.Assembly} did not load: {error.Message}");
            return 3;
        }
        AppBuilder.Configure<PreviewApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .SetupWithoutStarting();
        Type? appType = Previewer.Loadable(project).FirstOrDefault(t => typeof(MauiApplication).IsAssignableFrom(t) && !t.IsAbstract);
        MauiApp app = Build(project, appType, options);
        Previewer previewer = new(project, appType, new MauiContext(app.Services), protocol);
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // The project's code failing as its page is laid out (a converter, a handler) is the frame's error.
            e.Handled = true;
            previewer.Failed(e.Exception);
        };
        Requests requests = new();
        Thread reading = new(() => requests.Read(input)) { IsBackground = true, Name = "Passenger preview requests" };
        reading.Start();
        while (requests.Next() is { } request)
        {
            previewer.Draw(request);
        }
        return 0;
    }

    /// <summary>MAUI's app on Avalonia's backend with the project's App class, its handlers, fonts and images: what every
    /// page is drawn in. Its MauiProgram never runs, nor anything it registers.</summary>
    private static MauiApp Build(Assembly project, Type? appType, Options options)
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        if (appType is not null && UseMauiApp(project) is { } use)
        {
            use.MakeGenericMethod(appType).Invoke(null, [builder]);
        }
        builder.UseAvaloniaApp(useSingleViewLifetime: true);
        // A view no handler is registered for (a platform view MauiProgram registers, a library's) falls back to View's.
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<Microsoft.Maui.Controls.View, UnhandledViewHandler>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IFontRegistrar>(ProjectFonts.Load(options.Fonts)));
        if (options.Images.Count > 0)
        {
            builder.ConfigureImageSources(services => services.AddService<IFileImageSource>(_ => new ProjectImages(options.Images)));
        }
        return builder.Build();
    }

    /// <summary>MAUI's UseMauiApp&lt;TApp&gt;(MauiAppBuilder): the project's own where MAUI's generator wrote one, else MAUI's
    /// (in .NET 11 its XAML assembly's); null where neither is found.</summary>
    private static MethodInfo? UseMauiApp(Assembly project) =>
        new[] { project, typeof(MauiApplication).Assembly, typeof(Microsoft.Maui.Controls.Xaml.Extensions).Assembly }
            .SelectMany(Previewer.Loadable)
            .Where(t => t.IsAbstract && t.IsSealed)
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .FirstOrDefault(m => m.Name == "UseMauiApp" && m.IsGenericMethod && m.GetParameters() is [{ } only] && only.ParameterType == typeof(MauiAppBuilder));

    /// <summary>What the previewer is started with: the project's assembly, the folders of its fonts and images, and the
    /// package folders its restore resolves into.</summary>
    private sealed record Options(string Assembly, IReadOnlyList<string> Fonts, IReadOnlyList<string> Images, IReadOnlyList<string> Packages)
    {
        public static Options? Read(string[] args)
        {
            string? assembly = null;
            List<string> fonts = [];
            List<string> images = [];
            List<string> packages = [];
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--assembly":
                        assembly = Path.GetFullPath(args[i + 1]);
                        break;
                    case "--fonts":
                        fonts.Add(args[i + 1]);
                        break;
                    case "--images":
                        images.Add(args[i + 1]);
                        break;
                    case "--packages":
                        packages.Add(args[i + 1]);
                        break;
                    default:
                        return null;
                }
            }
            return args.Length % 2 == 0 && assembly is not null ? new Options(assembly, fonts, images, packages) : null;
        }
    }
}

/// <summary>The newest request read, for the drawing thread to take: one read while another is drawn replaces any still
/// waiting, so only the newest text is drawn.</summary>
internal sealed class Requests
{
    private readonly object gate = new();
    private MauiPreviewRequest? waiting;
    private bool ended;

    /// <summary>Reads requests until Passenger closes the previewer's input.</summary>
    public void Read(TextReader input)
    {
        try
        {
            while (JsonLines.Read(input, MauiPreviewJson.Request, static (_, error) => Console.Error.WriteLine($"A request that does not read: {error.Message}"))
                is { } request)
            {
                lock (gate)
                {
                    waiting = request;
                    Monitor.Pulse(gate);
                }
            }
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"Passenger's requests ended: {error.Message}");
        }
        lock (gate)
        {
            ended = true;
            Monitor.Pulse(gate);
        }
    }

    /// <summary>The newest request not yet drawn, waited for; null once the requests have ended.</summary>
    public MauiPreviewRequest? Next()
    {
        lock (gate)
        {
            while (waiting is null && !ended)
            {
                Monitor.Wait(gate);
            }
            MauiPreviewRequest? next = waiting;
            waiting = null;
            return next;
        }
    }
}

/// <summary>The Avalonia application the backend's controls are styled by: Fluent with the backend's styles, in the theme
/// each request asks for.</summary>
public sealed class PreviewApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new ControlStyles());
    }
}
