using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A folder whose global.json asks for a .NET SDK that is not installed says so where a person looks: the
/// status bar, a click there downloading it, over the language server's failure to load the projects; each project in
/// the Solution view, its tip giving dotnet's words; and Run's refusal, in place of "no executable project", until
/// global.json asks for an SDK there is. New Solution beside it gives dotnet's words whole as it refuses.</summary>
public sealed class MissingSdkTests : IDisposable
{
    /// <summary>A global.json asking for an SDK no machine has, and no other.</summary>
    internal const string Pinned = "{ \"sdk\": { \"version\": \"1.0.100\", \"rollForward\": \"disable\" } }\n";

    private readonly string root = PathNames.Canonical(TestFolders.Create("missing-sdk-"));
    private readonly string settings = TestFolders.Create("settings-");

    public MissingSdkTests()
    {
        TestFolders.Write(root, "Shop/Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        TestFolders.Write(root, "Shop/App/App.csproj", TestProjects.Console());
        TestFolders.Write(root, "Shop/App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
    }

    public void Dispose()
    {
        TestFolders.Delete(root);
        TestFolders.Delete(settings);
    }

    /// <summary>The words the want of the SDK is said in, where the folder open has the global.json.</summary>
    private const string Missing = "The .NET SDK 1.0.100 that global.json asks for is not installed: install it, or have global.json name one that is";

    [AvaloniaFact]
    public void A_folder_whose_global_json_asks_for_an_SDK_not_installed_says_so_in_the_status_bar_the_Solution_view_and_Runs_refusal_until_it_asks_for_one_there_is()
    {
        TestFolders.Write(root, "Shop/global.json", Pinned);
        MainWindow window = Ui.OpenFolder(Path.Combine(root, "Shop", "Shop.slnx"));
        Ui.WaitFor(() => window.Session.CSharp is { IsReady: true }, "the C# language server to have tried to load App", TimeSpan.FromMinutes(2));
        Ui.Settle();
        Assert.True(window.Status == Missing && window.StatusSeverity == ProblemSeverity.Error,
            $"Without the SDK global.json asks for, the status bar said ({window.StatusSeverity}): {window.Status}");
        Assert.Equal("download the .NET SDK 1.0.100", window.StatusPlace?.Name);
        Ui.Click(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StatusMessage"));
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet/1.0", Assert.Single(window.Browsed).AbsoluteUri);

        ExplorerNode app = window.ExplorerView.Tree.Rows.Single(r => r.Kind == NodeKind.Project && r.Name == "App");
        Assert.Equal("(no SDK)", app.Detail);
        Assert.True(app.Tip?.StartsWith(Missing, StringComparison.Ordinal) == true && app.Tip.Contains("Install the [1.0.100] .NET SDK", StringComparison.Ordinal),
            $"App's row, which no SDK evaluates, has the tip: {app.Tip}");

        Assert.Equal(Missing, window.Actions.WhyNot(AppActions.RunProgram, window.Actions.Context()));
        Ui.Run(window, AppActions.RunProgram);
        Assert.True(window.Status == Missing && !window.Session.RunProgram.IsRunning, $"Run without the SDK said: {window.Status}");

        // A global.json that asks for no SDK of its own has the newest installed evaluate the project.
        TestFolders.Write(root, "Shop/global.json", "{}\n");
        Ui.WaitFor(() => window.Session.Models.MissingSdk is null && window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project && r.Detail == TestProjects.Framework),
            () => $"App to be evaluated once global.json asks for no SDK it lacks: App's row says {window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == "App")?.Detail}", TimeSpan.FromMinutes(1));
        Assert.NotEqual(Missing, window.Status);
        Assert.Null(window.Actions.WhyNot(AppActions.RunProgram, window.Actions.Context()));
        window.Close();
    }

    [AvaloniaFact]
    public void New_Solution_where_global_json_asks_for_an_SDK_not_installed_refuses_with_dotnets_words_whole()
    {
        // Over the folder the solution is in, where New Solution puts the next one.
        TestFolders.Write(root, "global.json", Pinned);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(Path.Combine(root, "Shop", "Shop.slnx"));
        Ui.Settle();
        window.Perform(AppActions.NewSolution);
        Ui.Settle();
        NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>());
        Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "the templates' read to end", TimeSpan.FromMinutes(1));
        string said = dialog.Problem ?? string.Empty;
        Assert.True(said.StartsWith("The templates could not be read: dotnet --version failed: ", StringComparison.Ordinal)
            && said.Contains("Install the [1.0.100] .NET SDK or update [", StringComparison.Ordinal)
            && said.EndsWith("Learn about SDK resolution: https://aka.ms/dotnet/sdk-not-found", StringComparison.Ordinal),
            $"New Solution under a global.json asking for an SDK not installed refused with: {said}");
        dialog.Close();
        window.Close();
    }
}
