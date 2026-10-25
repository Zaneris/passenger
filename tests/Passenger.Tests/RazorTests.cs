using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Razor through the window, on a Blazor app made by <c>dotnet new blazor</c> with components of its own (one
/// with its code in a code-behind, Tally.razor.cs, and no <c>@code</c>) and a Razor Page: a component's colours and its
/// errors, completion of components, their parameters, HTML's elements and the C#, go to declaration from a tag, rename
/// across markup and code, the code-behind's members completed, checked, found and renamed from the markup and back, and a
/// Razor Page's errors and completion. Each test opens its own window, on the server the last one left running, warm,
/// and on the app as written (<see cref="SharedProject"/>).</summary>
public sealed class RazorTests : IClassFixture<BlazorShop>
{
    private readonly BlazorShop app;

    public RazorTests(BlazorShop app)
    {
        this.app = app;
        app.Reset();
    }

    /// <summary>A component's markup and code in their colours, a name mistyped in its code block an error; and once put
    /// right, Reformat Code lays out the code block's C# and leaves the markup as it was.</summary>
    [AvaloniaFact]
    public void A_component_shows_its_markup_and_code_in_their_colours_a_name_mistyped_in_its_code_block_as_an_error_and_reformat_lays_out_its_csharp()
    {
        MainWindow window = app.Open("Components/Pages/Counter.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => KindOf(editor, "PageTitle") == TokenKind.Class, "the PageTitle component's tag in a type's colour, from Razor's colours");
        Assert.Equal(TokenKind.Tag, KindOf(editor, "<h1>"));
        Assert.Equal(TokenKind.AttributeName, KindOf(editor, "role"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "@code"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "@onclick"));
        Assert.Equal(TokenKind.Field, KindOf(editor, "currentCount"));
        Assert.Equal(TokenKind.Method, KindOf(editor, "IncrementCount"));
        Assert.DoesNotContain(document.Markers, m => m.Kind == MarkerKind.Error);
        // The route @page gives is not named as the attribute parameter Razor's generated code passes it to.
        Assert.DoesNotContain(document.InlayHints, h => editor.Document.Snapshot.GetLineFromOffset(h.Offset) == 0);
        string text = editor.Document.Snapshot.ToString();
        int start = text.IndexOf("<button", StringComparison.Ordinal) + 1;
        editor.Selections = SelectionSet.Single(start + 3);
        Ui.Settle();
        Assert.Equal(text.IndexOf("</button>", StringComparison.Ordinal) + 2, editor.MatchedBraces?.Matching.Start);

        int typo = editor.Document.Snapshot.ToString().IndexOf("currentCount++", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(typo, "currentCount".Length, "currentCont")]);
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the name mistyped in @code");
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("currentCont", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
        Assert.StartsWith("CS0103:", error.Message, StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(typo, "currentCont".Length, "currentCount")]);
        Ui.WaitFor(() => !document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle to go once the name is put right");

        string before = editor.Document.Snapshot.ToString();
        string field = "    private int currentCount = 0;";
        int at = before.IndexOf(field, StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(at, field.Length, "private   int currentCount=0;")]);
        Ui.Settle();
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => editor.Document.Snapshot.ToString() == before, "the code block laid out again as the template had it");
        window.Close();
    }

    /// <summary>Resting on a component's tag describes it and Go to Declaration opens its razor file; completion offers the
    /// elements and components after a tag's bracket, a component's parameters and the code block's members.</summary>
    [AvaloniaFact]
    public void A_components_tag_is_described_and_goes_to_its_file_and_completion_offers_elements_components_parameters_and_the_code_blocks_members()
    {
        MainWindow window = app.Open("Components/Pages/Home.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        int tag = editor.Document.Snapshot.ToString().IndexOf("<Greeting", StringComparison.Ordinal) + 3;
        Point point = editor.TranslatePoint(editor.PointOf(tag) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hover card on the component's tag");
        Assert.Contains("Shop.Components.Greeting", window.Assist.HoverText, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "<Gree");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.Settle();
        Assert.Equal("Greeting.razor", window.EditorArea.Active?.Name);

        window.OpenFile(app.PathOf("Components/Pages/Home.razor"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<PageTitle>Home</PageTitle>\n");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the app's own component among what a '<' offers");
        Assert.Contains("div", window.Assist.CompletionLabels);
        Assert.Contains("PageTitle", window.Assist.CompletionLabels);
        window.KeyTextInput("Gree");
        Ui.Settle();
        Assert.Equal("Greeting", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.Equal("<Greeting", editor.Document.Snapshot.GetLineText(3));

        window.KeyTextInput(" ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Times"), "the component's parameters");
        Assert.Contains("Name", window.Assist.CompletionLabels);
        window.KeyTextInput("Ti");
        Ui.Settle();
        Assert.Equal("Times", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.Equal("<Greeting Times=\"\"", editor.Document.Snapshot.GetLineText(3));
        Assert.Equal(new TextPosition(3, "<Greeting Times=\"".Length), editor.CaretPosition);

        window.KeyTextInput("2");
        Ui.PlaceAfter(editor, "Times=\"2\"");
        window.KeyTextInput(" @");
        Ui.Settle();
        window.KeyTextInput("ke");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "@key", "Razor's directive attributes, narrowed by what is typed");
        Ui.Press(window, "ENTER");
        Assert.Equal("<Greeting Times=\"2\" @key=\"\"", editor.Document.Snapshot.GetLineText(3));
        Assert.Equal(new TextPosition(3, "<Greeting Times=\"2\" @key=\"".Length), editor.CaretPosition);

        window.OpenFile(app.PathOf("Components/Pages/Counter.razor"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "currentCount++;");
        window.KeyTextInput(" c");
        Ui.Settle();
        window.KeyTextInput("urr");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "currentCount", "the code block's field, completed in C#");
        window.Close();
    }

    [AvaloniaFact]
    public void A_razor_page_shows_a_mistyped_member_of_its_model_as_an_error_and_completes_the_models_members()
    {
        MainWindow window = app.Open("Pages/Hello.cshtml");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the model's member mistyped");
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("Cont", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
        Assert.StartsWith("CS1061:", error.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "<h1>@Model.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Count"), "the model's members after @Model.");
        Assert.Contains("Greeting", window.Assist.CompletionLabels);
        window.Close();
    }

    /// <summary>Go to Declaration from a component's markup opens its code-behind at the member; the markup completes the
    /// code-behind's members and parameters and shows one misspelt as an error.</summary>
    [AvaloniaFact]
    public void A_components_markup_goes_to_its_code_behinds_members_completes_them_and_its_parameters_and_shows_one_misspelt_as_an_error()
    {
        MainWindow window = app.Open("Components/Pages/Tally.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => KindOf(editor, "AddClick") == TokenKind.Method, "the server's colours on the markup");
        Ui.PlaceAfter(editor, "@onclick=\"AddC");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Tally.razor.cs", "the code-behind opened at the method");
        Assert.Equal(new TextPosition(11, "    private void ".Length), window.EditorArea.CodeEditor.CaretPosition);

        window.OpenFile(app.PathOf("Components/Pages/Tally.razor"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "@cli");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "Tally.razor.cs", "the code-behind opened at the field");
        Assert.Equal(new TextPosition(9, "    private int ".Length), window.EditorArea.CodeEditor.CaretPosition);

        window.OpenFile(app.PathOf("Components/Pages/Tally.razor"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => KindOf(editor, "clicks") == TokenKind.Field, "the code-behind's field in the markup coloured as a field");
        Assert.Equal(TokenKind.Method, KindOf(editor, "AddClick"));
        Assert.Equal(TokenKind.Property, KindOf(editor, "Start"));
        Assert.DoesNotContain(document.Markers, m => m.Kind == MarkerKind.Error);

        int used = editor.Document.Snapshot.ToString().IndexOf("@clicks", StringComparison.Ordinal) + 1;
        editor.ApplyEdits([new TextEdit(used, "clicks".Length, "clciks")]);
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the code-behind's field misspelt in the markup");
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("clciks", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
        Assert.StartsWith("CS0103:", error.Message, StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(used, "clciks".Length, "clicks")]);
        Ui.WaitFor(() => !document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle to go once the field's name is put right");

        Ui.PlaceAfter(editor, "<p>Clicks: @clicks</p>\n");
        window.KeyTextInput("<p>@");
        Ui.Settle();
        window.KeyTextInput("cli");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "clicks", "the code-behind's field among what the markup's C# offers");
        Ui.Press(window, "ENTER");
        Assert.Equal("<p>@clicks", editor.Document.Snapshot.GetLineText(5));
        window.KeyTextInput(" @");
        Ui.Settle();
        window.KeyTextInput("Add");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "AddClick", "the code-behind's method among what the markup's C# offers");
        Ui.Press(window, "ESCAPE");

        window.OpenFile(app.PathOf("Components/Pages/Home.razor"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Greeting Name=\"Ada\" />\n");
        window.KeyTextInput("<Tally ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Start"), "the code-behind's [Parameter] among the component's attributes");
        window.Close();
    }

    /// <summary>Renaming a component's parameter renames it where the markup sets it; renaming a code-behind's member renames
    /// it in the markup, and renaming one in the markup renames it in the code-behind.</summary>
    [AvaloniaFact]
    public void Renaming_a_parameter_or_a_code_behinds_member_renames_it_in_the_markup_and_renaming_it_in_the_markup_renames_it_in_the_code_behind()
    {
        MainWindow window = app.Open("Components/Pages/Home.razor");
        OpenDocument page = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("Components/Greeting.razor"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "public string Na");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Name", "the rename box on the parameter");
        window.KeyTextInput("Person");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => page.Text.Snapshot.ToString().Contains("<Greeting Person=\"Ada\"", StringComparison.Ordinal),
            "the page that sets the parameter to set it by its new name");
        string renamed = editor.Document.Snapshot.ToString();
        Assert.Contains("public string Person { get; set; }", renamed, StringComparison.Ordinal);
        Assert.Contains("Hello, @Person!", renamed, StringComparison.Ordinal);

        window.OpenFile(app.PathOf("Components/Pages/Tally.razor"));
        Ui.Settle();
        OpenDocument markup = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("Components/Pages/Tally.razor.cs"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "private int cli");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "clicks", "the rename box on the field");
        window.KeyTextInput("taps");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => markup.Text.Snapshot.ToString().Contains("<p>Clicks: @taps</p>", StringComparison.Ordinal),
            "the markup that shows the field to show it by its new name");
        Assert.Contains("taps++;", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("Components/Pages/Tally.razor"));
        Ui.Settle();
        OpenDocument code = window.EditorArea.Documents.First(d => d.Name == "Tally.razor.cs");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "@onclick=\"AddC");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "AddClick", "the rename box on the method in the markup");
        window.KeyTextInput("Tap");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => code.Text.Snapshot.ToString().Contains("private void Tap()", StringComparison.Ordinal),
            "the code-behind to declare the method by the name given in the markup");
        Assert.Contains("@onclick=\"Tap\"", markup.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>The one kind a stretch of the text is coloured as, its spaces aside; null when its parts differ.</summary>
    private static TokenKind? KindOf(CodeEditor editor, string stretch)
    {
        TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{stretch}' is not in the text");
        TokenKind?[] kinds = new TokenKind?[stretch.Length];
        Array.Fill(kinds, TokenKind.Text);
        foreach (SyntaxSpan span in editor.Highlighter!.GetSpans(text, start, stretch.Length))
        {
            for (int i = Math.Max(span.Start, start); i < Math.Min(span.End, start + stretch.Length); i++)
            {
                kinds[i - start] = span.Kind;
            }
        }
        TokenKind?[] seen = [.. kinds.Where((_, i) => !char.IsWhiteSpace(stretch[i])).Distinct()];
        return seen.Length == 1 ? seen[0] : null;
    }
}

