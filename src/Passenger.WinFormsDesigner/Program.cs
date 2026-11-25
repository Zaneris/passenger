using System.ComponentModel;
using System.ComponentModel.Design;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>The designer's start: Windows Forms set up as an app's ApplicationConfiguration sets it (visual styles, text
/// rendering) at a pixel a unit, the project's assembly loaded where it was built (its runtime configuration and dependencies
/// are the ones the designer runs with), and requests read on a thread of their own until Passenger closes them, the newest
/// drawn on this thread, which Windows Forms' design surface wants for its own. Standard output is the protocol's alone.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        (TextWriter protocol, TextReader input) = PreviewWire.Start();
        Assembly? project = null;
        if (args is ["--assembly", { } path])
        {
            try
            {
                project = ProjectAssembly(path);
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException)
            {
                Console.Error.WriteLine($"The project's assembly {path} did not load: {error.Message}");
                return 3;
            }
        }
        else if (args.Length > 0)
        {
            Console.Error.WriteLine("Usage: Passenger.WinFormsDesigner [--assembly PROJECT.dll]");
            return 2;
        }
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        LicenseManager.CurrentContext = new DesigntimeLicenseContext();
        PreviewRequests<FormDesignRequest> requests = new();
        Thread reading = new(() => requests.Read(input)) { IsBackground = true, Name = "Passenger designer requests" };
        reading.Start();
        using Designer designer = new(project);
        while (requests.Next() is { } request)
        {
            FormDesignWire.Answer(protocol, designer.Draw(request));
        }
        return 0;
    }

    /// <summary>The project's assembly by its name, as its dependencies list it, so it is the one the runtime binds its types
    /// to; from its path when they do not.</summary>
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
