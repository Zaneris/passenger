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
/// window's x:Class with its .axaml file; and the markup's file renamed in the Solution view takes its class along the
/// same way. A component with no code-behind is renamed whole from its file, its uses in C# and its tags. The person's
/// Undo takes each rename back whole, and the project builds after every rename.</summary>
public sealed class CodeBehindRenameTests : McpRig
{
    private const string Greeting = "<p class=\"greeting\">Hello, @Name!</p>\n";

    private const string GreetingCode = "using Microsoft.AspNetCore.Components;\n\nnamespace Shop.Components;\n\npublic partial class Greeting\n{\n"
        + "    [Parameter]\n    public string Name { get; set; } = \"\";\n}\n";

    private const string GreetingStyle = ".greeting {\n    color: teal;\n}\n";

    private const string GreetingAlone = Greeting + "\n@code {\n    [Parameter]\n    public string Name { get; set; } = \"\";\n}\n";

    private const string Home = "@page \"/\"\n\n<Greeting Name=\"Ada\" />\n";

    private const string Registry = "namespace Shop.Components;\n\npublic static class Registry\n{\n    public static Type Shown => typeof(Greeting);\n}\n";

    private const string Window = "<Window xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        + "        x:Class=\"Gallery.MainWindow\"\n        Title=\"Gallery\">\n    <TextBlock x:Name=\"Heading\" />\n</Window>\n";

    private const string WindowCode = "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class MainWindow : Window\n{\n    public MainWindow()\n    {\n"
        + "        InitializeComponent();\n        Heading.Text = \"Hello\";\n    }\n}\n";

    private const string Card = "<UserControl xmlns=\"https://github.com/avaloniaui\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
        + "             x:Class=\"Gallery.Tile\" />\n";

    private const string CardCode = "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class Tile : UserControl\n{\n    public Tile()\n    {\n"
        + "        InitializeComponent();\n    }\n}\n";

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