/// <summary>A Blazor app debugged from the window: breakpoints in a page's <c>@code</c> block and on the C# in its markup
/// bind through the line directives Razor's generated C# carries, and one in a component's code-behind binds as any C#
/// does, each stopping the app when the page is asked for. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class RazorDebuggingTests : IDisposable
{
    private readonly string folder = TestFolders.Create("razor-debug-");
    private readonly int port;

    public RazorDebuggingTests()
    {
        TcpListener free = new(IPAddress.Loopback, 0);
        free.Start();
        port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        TestFolders.Prepare(() =>
        {
            BlazorApp.Create(folder);
            File.WriteAllText(Path.Combine(folder, "Components/Pages/Visits.razor"),
                "@page \"/visits\"\n\n<p>Visits: @visits</p>\n\n@code {\n    private int visits;\n\n    protected override void OnInitialized()\n    {\n        visits = 41 + 1;\n    }\n}\n");
            File.WriteAllText(Path.Combine(folder, "Components/Pages/Tally.razor"), BlazorApp.TallyMarkup);
            File.WriteAllText(Path.Combine(folder, "Components/Pages/Tally.razor.cs"), BlazorApp.TallyCode);
            File.WriteAllText(Path.Combine(folder, "Properties/launchSettings.json"),
                $"{{ \"profiles\": {{ \"http\": {{ \"commandName\": \"Project\", \"launchBrowser\": true, \"applicationUrl\": \"http://localhost:{port}\", "
                + "\"environmentVariables\": { \"ASPNETCORE_ENVIRONMENT\": \"Development\" } } } }");
            Restore.Run(folder);
        }, folder);
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A breakpoint in a page's <c>@code</c> block stops the app as the page is asked for, one set on the markup's C#
    /// while it is stopped there stops it as the page is drawn, and the page comes once it goes on; then one in another
    /// component's code-behind, set before the app started, stops it as that page is asked for. One app, debugged once, with
    /// no C# language server: the breakpoints bind through the build's PDB, which the debugger reads.</summary>
    [AvaloniaFact]
    public void Breakpoints_in_a_pages_code_block_and_markup_and_in_a_components_code_behind_stop_the_app_as_each_page_is_asked_for()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        string code = Path.Combine(PathNames.Canonical(folder), "Components", "Pages", "Tally.razor.cs");
        string visits = Path.Combine(PathNames.Canonical(folder), "Components/Pages/Visits.razor");
        window.OpenFile(code);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new TextPosition(18, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Contains(new LineMark(18, LineMarkKind.Breakpoint), window.EditorArea.CodeEditor.LineMarks);
        window.OpenFile(visits);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(9, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Assert.Contains(new LineMark(9, LineMarkKind.Breakpoint), editor.LineMarks);

        Ui.Run(window, AppActions.Debug);
        Ui.WaitFor(() => window.Browsed.Count > 0, "the app to listen at its launch profile's address", TimeSpan.FromMinutes(4));
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(3) };
        Task<string> page = Task.Run(() => client.GetStringAsync($"http://localhost:{port}/visits"));
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the app to stop at the breakpoint in @code as the page is asked for");
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(9, LineMarkKind.ExecutionPoint)), "the stop shown on the breakpoint's line");
        Assert.Contains(new LineMark(9, LineMarkKind.BreakpointBound), editor.LineMarks);
        DapStackFrame top = window.BottomPanel.Debug.Frames[0];
        Assert.Contains("OnInitialized", top.Name, StringComparison.Ordinal);
        Assert.False(page.IsCompleted, "The page came back while the app was stopped at the breakpoint");

        editor.GoTo(new TextPosition(2, 14));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.Resume);
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(2, LineMarkKind.ExecutionPoint)), "the stop on the markup's @visits as the page is drawn");
        Assert.Contains("BuildRenderTree", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
        Assert.False(page.IsCompleted, "The page came back while the app was stopped in its markup");

        Ui.Run(window, AppActions.Resume);
        string html = Ui.Await(page, "the page once the app goes on");
        Assert.Contains("Visits: 42", html, StringComparison.Ordinal);

        Task<string> tally = Task.Run(() => client.GetStringAsync($"http://localhost:{port}/tally"));
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.EditorArea.Active?.Path == code,
            "the app to stop at the breakpoint in the code-behind as the page is asked for");
        Ui.WaitFor(() => window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(18, LineMarkKind.ExecutionPoint)), "the stop shown on the code-behind's line");
        Assert.Contains(new LineMark(18, LineMarkKind.BreakpointBound), window.EditorArea.CodeEditor.LineMarks);
        Assert.Contains("OnInitialized", window.BottomPanel.Debug.Frames[0].Name, StringComparison.Ordinal);
        Assert.False(tally.IsCompleted, "The page came back while the app was stopped in its code-behind");

        Ui.Run(window, AppActions.Resume);
        Assert.Contains("Clicks: 42", Ui.Await(tally, "the page once the app goes on"), StringComparison.Ordinal);

        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }
}

