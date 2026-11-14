using Avalonia;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A view of Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>, its desktop head and shared
/// library as the template makes them), whose XAML lives in a library with no runtime configuration of its own, previews
/// as an app's does: drawn in the desktop head that runs it. The preview says to build that head, Build for XAML Preview
/// builds it, and the view is drawn at its design size, then at a phone's.</summary>
public sealed class XamlLibraryPreviewTests
{
    [AvaloniaFact]
    public void A_shared_librarys_view_previews_in_the_desktop_head_that_runs_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("avalonia-xplat-preview"));
        Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateCrossPlatformAsync(folder, "XApp")), "dotnet new avalonia.xplat", TimeSpan.FromMinutes(6));
        // The device and browser heads need workloads a test machine may lack; the desktop head and the library are the template's own.
        foreach (string head in (string[])["XApp.Android", "XApp.iOS", "XApp.Browser"])
        {
            Directory.Delete(Path.Combine(folder, head), recursive: true);
        }
        File.WriteAllText(Path.Combine(folder, "XApp.slnx"), "<Solution>\n  <Project Path=\"XApp.Desktop/XApp.Desktop.csproj\" />\n  <Project Path=\"XApp/XApp.csproj\" />\n</Solution>\n");
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900, languageServer: false);
        try
        {
            string view = Path.Combine(folder, "XApp", "Views", "MainView.axaml");
            window.OpenFile(view);
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
            Ui.Settle();
            Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
            Ui.WaitFor(() => preview.WantsBuild, () => $"the preview to ask for a build; it says {preview.Status}", TimeSpan.FromMinutes(2));
            Assert.StartsWith("Build XApp.Desktop, which runs XApp, to preview its XAML", preview.Status, StringComparison.Ordinal);

            Assert.True(window.Perform(AppActions.BuildForPreview), $"Build for XAML Preview could not run: {window.Status}");
            Ui.WaitFor(() => XamlTests.DrawnAt(preview, new Size(800, 450)), () => $"the view drawn at its design size in the desktop head's build; it is {preview.FrameSize}, "
                + $"{preview.Frame?.PixelSize} pixels: {preview.Status} {preview.Error}", TimeSpan.FromMinutes(4));
            Assert.Null(preview.Error);

            Passenger.App.Xaml.XamlPreviewPane pane = window.PreviewPaneOf(document)!;
            Assert.True(pane.Sizes.IsVisible, "A shared view's preview offers no phone or tablet size");
            pane.Sizes.SelectedIndex = 1;
            Ui.WaitFor(() => XamlTests.DrawnAt(preview, new Size(412, 915)) && preview.Error is null, () => $"the view drawn at a phone's size; it is {preview.FrameSize}, "
                + $"{preview.Frame?.PixelSize} pixels: {preview.Status} {preview.Error}", TimeSpan.FromMinutes(1));
            Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        }
        finally
        {
            window.Close();
        }
    }
}
