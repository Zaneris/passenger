using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Mcp;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A class renamed from its code-behind takes the markup it is made with along, in the window and for an agent
/// attached to it: a Razor component's .razor file and its scoped style, which Razor names the class after, and an Avalonia
/// window's x:Class with its .axaml file; the person's Undo takes each rename back whole, and the project builds after
/// every rename.</summary>
public sealed class CodeBehindRenameTests : McpRig
{
    private const string Greeting = "<p class=\"greeting\">Hello, @Name!</p>\n";

    private const string GreetingCode = "using Microsoft.AspNetCore.Components;\n\nnamespace Shop.Components;\n\npublic partial class Greeting\n{\n"
        + "    [Parameter]\n    public string Name { get; set; } = \"\";\n}\n";

    private const string GreetingStyle = ".greeting {\n    color: teal;\n}\n";

    private const string Home = "@page \"/\"\n\n<Greeting Name=\"Ada\" />\n";

    private const string Window = "<Window xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        + "        x:Class=\"Gallery.MainWindow\"\n        Title=\"Gallery\">\n    <TextBlock x:Name=\"Heading\" />\n</Window>\n";

    private const string WindowCode = "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class MainWindow : Window\n{\n    public MainWindow()\n    {\n"
        + "        InitializeComponent();\n        Heading.Text = \"Hello\";\n    }\n}\n";

    private const string App = "<Application xmlns=\"https://github.com/avaloniaui\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        + "             x:Class=\"Gallery.App\">\n    <Application.Styles>\n        <FluentTheme />\n    </Application.Styles>\n</Application>\n";

    private const string AppCode = "using Avalonia;\nusing Avalonia.Controls;\nusing Avalonia.Markup.Xaml;\n\nnamespace Gallery;\n\npublic partial class App : Application\n{\n"
        + "    public override void Initialize() => AvaloniaXamlLoader.Load(this);\n\n    public static Window Open() => new MainWindow();\n}\n";

    /// <summary>A component's class renamed from its code-behind in the window moves its .razor file and scoped style with
    /// it and changes the tag that uses it, and the app builds; the person's Undo puts all of it back at once; an agent's
    /// rename does the same, saying each file it moved; and so does the code-behind's file renamed with its class in the
    /// Solution view, the app building after each.</summary>
    [AvaloniaFact]
    public void Renaming_a_razor_components_class_from_its_code_behind_moves_its_razor_file_and_scoped_style_with_it_in_the_window_and_for_an_agent()
    {
        string folder = PathNames.Canonical(TestFolders.Create("razor-rename-"));
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");
        TestFolders.Write(folder, "_Imports.razor", "@using Microsoft.AspNetCore.Components\n@using Microsoft.AspNetCore.Components.Web\n@using Shop.Components\n");
        TestFolders.Write(folder, "Components/Greeting.razor", Greeting);
        TestFolders.Write(folder, "Components/Greeting.razor.cs", GreetingCode);
        TestFolders.Write(folder, "Components/Greeting.razor.css", GreetingStyle);
        TestFolders.Write(folder, "Components/Home.razor", Home);
        Restore.Run(folder);
        string components = Path.Combine(folder, "Components");
        MainWindow window = Opened(folder, "Components/Greeting.razor.cs");
        List<string> asked = AnswerUndo(window);
        McpClient? client = null;
        try
        {
            RenameAt(window, "class Gree", "Salute");
            Assert.True(Names(components).SequenceEqual(["Home.razor", "Salute.razor", "Salute.razor.cs", "Salute.razor.css"]),
                $"The component's files after its class was renamed from its code-behind: [{string.Join(", ", Names(components))}], {window.Status}");
            Assert.Equal(Greeting, File.ReadAllText(Path.Combine(components, "Salute.razor")));
            Assert.Equal(GreetingStyle, File.ReadAllText(Path.Combine(components, "Salute.razor.css")));
            Assert.Equal(Home.Replace("<Greeting", "<Salute", StringComparison.Ordinal), File.ReadAllText(Path.Combine(components, "Home.razor")));
            Assert.Equal("Salute.razor.cs", window.EditorArea.Active?.Name);
            Builds(window, folder, "the component's class was renamed in the window");

            UndoInEditor(window);
            Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename Greeting to Salute? It changes ", StringComparison.Ordinal), $"The person's Undo asked: {string.Join(" | ", asked)}");
            Assert.True(Names(components).SequenceEqual(["Greeting.razor", "Greeting.razor.cs", "Greeting.razor.css", "Home.razor"]),
                $"Undo did not put the component's files back: [{string.Join(", ", Names(components))}], {window.Status}");
            Assert.Equal(Home, File.ReadAllText(Path.Combine(components, "Home.razor")));
            Assert.Equal(GreetingCode, window.EditorArea.Active?.Text.Snapshot.ToString());
            window.EditorArea.SaveAll();

            client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            string said = Text(Allowed(window, client, McpTools.Rename, At(GreetingCode, "Components/Greeting.razor.cs", "class Gree", "Welcome")));
            string moved = string.Join(", ", new[] { "Greeting.razor.cs", "Greeting.razor", "Greeting.razor.css" }
                .Select(f => $"{Path.Combine("Components", f)} to {Path.Combine("Components", f.Replace("Greeting", "Welcome", StringComparison.Ordinal))}"));
            Assert.True(said.EndsWith($"; renamed {moved}", StringComparison.Ordinal), $"The agent's rename answered: {said}");
            Assert.True(Names(components).SequenceEqual(["Home.razor", "Welcome.razor", "Welcome.razor.cs", "Welcome.razor.css"]),
                $"The component's files after the agent renamed its class: [{string.Join(", ", Names(components))}]");
            Assert.Equal(Home.Replace("<Greeting", "<Welcome", StringComparison.Ordinal), File.ReadAllText(Path.Combine(components, "Home.razor")));
            Builds(window, folder, "the agent renamed the component's class");

            Choose(window, Path.Combine(components, "Welcome.razor.cs"));
            Ui.Run(window, AppActions.Rename);
            NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
            Assert.True(popup.Option?.IsChecked == true, "Renaming Welcome.razor.cs in the Solution view did not offer to rename the class Welcome with it");
            window.KeyTextInput("Greeting.razor.cs");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
            Assert.True(Names(components).SequenceEqual(["Greeting.razor", "Greeting.razor.cs", "Greeting.razor.css", "Home.razor"]),
                $"The component's files after its code-behind was renamed with its class in the Solution view: [{string.Join(", ", Names(components))}], {window.Status}");
            Assert.Equal(Home, File.ReadAllText(Path.Combine(components, "Home.razor")));
            Builds(window, folder, "the code-behind was renamed with its class in the Solution view");
        }
        finally
        {
            Leave(client, window);
        }
    }