/// <summary>A Blazor Web App as <c>dotnet new blazor</c> makes it (named Shop, for .NET 10, without HTTPS), in a folder.</summary>
internal static class BlazorApp
{
    /// <summary>A component whose code is all in its code-behind, Tally.razor.cs, its markup without <c>@code</c>.</summary>
    public const string TallyMarkup = "@page \"/tally\"\n\n<h1>Tally from @Start</h1>\n\n<p>Clicks: @clicks</p>\n\n<button class=\"btn btn-primary\" @onclick=\"AddClick\">Add</button>\n";

    /// <summary>The component's partial class: a parameter, a field and a method the markup uses, and its initialization.</summary>
    public const string TallyCode = "using Microsoft.AspNetCore.Components;\n\nnamespace Shop.Components.Pages;\n\npublic partial class Tally\n{\n"
        + "    [Parameter]\n    public int Start { get; set; }\n\n    private int clicks;\n\n    private void AddClick()\n    {\n        clicks++;\n    }\n\n"
        + "    protected override void OnInitialized()\n    {\n        clicks = Start + 41 + 1;\n    }\n}\n";

    public static void Create(string folder)
    {
        ChildResult made = Task.Run(() => ChildProcesses.RunAsync(
            ChildProcesses.DotnetCommand(["new", "blazor", "--output", folder, "--name", "Shop", "--framework", TestProjects.Framework, "--no-https", "--no-restore"]),
            limit: TimeSpan.FromMinutes(3))).GetAwaiter().GetResult();
        Assert.True(made.Succeeded, $"dotnet new blazor did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
    }
}

/// <summary>The Blazor app the Razor and web tests open, restored once a class and never built, its warm server shared
/// (<see cref="SharedProject"/>): for Razor, a component that takes parameters and a page that sets them, a component whose
/// code is all in its code-behind, and a Razor Page whose markup misnames a member of its model; for the web languages,
/// the files a web project carries beside its C#: wwwroot/js/site.js, TypeScript under Scripts/ with its tsconfig.json,
/// wwwroot/css/site.css and an SCSS and a Less stylesheet beside it, a package.json with a dependency installed in
/// node_modules, and a component with a &lt;style&gt; and a &lt;script&gt;.</summary>
public sealed class BlazorShop : SharedProject
{
    public const string Script = "// Greets whoever opens the page.\nfunction greet(name) {\n    return \"Hello, \" + name;\n}\n\nconst message = greet(\"Ada\");\nconsole.log(message);\n";

