using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Find in Files' scope and file mask: the solution, one project (a project nested in its folder left to
/// itself), a directory with or without its subdirectories, or a named scope; a mask of wildcards with commas, a !
/// leaving files out; the Solution view's Edit menu opening Find and Replace in Files on a row's folder, project or
/// file; and the scope and mask coming back the next time, and after a restart.</summary>
public sealed class FindInFilesScopeTests : IClassFixture<FindInFilesScopeTests.Shop>, IDisposable
{
    private readonly Shop shop;
    private readonly string folder;
    private readonly string settings = TestFolders.Create("settings-");

    public FindInFilesScopeTests(Shop shop)
    {
        this.shop = shop;
        shop.Reset();
        folder = shop.Folder;
    }

    public void Dispose() => TestFolders.Delete(settings);

    [AvaloniaFact]
    public void Changing_the_scope_cannot_replace_files_from_the_previous_search_before_the_next_search_finishes()
    {
        MainWindow window = Open();
        try
        {
            window.OpenFile(Full("App/Main.cs"));
            Ui.Settle();
            int confirmations = 0;
            window.Confirm = (_, _, _) =>
            {
                confirmations++;
                return Task.FromResult(true);
            };
            Ui.Press(window, Ui.Shortcut(AppActions.ReplaceInFiles));
            FindInFilesPopup popup = Assert.IsType<FindInFilesPopup>(window.Popup);
            Search(popup, "total");
            Assert.True(popup.Matches.Select(m => m.Path).Distinct().Count() > 1);
            string outside = File.ReadAllText(Full("Lib/Money.cs"));
            popup.Replacement = "sum";
            popup.ScopeKind = SearchScopeKind.Scope;
            popup.NamedScope = "Current File";
            popup.RequestReplace();
            Assert.Equal(0, confirmations);
            Assert.Equal(outside, File.ReadAllText(Full("Lib/Money.cs")));
            Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the current-file replacement");
            Assert.Equal("Replaced 2 matches in 1 file", window.Status);
            Assert.Equal(1, confirmations);
            Assert.Equal(outside, File.ReadAllText(Full("Lib/Money.cs")));
            Assert.Contains("sum", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_search_in_one_project_finds_only_its_files_and_leaves_a_project_nested_in_it_to_itself()
    {
        MainWindow window = Open();
        window.OpenFile(Full("App/Main.cs"));
        Ui.Settle();
        FindInFilesPopup popup = Find(window);
        Search(popup, "total");
        Assert.Equal(["Line.cs", "Main.cs", "Money.cs", "Money.g.cs", "notes.md", "Order.cs", "Plugin.cs", "total.md"], Found(popup));

        Ui.Press(window, "alt P");
        Assert.Equal((SearchScopeKind.Project, Full("App/App.csproj")), (popup.ScopeKind, popup.Project));
        string[] app = Found(popup);
        Assert.True(app.SequenceEqual(["Line.cs", "Main.cs", "notes.md", "Order.cs"]),
            $"Searching the App project found [{string.Join(", ", app)}]: another project's files, or those of the project nested in its folder");

        popup.Project = Full("Lib/Lib.csproj");
        Ui.Settle();
        Assert.Equal(["Money.cs", "Money.g.cs"], Found(popup));
        Assert.Equal("2 matches in 2 files", popup.Status);

        Ui.Press(window, "alt S");
        Assert.Equal("Open Files", popup.NamedScope);
        Assert.Equal(["Main.cs"], Found(popup));
        window.Close();
    }

    [AvaloniaFact]
    public void The_Solution_views_Edit_menu_finds_in_a_folder_a_project_or_a_file_and_nowhere_else()
    {
        MainWindow window = Open();
        FindInFilesPopup popup = FromMenu(window, Reveal(window, "App/Models"), "Find in Files…");
        Assert.Equal((SearchScopeKind.Directory, Full("App/Models"), true), (popup.ScopeKind, popup.Directory, popup.Recursive));
        Search(popup, "total");
        string[] models = Found(popup);
        Assert.True(models.SequenceEqual(["Line.cs", "notes.md", "Order.cs"]), $"Find in Files on the Models folder found [{string.Join(", ", models)}]");

        Ui.Press(window, "alt Y");
        Assert.False(popup.Recursive);
        Assert.Equal(["notes.md", "Order.cs"], Found(popup));
        Ui.Press(window, "ESCAPE");

        popup = FromMenu(window, window.ExplorerView.Tree.Rows.First(r => r.Kind == NodeKind.Project && r.Name == "Lib"), "Find in Files…");
        Assert.Equal((SearchScopeKind.Project, Full("Lib/Lib.csproj")), (popup.ScopeKind, popup.Project));
        Search(popup, "total");
        Assert.Equal(["Money.cs", "Money.g.cs"], Found(popup));
        Ui.Press(window, "ESCAPE");

        popup = FromMenu(window, Reveal(window, "Lib/Money.g.cs"), "Find in Files…");
        Assert.Equal((SearchScopeKind.Scope, "Money.g.cs"), (popup.ScopeKind, popup.NamedScope));
        Search(popup, "total");
        Assert.Equal(["Money.g.cs"], Found(popup));
        Ui.Press(window, "ESCAPE");

        Reveal(window, "App/App.csproj");
        window.ExplorerView.FocusRows();
        Ui.Press(window, Ui.Shortcut(AppActions.FindInFiles));
        popup = Assert.IsType<FindInFilesPopup>(window.Popup);
        Assert.Equal((SearchScopeKind.Project, Full("App/App.csproj")), (popup.ScopeKind, popup.Project));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_mask_leaves_out_other_files_and_one_after_a_bang_leaves_out_what_it_matches()
    {
        MainWindow window = Open();
        FindInFilesPopup popup = Find(window);
        Search(popup, "total");
        Assert.Equal(8, Found(popup).Length);

        Ui.Press(window, "alt K");
        Assert.Equal("*.cs", popup.FileMask);
        string[] code = Found(popup);
        Assert.True(code.SequenceEqual(["Line.cs", "Main.cs", "Money.cs", "Money.g.cs", "Order.cs", "Plugin.cs"]), $"The mask *.cs let through [{string.Join(", ", code)}]");

        popup.FileMask = "*.cs, !*.g.cs";
        Ui.Settle();
        Assert.DoesNotContain("Money.g.cs", Found(popup));
        Assert.Equal(5, Found(popup).Length);

        popup.FileMask = "!*.cs";
        Ui.Settle();
        Assert.Equal(["notes.md", "total.md"], Found(popup));
        popup.FileMask = "*.MD,Plugin.?s";
        Ui.Settle();
        Assert.Equal(["notes.md", "Plugin.cs", "total.md"], Found(popup));

        popup.FileMask = " ";
        Ui.Settle();
        Assert.Empty(popup.Matches);
        Assert.Equal("Empty file mask", popup.Status);

        Ui.Press(window, "alt K");
        Assert.Null(popup.FileMask);
        Assert.Equal(8, Found(popup).Length);
        window.Close();
    }

    [AvaloniaFact]
    public void Replace_in_files_from_a_folders_menu_changes_only_the_files_under_it()
    {
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        FindInFilesPopup popup = FromMenu(window, Reveal(window, "App/Models"), "Replace in Files…");
        Assert.Equal(SearchScopeKind.Directory, popup.ScopeKind);
        popup.Words = true;
        Search(popup, "total");
        popup.Replacement = "sum";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement");

        Assert.Equal("Replaced 3 matches in 3 files", window.Status);
        Assert.Equal("class Order { decimal sum; }\n", File.ReadAllText(Full("App/Models/Order.cs")));
        Assert.Equal("class Line { decimal sum; }\n", File.ReadAllText(Full("App/Models/Lines/Line.cs")));
        Assert.Equal("the sum, in words\n", File.ReadAllText(Full("App/Models/notes.md")));
        foreach ((string file, string text) in new[]
        {
            ("App/Main.cs", "// the total of the app\nclass Main { int total; }\n"),
            ("App/Plugins/Plugin.cs", "class Plugin { int total; }\n"),
            ("Lib/Money.g.cs", "// generated: total\n"),
            ("docs/total.md", "total\n"),
        })
        {
            Assert.True(File.ReadAllText(Full(file)) == text, $"Replace in Files on the Models folder changed {file}, outside it");
        }
        window.Close();
    }

    /// <summary>Replace All acts on the query and scope as they stand, though the matches shown for a moment after the
    /// scope changes are still the last search's.</summary>
    [AvaloniaFact]
    public void Replace_all_pressed_as_the_scope_changes_replaces_in_the_new_scope_alone()
    {
        string[] others = ["App/Models/Order.cs", "App/Plugins/Plugin.cs", "Lib/Money.cs", "Lib/Money.g.cs", "docs/total.md"];
        Dictionary<string, string> before = others.ToDictionary(f => f, f => File.ReadAllText(Full(f)));
        MainWindow window = Keeping(folder);
        window.OpenFile(Full("App/Main.cs"));
        Ui.Settle();
        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        Ui.Press(window, Ui.Shortcut(AppActions.ReplaceInFiles));
        FindInFilesPopup popup = Assert.IsType<FindInFilesPopup>(window.Popup);
        Assert.Equal(SearchScopeKind.Solution, popup.ScopeKind);
        Search(popup, "total");
        Assert.Equal(8, Found(popup).Length);

        popup.Replacement = "sum";
        popup.ScopeKind = SearchScopeKind.Scope;
        popup.NamedScope = "Current File";
        popup.RequestReplace();
        Ui.WaitFor(() => window.Status?.StartsWith("Replaced", StringComparison.Ordinal) == true, "the replacement");
        Assert.Equal("Replaced 2 matches in 1 file", window.Status);
        Assert.StartsWith("Replace 2 matches in 1 file with 'sum'?", Assert.Single(asked), StringComparison.Ordinal);
        Assert.Equal("// the sum of the app\nclass Main { int sum; }\n", window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        foreach (string file in others)
        {
            Assert.True(File.ReadAllText(Full(file)) == before[file], $"Replace All in the current file changed {file}, which the solution's search had found");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void The_scope_and_the_mask_come_back_when_find_opens_again_and_after_a_restart()
    {
        MainWindow first = Keeping(folder);
        FindInFilesPopup popup = Find(first);
        Ui.Press(first, "alt D");
        popup.Directory = Full("Lib");
        Ui.Press(first, "alt Y");
        Ui.Press(first, "alt K");
        popup.FileMask = "*.cs, !*.g.cs";
        Ui.Settle();
        Ui.Press(first, "ESCAPE");

        popup = Find(first);
        Assert.Equal((SearchScopeKind.Directory, Full("Lib"), false, "*.cs, !*.g.cs"), (popup.ScopeKind, popup.Directory, popup.Recursive, popup.FileMask));
        Search(popup, "total");
        Assert.Equal(["Money.cs"], Found(popup));
        Ui.Press(first, "ESCAPE");
        first.Close();

        MainWindow second = Keeping(folder);
        popup = Find(second);
        Assert.True((popup.ScopeKind, popup.Directory, popup.Recursive, popup.FileMask) == (SearchScopeKind.Directory, Full("Lib"), false, "*.cs, !*.g.cs"),
            $"After a restart Find in Files opened on {popup.ScopeKind} {popup.Directory} (recursive {popup.Recursive}) with the mask {popup.FileMask ?? "off"}");
        Ui.Press(second, "ESCAPE");

        // Another folder has its own scope, and the mask is every folder's.
        string other = TestFolders.Create("other-");
        File.WriteAllText(Path.Combine(other, "One.cs"), "class One { }\n");
        second.OpenFolder(other);
        Ui.Settle();
        popup = Find(second);
        Assert.Equal((SearchScopeKind.Solution, "*.cs, !*.g.cs"), (popup.ScopeKind, popup.FileMask));
        Ui.Press(second, "ESCAPE");
        second.Close();
    }

    /// <summary>Find in Files as its shortcut opens it, where the keys are.</summary>
    private static FindInFilesPopup Find(MainWindow window)
    {
        Ui.Press(window, Ui.Shortcut(AppActions.FindInFiles));
        return Assert.IsType<FindInFilesPopup>(window.Popup);
    }

    private static void Search(FindInFilesPopup popup, string text)
    {
        popup.Text = text;
        Ui.Settle();
        Ui.WaitFor(() => popup.Searching.IsCompleted, $"the search for {text}");
    }

    /// <summary>The names of the files the matches are in, each once, in order.</summary>
    private static string[] Found(FindInFilesPopup popup)
    {
        Ui.WaitFor(() => popup.Searching.IsCompleted, "the search");
        return [.. popup.Matches.Select(m => m.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The Solution view's row of a file or folder, opened down to and chosen.</summary>
    private ExplorerNode Reveal(MainWindow window, string relative)
    {
        window.ExplorerView.Reveal(Full(relative));
        Ui.Settle();
        return window.ExplorerView.Selected ?? throw new InvalidOperationException($"The Solution view shows no {relative}");
    }

    /// <summary>A row's right-click menu, Edit, and an item of it, as clicks on them would; the popup it opens.</summary>
    private static FindInFilesPopup FromMenu(MainWindow window, ExplorerNode row, string item)
    {
        Ui.ChooseFromRowMenu(window, row, "Edit", item);
        return Assert.IsType<FindInFilesPopup>(window.Popup);
    }

    /// <summary>A window on the solution, with no C# language server: Find in Files, its scopes and the Solution view's
    /// projects ask nothing of C#.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        return window;
    }

    /// <summary>The window a person uses, as the app makes it: one that keeps its state, here in the test's own folder; with
    /// no C# language server, as <see cref="Open"/>.</summary>
    private MainWindow Keeping(string open)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(open);
        Ui.Settle();
        return window;
    }

    private string Full(string relative) => PathNames.Canonical(Path.Combine(folder, relative));

    /// <summary>A solution of three projects, one nested in another's folder, with "total" in code, notes, generated code and
    /// docs outside every project, never restored; each test starting on it as written, what a replacement wrote put back.</summary>
    public sealed class Shop : SharedProject
    {
        private static readonly string Project = TestProjects.Library();

        public Shop()
            : base("scope-")
        {
            Put("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"App/Plugins/Plugins.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
            Put("App/App.csproj", Project);
            Put("App/Main.cs", "// the total of the app\nclass Main { int total; }\n");
            Put("App/Models/Order.cs", "class Order { decimal total; }\n");
            Put("App/Models/Lines/Line.cs", "class Line { decimal total; }\n");
            Put("App/Models/notes.md", "the total, in words\n");
            Put("App/Plugins/Plugins.csproj", Project);
            Put("App/Plugins/Plugin.cs", "class Plugin { int total; }\n");
            Put("Lib/Lib.csproj", Project);
            Put("Lib/Money.cs", "record Money(decimal Total);\n");
            Put("Lib/Money.g.cs", "// generated: total\n");
            Put("docs/total.md", "total\n");
            // Never restored: no window of the class starts a language server, and the scopes are the projects as evaluated.
            Prepare(restore: false);
        }

        private void Put(string relative, string text)
        {
            string path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }
}