    /// <summary>A window's class renamed from its code-behind in the window changes the markup's x:Class and renames the
    /// .axaml file with the code-behind, and the app builds; the person's Undo puts all of it back at once; then an agent's
    /// rename with renameFiles false changes the x:Class alone of the markup, the files keeping their names, and the app
    /// builds again.</summary>
    [AvaloniaFact]
    public void Renaming_an_avalonia_windows_class_from_its_code_behind_changes_its_x_class_and_renames_its_axaml_in_the_window_and_for_an_agent()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-rename-"));
        TestFolders.Write(folder, "Gallery.csproj", AvaloniaApp.HeadlessProject());
        TestFolders.Write(folder, "Program.cs", "namespace Gallery;\n\npublic static class Program\n{\n    public static void Main()\n    {\n    }\n}\n");
        TestFolders.Write(folder, "App.axaml", App);
        TestFolders.Write(folder, "App.axaml.cs", AppCode);
        TestFolders.Write(folder, "MainWindow.axaml", Window);
        TestFolders.Write(folder, "MainWindow.axaml.cs", WindowCode);
        Restore.Run(folder);
        MainWindow window = Opened(folder, "MainWindow.axaml.cs");
        List<string> asked = AnswerUndo(window);
        McpClient? client = null;
        try
        {
            RenameAt(window, "class MainWin", "MainView");
            Assert.True(Names(folder, "*.axaml*").SequenceEqual(["App.axaml", "App.axaml.cs", "MainView.axaml", "MainView.axaml.cs"]),
                $"The window's files after its class was renamed from its code-behind: [{string.Join(", ", Names(folder, "*.axaml*"))}], {window.Status}");
            Assert.Equal(Window.Replace("Gallery.MainWindow", "Gallery.MainView", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "MainView.axaml")));
            Assert.Equal(AppCode.Replace("new MainWindow()", "new MainView()", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "App.axaml.cs")));
            Builds(window, folder, "the window's class was renamed in the window");

            UndoInEditor(window);
            Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename MainWindow to MainView? It changes ", StringComparison.Ordinal), $"The person's Undo asked: {string.Join(" | ", asked)}");
            Assert.True(Names(folder, "*.axaml*").SequenceEqual(["App.axaml", "App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs"]),
                $"Undo did not put the window's files back: [{string.Join(", ", Names(folder, "*.axaml*"))}], {window.Status}");
            Assert.Equal(Window, File.ReadAllText(Path.Combine(folder, "MainWindow.axaml")));
            Assert.Equal(AppCode, File.ReadAllText(Path.Combine(folder, "App.axaml.cs")));
            window.EditorArea.SaveAll();

            client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            Dictionary<string, object?> arguments = At(WindowCode, "MainWindow.axaml.cs", "class MainWin", "Shell");
            arguments["renameFiles"] = false;
            string said = Text(Allowed(window, client, McpTools.Rename, arguments));
            const string Changed = "Renamed in 3 files: ";
            Assert.True(said.StartsWith(Changed, StringComparison.Ordinal)
                && said[Changed.Length..].Split(", ").Order(StringComparer.Ordinal).SequenceEqual(["App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs"]),
                $"The agent's rename with renameFiles false did not say it changed the code-behind, its use and the markup, and moved nothing: {said}");
            Assert.True(Names(folder, "*.axaml*").SequenceEqual(["App.axaml", "App.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs"]),
                $"A rename with renameFiles false renamed files: [{string.Join(", ", Names(folder, "*.axaml*"))}]");
            Assert.Equal(Window.Replace("Gallery.MainWindow", "Gallery.Shell", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "MainWindow.axaml")));
            Assert.Contains("public partial class Shell : Window", File.ReadAllText(Path.Combine(folder, "MainWindow.axaml.cs")), StringComparison.Ordinal);
            Builds(window, folder, "the agent renamed the window's class with renameFiles false");
        }
        finally
        {
            Leave(client, window);
        }
    }

    /// <summary>A window over the folder that lets agents in, a file of it open, once the C# language server has loaded it.</summary>
    private static MainWindow Opened(string folder, string file)
    {
        MainWindow window = AgentsWindow(folder);
        window.OpenFile(Path.Combine(folder, file));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        return window;
    }

    /// <summary>Rename at the end of a piece of the active file, the new name typed into its box once it has offered the
    /// files named after the type, and Enter pressed.</summary>
    private static void RenameAt(MainWindow window, string after, string name)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.RenameOffersFiles.Count > 0, "the rename box to offer the files named after the class");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Ui.Settle();
    }

    /// <summary>Clicks a file's row in the Solution view, its folders opened down to it, as a person chooses it before a key.</summary>
    private static void Choose(MainWindow window, string path)
    {
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        ExplorerNode row = window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Path == path)
            ?? throw new Xunit.Sdk.XunitException($"The Solution view does not show {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
        Ui.ClickRow(window, row);
    }

    /// <summary>The person's Undo in the editor of the file the rename was made in.</summary>
    private static void UndoInEditor(MainWindow window)
    {
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
    }

    /// <summary>Agrees to every Undo the person is asked about, keeping each question's text.</summary>
    private static List<string> AnswerUndo(MainWindow window)
    {
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        return asked;
    }

    /// <summary>An agent's call the person allows when the window asks them first; its answer.</summary>
    private JsonElement Allowed(MainWindow window, McpClient client, [IdentifierOf(typeof(McpTools))] string tool, Dictionary<string, object?> arguments)
    {
        Task<JsonElement> calling = Waiting.Apart(() => Call(client, tool, arguments));
        Press(Question(window, $"the question for the agent's {tool}"), "Allow");
        return Await(calling, $"the agent's {tool}");
    }

    /// <summary>A rename's arguments for the place at the end of a piece of a file's text.</summary>
    private static Dictionary<string, object?> At(string text, string path, string after, string name)
    {
        int at = text.IndexOf(after, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{path} has no {after}");
        at += after.Length;
        int line = text[..at].Count(c => c == '\n');
        return new() { ["path"] = path, ["line"] = line + 1, ["column"] = at - text.LastIndexOf('\n', at - 1), ["newName"] = name };
    }

    /// <summary>The names of a folder's files, in order.</summary>
    private static string[] Names(string folder, string pattern = "*") =>
        [.. Directory.GetFiles(folder, pattern).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>The open files saved and the project built with the .NET command line, as a person would check it.</summary>
    private static void Builds(MainWindow window, string folder, string after)
    {
        window.EditorArea.SaveAll();
        ChildResult built = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", folder, "--no-restore", "-nologo", "-v:q"]), limit: Restore.Limit.Scaled())),
            "the project to build", Restore.Limit.Scaled() + TestFolders.Spare);
        Assert.True(built.Succeeded, $"The project does not build after {after}:\n{built.Output}{built.Error}");
    }

    /// <summary>The agent goes, if it came, then the window closes.</summary>
    private void Leave(McpClient? client, MainWindow window)
    {
        if (client is not null)
        {
            Gone(client);
        }
        window.Close();
    }
}