    public const string Items = "export interface Item {\n    name: string;\n    price: number;\n}\n\nexport function total(items: Item[]): number {\n"
        + "    return items.reduce((sum, item) => sum + item.price, 0);\n}\n";

    public const string Cart = "import { Item, total } from \"./items\";\n\nconst items: Item[] = [{ name: \"tea\", price: 3 }];\nconst sum: number = total(items);\n"
        + "const label: string = sum;\nconsole.log(label);\n";

    public const string Config = "{\n  // The scripts compile to wwwroot/js.\n  \"compilerOptions\": {\n    \"target\": \"ES2022\",\n    \"module\": \"ES2022\",\n"
        + "    \"strict\": true,\n    \"outDir\": \"wwwroot/js/out\"\n  },\n  \"include\": [\"Scripts/**/*.ts\"]\n}\n";

    public const string Style = ":root {\n    --accent: #1b6ec2;\n}\n\n.btn-primary {\n    color: var(--accent);\n    background-color: #ffffff;\n}\n";

    /// <summary>A variable, a mixin and nesting: what CSS's own reading would mark as errors.</summary>
    public const string Sass = "$accent: #1b6ec2;\n\n@mixin framed($width) {\n    border: $width solid $accent;\n}\n\n.card {\n    padding: 12px;\n"
        + "    &:hover {\n        color: darken($accent, 10%);\n    }\n    @include framed(1px);\n}\n";

