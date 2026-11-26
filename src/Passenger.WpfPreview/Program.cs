using System.ComponentModel;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Passenger.Core;

namespace Passenger.WpfPreview;

/// <summary>The previewer's start: the project's assembly loaded (its runtime configuration and dependencies are the
/// ones the previewer runs with) with what it references (<see cref="ProjectAssemblies"/>), an application of its own that
/// never shows a window, and requests read from standard
/// input on a thread of their own until it closes, which is Passenger letting go, all of it in design mode. Standard
/// output is the protocol's alone: whatever else writes to the console (the project's own code included) goes to
/// standard error.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything of WPF's, which reads it once: each element then knows its place in the XAML, for its box.
        Environment.SetEnvironmentVariable(RuntimeVariables.XamlDiagnosticsSourceInfo, "1");
        DesignMode();
        (TextWriter protocol, TextReader input) = PreviewWire.Start();
        if (args is not ["--assembly", { } path])
        {
            Console.Error.WriteLine("Usage: Passenger.WpfPreview --assembly PROJECT.dll");
            return 2;
        }
        Assembly project;
        try
        {
            project = ProjectAssembly(path);
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException)
        {
            Console.Error.WriteLine($"The project's assembly {path} did not load: {error.Message}");
            return 3;
        }
        ProjectAssemblies.Take(project, Path.GetDirectoryName(Path.GetFullPath(path))!);
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        TakeResources(project);
        PreviewRequests<WpfPreviewRequest> requests = new();
        Previewer previewer = new(project, protocol, requests, app.Dispatcher);
        app.DispatcherUnhandledException += (_, e) =>
        {
            // The project's code failing as it is drawn (a control's Loaded) is the preview's error, not the previewer's end.
            e.Handled = true;
            previewer.Failed(e.Exception);
        };
        Thread reading = new(() =>
        {
            requests.Read(input, previewer.Arrived);
            app.Dispatcher.InvokeAsync(app.Shutdown);
        }) { IsBackground = true, Name = "Passenger preview requests" };
        reading.Start();
        app.Run();
        return 0;
    }

    /// <summary>Design mode, set before anything of the project's loads: IsInDesignMode's default made true for every
    /// DependencyObject, so GetIsInDesignMode answers true for whatever the preview makes (in its constructor too,
    /// before it has a parent), and LicenseManager given a design-time context, so its UsageMode says
    /// Designtime.</summary>
    private static void DesignMode()
    {
        DesignerProperties.IsInDesignModeProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(true));
        LicenseManager.CurrentContext = new DesigntimeLicenseContext();
    }

    /// <summary>Has pack://application:,,,/ name the project's own resources, as it does in the running app. WPF lets only a
    /// process with no entry assembly (a designer's) set <see cref="Application.ResourceAssembly"/>, so it is set as that
    /// setter sets it; a WPF without those fields leaves it the previewer's, and only URIs naming no assembly miss.</summary>
    private static void TakeResources(Assembly project)
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo? application = typeof(Application).GetField("_resourceAssembly", Hidden);
        PropertyInfo? uris = typeof(BaseUriHelper).GetProperty("ResourceAssembly", Hidden);
        if (application is null || uris?.SetMethod is null)
        {
            Console.Error.WriteLine("This WPF does not let the previewer name the project's resources as the application's");
            return;
        }
        application.SetValue(null, project);
        uris.SetValue(null, project);
    }

    /// <summary>The project's assembly by its name, as its dependencies list it, so it is the one the runtime binds the
    /// XAML's types to; from its path when they do not.</summary>
    private static Assembly ProjectAssembly(string path)
    {
        try
        {
            return Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(path)));
        }
        catch (FileNotFoundException)
        {
            return Assembly.LoadFrom(path);
        }
    }
}
