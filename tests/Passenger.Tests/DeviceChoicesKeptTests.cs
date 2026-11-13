using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run.Devices;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Where a project was chosen to run is the folder's: it outlasts the window's own rewrites of the folder's state
/// and a window opening the folder again finds it.</summary>
public sealed class DeviceChoicesKeptTests
{
    [AvaloniaFact]
    public void A_device_chosen_for_a_project_outlasts_the_folders_state_written_again_and_is_found_by_the_next_window()
    {
        string folder = TestFolders.Create("device-kept-");
        string settings = TestFolders.Create("device-settings-");
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Hello/Program.cs", "System.Console.WriteLine(1);\n");
        try
        {
            MainWindow first = Keeping(settings, folder);
            Ui.WaitFor(() => first.Session.Projects.Count == 1, "the folder's project evaluated");
            ProjectModel project = first.Session.Projects.Single();
            first.Session.Targets.Choose(project, new DeviceTarget(TargetPlatform.Android, "emulator-5554", "Pixel 9", TargetKind.Emulator, true) { Avd = "Pixel_9" });
            // Opening a file writes the folder's state again whole.
            first.OpenFile(Path.Combine(folder, "Hello", "Program.cs"));
            Ui.Settle();
            first.Close();

            MainWindow second = Keeping(settings, folder);
            try
            {
                Ui.WaitFor(() => second.Session.Projects.Count == 1, "the folder's project evaluated again");
                ProjectModel again = second.Session.Projects.Single();
                Assert.True(second.Session.Targets.Of(again)?.Key == "Pixel_9",
                    $"The folder opened again with {second.Session.Targets.Of(again)?.Name ?? "no device"} chosen for Hello, not the Pixel 9 chosen before: the window's rewrite of its state dropped the choice");
            }
            finally
            {
                second.Close();
            }
        }
        finally
        {
            TestFolders.Delete(settings);
        }
    }

    /// <summary>A window that keeps its state in a folder of its own, on the folder given.</summary>
    private static MainWindow Keeping(string settings, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }
}