    public const string Less = "@accent: #1b6ec2;\n\n.framed(@width) {\n    border: @width solid @accent;\n}\n\n.card {\n    padding: 12px;\n"
        + "    &:hover {\n        color: darken(@accent, 10%);\n    }\n    .framed(1px);\n}\n";

    public const string Package = "{\n  \"name\": \"shop-client\",\n  \"version\": \"1.0.0\",\n  \"private\": true,\n  \"scripts\": {\n"
        + "    \"build\": \"tsc -p .\",\n    \"watch\": \"npm run build -- --watch\"\n  },\n  \"devDependencies\": {\n    \"left-pad\": \"^1.3.0\"\n  }\n}\n";

    public const string Component = "@page \"/styled\"\n\n<h1 class=\"title\">Styled</h1>\n\n<style>\n    .title {\n        color: #1b6ec2;\n    }\n</style>\n\n"
        + "<script>\n    function shout(text) {\n        return text.toUpperCase();\n    }\n    shout(\"hi\");\n</script>\n";

    private static readonly (string Path, string Text)[] Files =
    [
        ("Components/Greeting.razor",
            "<p>Hello, @Name!</p>\n\n@code {\n    [Parameter]\n    public string Name { get; set; } = \"\";\n\n    [Parameter]\n    public int Times { get; set; }\n}\n"),
        ("Components/Pages/Home.razor", "@page \"/\"\n\n<PageTitle>Home</PageTitle>\n\n<Greeting Name=\"Ada\" />\n"),
        ("Components/Pages/Tally.razor", BlazorApp.TallyMarkup),
        ("Components/Pages/Tally.razor.cs", BlazorApp.TallyCode),
        ("Pages/Hello.cshtml", "@page\n@model Shop.Pages.HelloModel\n\n<h1>@Model.Greeting</h1>\n<p>@Model.Cont</p>\n"),
        ("Pages/Hello.cshtml.cs", "using Microsoft.AspNetCore.Mvc.RazorPages;\n\nnamespace Shop.Pages;\n\npublic class HelloModel : PageModel\n{\n"
            + "    public string Greeting { get; set; } = \"Hi\";\n\n    public int Count { get; set; }\n\n    public void OnGet()\n    {\n        Count = 3;\n    }\n}\n"),
        ("wwwroot/js/site.js", Script),
        ("Scripts/items.ts", Items),
        ("Scripts/cart.ts", Cart),
        ("tsconfig.json", Config),
        ("wwwroot/css/site.css", Style),
        ("wwwroot/css/theme.scss", Sass),
        ("wwwroot/css/theme.less", Less),
        ("package.json", Package),
        ("node_modules/left-pad/package.json", "{\n  \"name\": \"left-pad\",\n  \"version\": \"1.3.0\",\n  \"main\": \"index.js\"\n}\n"),
        ("node_modules/left-pad/index.js", "module.exports = (text, length) => text.padStart(length);\n"),
        ("Components/Pages/Styled.razor", Component),
    ];

    public BlazorShop()
        : base("blazor-")
    {
        TestFolders.Prepare(() =>
        {
            BlazorApp.Create(Folder);
            foreach ((string path, string text) in Files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf(path))!);
                File.WriteAllText(PathOf(path), text);
            }
            // The first window's design-time build makes bin, which a reset would take for a file the test added: made now, it stays.
            Directory.CreateDirectory(PathOf("bin"));
        }, Folder);
        Prepare();
    }

    /// <summary>A file's path as the IDE names it: canonical, its '/' Windows' '\' there, so it compares with an open file's.</summary>
    public string Named(string relative) => PathNames.Canonical(PathOf(relative));
}