    /// <summary>A component's .razor file renamed in the Solution view offers to rename its class with it, as a C# file named
    /// after its type does: taken, the code-behind's partial class, the tag and the typeof that use it follow, the
    /// code-behind and the scoped style are renamed with it, and the app builds; one Undo puts all of it back.</summary>
    [AvaloniaFact]
    public void Renaming_a_razor_components_file_in_the_solution_view_renames_its_class_with_its_uses_and_code_behind_and_one_undo_puts_it_back()
    {
        string folder = PathNames.Canonical(TestFolders.Create("razor-file-rename-"));
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");
        TestFolders.Write(folder, "_Imports.razor", "@using Microsoft.AspNetCore.Components\n@using Microsoft.AspNetCore.Components.Web\n@using Shop.Components\n");
        TestFolders.Write(folder, "Components/Greeting.razor", Greeting);
        TestFolders.Write(folder, "Components/Greeting.razor.cs", GreetingCode);
        TestFolders.Write(folder, "Components/Greeting.razor.css", GreetingStyle);
        TestFolders.Write(folder, "Components/Home.razor", Home);
        TestFolders.Write(folder, "Components/Registry.cs", Registry);
        Restore.Run(folder);
        string components = Path.Combine(folder, "Components");
        MainWindow window = Opened(folder, "Program.cs");
        List<string> asked = AnswerUndo(window);
        try
        {
            RenameInSolutionView(window, Path.Combine(components, "Greeting.razor"), "Salute.razor", "Greeting");
            Assert.True(Names(components).SequenceEqual(["Home.razor", "Registry.cs", "Salute.razor", "Salute.razor.cs", "Salute.razor.css"]),
                $"The component's files after Greeting.razor was renamed with its class in the Solution view: [{string.Join(", ", Names(components))}], {window.Status}");
            Assert.Equal(Greeting, File.ReadAllText(Path.Combine(components, "Salute.razor")));
            Assert.Equal(GreetingCode.Replace("class Greeting", "class Salute", StringComparison.Ordinal), File.ReadAllText(Path.Combine(components, "Salute.razor.cs")));
            Assert.Equal(GreetingStyle, File.ReadAllText(Path.Combine(components, "Salute.razor.css")));
            Assert.Equal(Home.Replace("<Greeting", "<Salute", StringComparison.Ordinal), File.ReadAllText(Path.Combine(components, "Home.razor")));
            Assert.Equal(Registry.Replace("typeof(Greeting)", "typeof(Salute)", StringComparison.Ordinal), File.ReadAllText(Path.Combine(components, "Registry.cs")));
            Builds(window, folder, "the component's file was renamed with its class in the Solution view");

            UndoInSolutionView(window, Path.Combine(components, "Salute.razor"));
            Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename Greeting to Salute? It changes ", StringComparison.Ordinal), $"The person's Undo asked: {string.Join(" | ", asked)}");
            Assert.True(Names(components).SequenceEqual(["Greeting.razor", "Greeting.razor.cs", "Greeting.razor.css", "Home.razor", "Registry.cs"]),
                $"One Undo did not put the component's files back: [{string.Join(", ", Names(components))}], {window.Status}");
            Assert.Equal(GreetingCode, File.ReadAllText(Path.Combine(components, "Greeting.razor.cs")));
            Assert.Equal(Home, File.ReadAllText(Path.Combine(components, "Home.razor")));
            Assert.Equal(Registry, File.ReadAllText(Path.Combine(components, "Registry.cs")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A component with no code-behind, used by a tag in another component and by typeof in C#, renamed from its
    /// .razor file in the Solution view, from the typeof and from the tag: each time its file and scoped style move, the tag
    /// and the typeof follow, and the app builds, and one Undo puts all of it back. An agent attached finds its usages by
    /// its name and renames it by its name the same way. The folder's _Imports.razor offers no class to rename.</summary>
    [AvaloniaFact]
    public void Renaming_a_razor_component_with_no_code_behind_from_its_file_its_typeof_or_its_tag_renames_it_whole_and_one_undo_puts_it_back()
    {
        string folder = PathNames.Canonical(TestFolders.Create("razor-alone-rename-"));
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Web("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(folder, "Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");
        TestFolders.Write(folder, "_Imports.razor", "@using Microsoft.AspNetCore.Components\n@using Microsoft.AspNetCore.Components.Web\n@using Shop.Components\n");
        TestFolders.Write(folder, "Components/Greeting.razor", GreetingAlone);
        TestFolders.Write(folder, "Components/Greeting.razor.css", GreetingStyle);
        TestFolders.Write(folder, "Components/Home.razor", Home);
        TestFolders.Write(folder, "Components/Registry.cs", Registry);
        Restore.Run(folder);
        string components = Path.Combine(folder, "Components");
        MainWindow window = Opened(folder, "Components/Registry.cs");
        List<string> asked = AnswerUndo(window);
        McpClient? client = null;
        try
        {
            Choose(window, Path.Combine(folder, "_Imports.razor"));
            Ui.Run(window, AppActions.Rename);
            NamePopup imports = Assert.IsType<NamePopup>(window.Popup);
            Assert.True(imports.Option is null, $"Renaming _Imports.razor, which makes no component, offered: {imports.Option?.Content}");
            Ui.Press(window, "ESCAPE");
            Ui.WaitFor(() => !window.IsPopupOpen, "the rename popup to close");

            RenameInSolutionView(window, Path.Combine(components, "Greeting.razor"), "Salute.razor", "Greeting");
            RenamedWhole(window, folder, "Salute", "Greeting.razor was renamed with its class in the Solution view");
            UndoInSolutionView(window, Path.Combine(components, "Salute.razor"));
            PutBack(window, folder, asked, 1, "the Solution view's rename of Greeting.razor");

            window.OpenFile(Path.Combine(components, "Registry.cs"));
            Ui.Settle();
            RenameAt(window, "typeof(Gree", "Salute", offersFiles: false);
            RenamedWhole(window, folder, "Salute", "the component was renamed from its typeof");
            UndoInEditor(window);
            PutBack(window, folder, asked, 2, "the rename from the typeof");

            window.OpenFile(Path.Combine(components, "Home.razor"));
            Ui.Settle();
            RenameAt(window, "<Gree", "Salute", offersFiles: false);
            RenamedWhole(window, folder, "Salute", "the component was renamed from its tag");
            UndoInEditor(window);
            PutBack(window, folder, asked, 3, "the rename from the tag");
            window.EditorArea.SaveAll();

            client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            JsonElement uses = Await(Waiting.Apart(() => Call(client, McpTools.Usages, new() { ["symbol"] = "Greeting" })), "the agent's usages of the component");
            string[] found = [.. uses.GetProperty("files").EnumerateArray()
                .SelectMany(f => f.GetProperty("places").EnumerateArray().Select(p => $"{f.GetProperty("path").GetString()}:{p.GetString()!.Split(' ')[0]}"))];
            string[] expected = [Path.Combine("Components", "Home.razor") + ":3:2", Path.Combine("Components", "Registry.cs") + ":5:40"];
            Assert.True(expected.All(found.Contains), $"The agent's usages of the component by its name: {Text(uses)}");
            string said = Text(Allowed(window, client, McpTools.Rename, new() { ["symbol"] = "Greeting", ["newName"] = "Welcome" }));
            Assert.True(said.Contains($"{Path.Combine("Components", "Greeting.razor")} to {Path.Combine("Components", "Welcome.razor")}", StringComparison.Ordinal),
                $"The agent's rename of the component by its name answered: {said}");
            RenamedWhole(window, folder, "Welcome", "the agent renamed the component by its name");
        }
        finally
        {
            Leave(client, window);
        }
    }

    /// <summary>The component named Greeting with no code-behind renamed to <paramref name="name"/> whole: its file and scoped
    /// style moved as they were, the tag and the typeof that use it renamed, and the app building.</summary>
    private static void RenamedWhole(MainWindow window, string folder, string name, string after)
    {
        string components = Path.Combine(folder, "Components");
        string[] files = [.. new[] { "Home.razor", "Registry.cs", $"{name}.razor", $"{name}.razor.css" }.Order(StringComparer.Ordinal)];
        Assert.True(Names(components).SequenceEqual(files), $"The component's files after {after}: [{string.Join(", ", Names(components))}], {window.Status}");
        Assert.Equal(GreetingAlone, File.ReadAllText(Path.Combine(components, $"{name}.razor")));
        Assert.Equal(GreetingStyle, File.ReadAllText(Path.Combine(components, $"{name}.razor.css")));
        Assert.Equal(Home.Replace("<Greeting", $"<{name}", StringComparison.Ordinal), Shown(window, Path.Combine(components, "Home.razor")));
        Assert.Equal(Registry.Replace("typeof(Greeting)", $"typeof({name})", StringComparison.Ordinal), Shown(window, Path.Combine(components, "Registry.cs")));
        Builds(window, folder, after);
    }

    /// <summary>One Undo of a rename of the component named Greeting with no code-behind, the person's <paramref name="undone"/>th,
    /// asked about and putting its files, its tag and its typeof back as they were.</summary>
    private static void PutBack(MainWindow window, string folder, List<string> asked, int undone, string rename)
    {
        string components = Path.Combine(folder, "Components");
        Assert.True(asked.Count == undone && asked[^1].StartsWith("Undo Rename Greeting to Salute? It changes ", StringComparison.Ordinal),
            $"The person's Undo of {rename} asked: {string.Join(" | ", asked)}");
        Assert.True(Names(components).SequenceEqual(["Greeting.razor", "Greeting.razor.css", "Home.razor", "Registry.cs"]),
            $"One Undo of {rename} did not put the component's files back: [{string.Join(", ", Names(components))}], {window.Status}");
        Assert.Equal(GreetingAlone, File.ReadAllText(Path.Combine(components, "Greeting.razor")));
        Assert.Equal(Home, Shown(window, Path.Combine(components, "Home.razor")));
        Assert.Equal(Registry, Shown(window, Path.Combine(components, "Registry.cs")));
    }

    /// <summary>A file's text as the window shows it: its editor's where it is open, else the disk's.</summary>
    private static string Shown(MainWindow window, string path) =>
        window.EditorArea.Find(path)?.Text.Snapshot.ToString() ?? File.ReadAllText(path);

    /// <summary>A window's .axaml file renamed in the Solution view offers to rename its class, which its x:Class names after
    /// the file: taken, the x:Class, the code-behind's class and file and the code that makes the window follow, and the
    /// app builds; one Undo puts all of it back. A XAML file whose class is named otherwise offers nothing.</summary>
    [AvaloniaFact]
    public void Renaming_an_avalonia_windows_axaml_in_the_solution_view_offers_to_rename_its_class_and_one_undo_puts_it_back()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-file-rename-"));
        TestFolders.Write(folder, "Gallery.csproj", AvaloniaApp.HeadlessProject());
        TestFolders.Write(folder, "Program.cs", "namespace Gallery;\n\npublic static class Program\n{\n    public static void Main()\n    {\n    }\n}\n");
        TestFolders.Write(folder, "App.axaml", App);
        TestFolders.Write(folder, "App.axaml.cs", AppCode);
        TestFolders.Write(folder, "MainWindow.axaml", Window);
        TestFolders.Write(folder, "MainWindow.axaml.cs", WindowCode);
        TestFolders.Write(folder, "Card.axaml", Card);
        TestFolders.Write(folder, "Card.axaml.cs", CardCode);
        Restore.Run(folder);
        MainWindow window = Opened(folder, "Program.cs");
        List<string> asked = AnswerUndo(window);
        try
        {
            Choose(window, Path.Combine(folder, "Card.axaml"));
            Ui.Run(window, AppActions.Rename);
            NamePopup other = Assert.IsType<NamePopup>(window.Popup);
            Assert.True(other.Option is null, $"Renaming Card.axaml, whose class is Tile, offered: {other.Option?.Content}");
            Ui.Press(window, "ESCAPE");
            Ui.WaitFor(() => !window.IsPopupOpen, "the rename popup to close");

            RenameInSolutionView(window, Path.Combine(folder, "MainWindow.axaml"), "MainView.axaml", "MainWindow");
            Assert.True(Names(folder, "*.axaml*").SequenceEqual(["App.axaml", "App.axaml.cs", "Card.axaml", "Card.axaml.cs", "MainView.axaml", "MainView.axaml.cs"]),
                $"The window's files after MainWindow.axaml was renamed with its class in the Solution view: [{string.Join(", ", Names(folder, "*.axaml*"))}], {window.Status}");
            Assert.Equal(Window.Replace("Gallery.MainWindow", "Gallery.MainView", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "MainView.axaml")));
            Assert.Equal(WindowCode.Replace("MainWindow", "MainView", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "MainView.axaml.cs")));
            Assert.Equal(AppCode.Replace("new MainWindow()", "new MainView()", StringComparison.Ordinal), File.ReadAllText(Path.Combine(folder, "App.axaml.cs")));
            Builds(window, folder, "the window's .axaml was renamed with its class in the Solution view");

            UndoInSolutionView(window, Path.Combine(folder, "MainView.axaml"));
            Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename MainWindow to MainView? It changes ", StringComparison.Ordinal), $"The person's Undo asked: {string.Join(" | ", asked)}");
            Assert.True(Names(folder, "*.axaml*").SequenceEqual(["App.axaml", "App.axaml.cs", "Card.axaml", "Card.axaml.cs", "MainWindow.axaml", "MainWindow.axaml.cs"]),
                $"One Undo did not put the window's files back: [{string.Join(", ", Names(folder, "*.axaml*"))}], {window.Status}");
            Assert.Equal(Window, File.ReadAllText(Path.Combine(folder, "MainWindow.axaml")));
            Assert.Equal(WindowCode, File.ReadAllText(Path.Combine(folder, "MainWindow.axaml.cs")));
            Assert.Equal(AppCode, File.ReadAllText(Path.Combine(folder, "App.axaml.cs")));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rename on a file's row in the Solution view, the type it is named after offered with it and left checked,
    /// the new name typed and Enter pressed, until the popup closes.</summary>
    private static void RenameInSolutionView(MainWindow window, string path, string name, string type)
    {
        Choose(window, path);
        Ui.Run(window, AppActions.Rename);
        NamePopup popup = Assert.IsType<NamePopup>(window.Popup);
        Assert.True(popup.Option is { IsChecked: true } option && (string?)option.Content == $"Rename type '{type}' to match the new file name",
            $"Renaming {Path.GetFileName(path)} in the Solution view did not offer to rename its class {type} with it: {popup.Option?.Content}");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => !window.IsPopupOpen, $"the rename to finish: {popup.Problem}");
        Ui.Settle();
    }

    /// <summary>The person's Undo with a file's row chosen in the Solution view.</summary>
    private static void UndoInSolutionView(MainWindow window, string path)
    {
        Choose(window, path);
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
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

    /// <summary>Rename at the end of a piece of the active file, the new name typed into its box once it is open (and has
    /// offered the files named after the type, when <paramref name="offersFiles"/>), and Enter pressed.</summary>
    private static void RenameAt(MainWindow window, string after, string name, bool offersFiles = true)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => offersFiles ? window.Assist.RenameOffersFiles.Count > 0 : window.Assist.Renaming is not null,
            offersFiles ? "the rename box to offer the files named after the class" : "the rename box to open");
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
