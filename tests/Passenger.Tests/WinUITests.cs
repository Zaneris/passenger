using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Mcp;
using Passenger.App.Templates;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Inspector;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>A WinUI app of the Windows App SDK through the window, on Windows, where WinUI builds and runs: its XAML read
/// against the projection its restore brings (completion of controls, properties, events and values, the checks as typed),
/// its <c>{x:Bind}</c> paths completed and checked against the window's code and a data template's <c>x:DataType</c>, Go to
/// Declaration from them into the C#, and a rename in the C# following them; the app run, debugged, inspected in the Live
/// Visual Tree and used by an agent. Skipped elsewhere, saying why. The app is written as Visual Studio's template makes
/// one, no <c>dotnet new</c> template being Microsoft's; its packages come from nuget.org, as no restore of the solution's
/// brings them, so it is never one of the gate's.</summary>
public sealed class WinUITests : IClassFixture<WinUITests.Gallery>
{
    private readonly Gallery app;

    public WinUITests(Gallery app)
    {
        this.app = app;
        if (OperatingSystem.IsWindows())
        {
            app.Reset();
        }
    }

    /// <summary>WinUI's XAML as a person writes it: completion of its controls, a control's properties, events and values,
    /// its markup extensions and its own directives; an <c>{x:Bind}</c>'s path from the window's code and from a data
    /// template's <c>x:DataType</c>; marks where its compiler would refuse it; Go to Declaration into the C#; and a rename
    /// of a bound property from its C#.</summary>
    [AvaloniaFact]
    public void WinUI_xaml_completes_checks_goes_to_and_renames_with_x_bind_read_against_the_windows_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinUI builds and runs only on Windows, whose app the test makes");
        }
        Flow.StepApart("typing a tag offers WinUI's controls, then a control's properties and events, then an enum's values", () => new WinUITests(app),
            t => t.Typing_a_tag_offers_winuis_controls_then_a_controls_properties_and_events_then_an_enums_values());
        Flow.StepApart("x:Bind offers the window's members, its view model's through a path, and a data template's x:DataType's", () => new WinUITests(app),
            t => t.X_bind_offers_the_windows_members_its_view_models_through_a_path_and_a_data_templates_data_types());
        Flow.StepApart("a window that compiles is marked with nothing, and a misspelt property, value, theme resource and x:Bind path are marked as typed",
            () => new WinUITests(app), t => t.A_window_that_compiles_is_marked_with_nothing_and_misspellings_are_marked_as_typed());
        Flow.StepApart("Go to Declaration from x:Bind opens the bound property, and renaming it in C# renames the x:Bind", () => new WinUITests(app),
            t => t.Go_to_declaration_from_x_bind_opens_the_bound_property_and_renaming_it_in_csharp_renames_the_x_bind());
        Flow.StepApart("Go to Declaration from the code-behind's use of a named element lands on its x:Name, whose rename takes its x:Bind along",
            () => new WinUITests(app), t => t.Go_to_declaration_from_the_code_behinds_use_of_a_named_element_lands_on_its_name_whose_rename_takes_its_x_bind_along());
    }

    /// <summary>A WinUI app run and debugged as it is built: the fixture's unpackaged, its window on a desktop of its own, and
    /// one New Solution makes from Passenger's own template, packaged, registered from its build and started by Windows.</summary>
    [AvaloniaFact]
    public void A_winui_app_runs_and_debugs_unpackaged_and_as_a_package()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinUI builds and runs only on Windows, whose app the test makes");
        }
        Flow.StepApart("an unpackaged app runs, and debugged stops, shows its live tree and failed binding and takes an agent's click", () => new WinUITests(app),
            t => t.An_unpackaged_app_runs_and_debugged_stops_shows_its_live_tree_and_failed_binding_and_takes_an_agents_click());
        Flow.StepApart("a packaged app New Solution makes from Passenger's template is registered from its build, runs as Windows starts it, and debugged stops "
            + "at a breakpoint and shows its live tree", () => new WinUITests(app), t => t.A_packaged_app_new_solution_makes_is_registered_runs_as_windows_starts_it_and_debugged_stops());
    }

    /// <summary>The unpackaged app (WindowsPackageType None, the Windows App SDK carried in its output) runs, saying in the
    /// Run window that its window is up, and Stop ends it. Debug stops it at a breakpoint in its window's constructor, its
    /// locals shown; let go, its elements show in the Live Visual Tree as WinUI's diagnostics tell of them, the button's
    /// value and XAML with them, its misspelt binding is listed in Problems with the member nearest it, and an agent sees
    /// its tree, clicks its button and takes its picture. Its window opens on a desktop of its own, where nobody sees it.</summary>
    private void An_unpackaged_app_runs_and_debugged_stops_shows_its_live_tree_and_failed_binding_and_takes_an_agents_click()
    {
        using IDisposable hang = Limits.BeginHang();
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml.cs"));
        try
        {
            Ui.Launched(window, window.RunAsync(), "the app's build and start", Slow);
            Ui.WaitFor(() => Output(window).Contains(Gallery.Up, StringComparison.Ordinal),
                () => $"the app to say its window is up, in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
            Assert.True(window.Runner.IsRunning, $"The app ended once its window was up:\n{Output(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the app to end once stopped", TimeSpan.FromMinutes(1));

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "        System.Console.WriteLine(");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the app's start under the debugger", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop?.Top?.Name is { } top && top.Contains("MainWindow", StringComparison.Ordinal),
                () => $"the stop at the breakpoint in MainWindow's constructor; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}, stopped at "
                    + $"{window.Debugger.Stop?.Top?.Name}: {Ui.Said(window)}", Slow);
            Assert.Contains(window.BottomPanel.Debug.Tree.Rows, r => r.Depth == 0 && r.Name == "this");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "        System.Console.WriteLine(");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);

            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Go"),
                () => $"the Live Visual Tree to show the app's window; its inspector is {window.LiveApp?.Kind} {window.LiveApp?.State} {window.LiveApp?.Problem}: {Ui.Said(window)}", Slow);
            Assert.True(window.LiveApp is { Kind: LiveAppKind.WinUI } && window.LiveApp.Framework == $"WinUI 3, Windows App SDK {Gallery.WindowsAppSdk}",
                $"The app was taken for {window.LiveApp?.Kind}, {window.LiveApp?.Framework}");
            Assert.Contains(tree.Rows, r => r.Element is { Type: "TextBlock", Name: "Heading" });
            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "Go");
            Ui.WaitFor(() => tree.Details?.Name == "Go", "the button's details");
            ElementDetails go = tree.Details!;
            string xaml = PathNames.Canonical(app.PathOf("MainWindow.xaml"));
            int line = Array.FindIndex(File.ReadAllLines(xaml), l => l.Contains("x:Name=\"Go\"", StringComparison.Ordinal)) + 1;
            Assert.True(go.Source is { } made && PathNames.Same(made.File, xaml) && made.Line == line, $"The button's XAML is {go.Source?.File}:{go.Source?.Line}, not MainWindow.xaml:{line}");
            PropertyValue content = go.Properties.Single(p => p.Name == "Content");
            Assert.True(content is { Source: ValueSources.Local, Value: "Go" }, $"The button's Content is {content.Value} from {content.Source}, not its local Go");

            PassengerTools agent = new(window);
            Assert.Contains("Button#Go \"Go\"", Ui.Await(agent.AppTree(filter: "Button"), "an agent's look at the app's buttons", Slow), StringComparison.Ordinal);
            string clicked = Ui.Await(agent.AppClick(control: "#Go"), "an agent's click on Go", Slow);
            Assert.StartsWith("Clicked Button#Go", clicked, StringComparison.Ordinal);
            Ui.WaitFor(() => Ui.Await(agent.AppTree(control: "#Go"), "an agent's look at Go", Slow).Contains("\"Going\"", StringComparison.Ordinal),
                "Go to say Going once the agent clicked it", Slow);
            AppWindowInfo shown = Assert.Single(Ui.Await(agent.AppWindowsList(), "an agent's list of the app's windows", Slow).Windows);
            Assert.Equal("Gallery", shown.Title);
            CallToolResult picture = Ui.Await(agent.AppScreenshot(), "an agent's picture of the app", Slow);
            ImageContentBlock image = Assert.Single(picture.Content.OfType<ImageContentBlock>());
            using (Bitmap drawn = new(new MemoryStream(image.DecodedData.ToArray())))
            {
                Assert.True(Math.Abs(drawn.PixelSize.Width - (shown.Width * shown.Scaling)) <= 1 && Math.Abs(drawn.PixelSize.Height - (shown.Height * shown.Scaling)) <= 1,
                    $"The picture is {drawn.PixelSize} pixels for a window of {shown}");
            }
            Assert.True(image.DecodedData.Length > 4000, $"The app's picture is {image.DecodedData.Length} bytes, as a blank one would be");

            // Problems lists the failures of the app Run started last as well as the debugged one's.
            ProblemsModel problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.BindingProblems.Any(p => p.Failure.Type == "MainViewModel") && problems.BindingProblems.Any(p => p.Failure.Type == "Person"),
                () => $"the caption's binding to fail on its window's view model and, once Go is clicked, on a person, in Problems: {string.Join("; ", problems.BindingProblems.Select(p => p.Detail))}: {Ui.Said(window)}",
                TimeSpan.FromMinutes(1));
            BindingProblem caption = problems.BindingProblems.First(p => p.Failure.Type == "MainViewModel");
            Assert.Equal(("TextBlock#Caption", "Text", "Greting"), (caption.Failure.Control, caption.Failure.Property, caption.Failure.Path));
            Assert.Equal(["Greeting"], caption.Failure.Nearest);
            int bound = Array.FindIndex(File.ReadAllLines(xaml), l => l.Contains("{Binding Greting}", StringComparison.Ordinal));
            Assert.True(caption.Problem.Path is { } at && PathNames.Same(at, xaml) && caption.Problem.Position.Line == bound,
                $"The failed binding is placed at {caption.Problem.Path}:{caption.Problem.Position}, not the caption's Greting on MainWindow.xaml's line {bound + 1}");

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>New Solution's WinUI App, Passenger's template, made packaged (its default), restoring from nuget.org: Run
    /// registers its build's package as a development package, saying so, Windows starts it through Passenger's starter,
    /// and Stop ends it; Debug holds it at its start, stops in its window's constructor, and let go, the Live Visual Tree shows
    /// it. Its package is named alike in every run, so each registration replaces the last. It needs Windows App Runtime 2.7
    /// and Developer Mode; its window shows on the desktop, where Windows starts a package's.</summary>
    private void A_packaged_app_new_solution_makes_is_registered_runs_as_windows_starts_it_and_debugged_stops()
    {
        if (OperatingSystem.IsWindows() && Passenger.App.Run.WindowsPackages.WhyNotRegistered() is { } off)
        {
            Assert.Skip(off);
        }
        using IDisposable hang = Limits.BeginHang();
        string folder = TestFolders.Create("winui-made-");
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, Unseen = PassengerApp.ShowsNoWindow };
        window.Show();
        window.Start(LaunchOptions.Empty);
        Ui.Settle();
        try
        {
            Ui.Click(window.Welcome.NewSolutionButton);
            Ui.Settle();
            NewProjectDialog dialog = Assert.Single(window.OwnedWindows.OfType<NewProjectDialog>());
            Ui.WaitFor(() => dialog.TemplatesRead.IsCompleted, "dotnet new's templates to be read", TimeSpan.FromMinutes(3));
            dialog.Folder = folder;
            Ui.Click(dialog.FamilyRow("WinUI App") ?? throw new InvalidOperationException($"New Solution lists no WinUI App: [{string.Join(", ", dialog.Families.Select(f => f.Name))}]"));
            Assert.True(dialog.Chosen is { OwnProject: true }, $"WinUI App chose {dialog.Chosen?.Name}");
            Assert.Contains("Packaging", dialog.OptionLabels);
            dialog.SolutionName = "Packed";
            Ui.Settle();
            Ui.Click(dialog.CreateButton);
            Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "the WinUI app to be made and restored", Slow);
            Assert.True(dialog.Problem is null, $"The WinUI app was not made: {dialog.Problem}");
            string made = Assert.Single(Directory.GetFiles(folder, "Packed.csproj", SearchOption.AllDirectories));
            string projectFolder = Path.GetDirectoryName(made)!;
            foreach (string file in (string[])["App.xaml", "MainWindow.xaml", "MainWindow.xaml.cs", "app.manifest", "Package.appxmanifest", Path.Combine("Assets", "StoreLogo.png"),
                Path.Combine("Properties", "launchSettings.json")])
            {
                Assert.True(File.Exists(Path.Combine(projectFolder, file)), $"The template wrote no {file} in {projectFolder}");
            }
            string manifest = Path.Combine(projectFolder, "Package.appxmanifest");
            File.WriteAllText(manifest, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(manifest), "<Identity Name=\"[^\"]+\"", "<Identity Name=\"Passenger.Tests.Packed\""));

            Ui.WaitFor(() => window.Configurations.Any(c => c.Packaged), () => $"the package's configuration among [{string.Join(", ", window.Configurations.Select(c => c.Name))}]", Slow);
            Assert.True(window.SelectConfiguration(window.Configurations.First(c => c.Packaged).Name));
            if (Ui.Await(window.RunAsync(), "the package's build, registration and start", Slow) is { } refused)
            {
                // A runner without the Windows App SDK's runtime of the template's version runs no packaged app of it.
                Assert.SkipWhen(refused.Contains("which is not installed for this user", StringComparison.Ordinal), refused);
                string solution = Directory.GetFiles(folder, "Packed.sln*", SearchOption.AllDirectories).FirstOrDefault() is { } made2 ? File.ReadAllText(made2) : "(no solution)";
                Assert.Fail($"Run refused the package: {refused}{Ui.Said(window)}\nThe build chosen: {window.Session.BuildChoices.Chosen?.Key ?? "none"}, "
                    + $"offered [{string.Join(", ", window.Session.BuildChoices.Offered.Select(o => o.Key))}]; the solution:\n{solution}");
            }
            Ui.WaitFor(() => Output(window).Contains(" started as process ", StringComparison.Ordinal),
                () => $"Windows to start the package, said in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
            Assert.Contains("Registered Passenger.Tests.Packed as a development package", Output(window), StringComparison.Ordinal);
            Assert.True(window.Runner.IsRunning, $"The package's app ended as it started:\n{Output(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the package's app to end once stopped", TimeSpan.FromMinutes(1));

            window.OpenFile(Path.Combine(projectFolder, "MainWindow.xaml.cs"));
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "        InitializeComponent(");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the package's start under the debugger", Slow);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop?.Top?.Name is { } top && top.Contains("MainWindow", StringComparison.Ordinal),
                () => $"the stop at the breakpoint in MainWindow's constructor; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}, stopped at "
                    + $"{window.Debugger.Stop?.Top?.Name}, the breakpoint {string.Join("; ", window.Debugger.Breakpoints.In(PathNames.Canonical(Path.Combine(projectFolder, "MainWindow.xaml.cs"))).Select(b => $"{b.Status} {b.Message}"))}: "
                    + $"{Ui.Said(window)}", Slow);
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "        InitializeComponent(");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => window.LiveTree.Rows.Any(r => r.Element.Name == "Count"),
                () => $"the Live Visual Tree to show the package's window; its inspector is {window.LiveApp?.Kind} {window.LiveApp?.State} {window.LiveApp?.Problem}: {Ui.Said(window)}", Slow);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    private static TimeSpan Slow => Limits.WithinHang(TimeSpan.FromMinutes(8));

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private void Typing_a_tag_offers_winuis_controls_then_a_controls_properties_and_events_then_an_enums_values()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Spacing=\"8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "WinUI's controls offered after a tag's '<'");
        window.KeyTextInput("Butt");
        Ui.Settle();
        Assert.Equal("Button", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        window.KeyTextInput("C");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Click"), "a Button's events among its attributes");
        Assert.Contains("Command", window.Assist.CompletionLabels);
        Assert.Contains("Content", window.Assist.CompletionLabels);
        Assert.Contains("CornerRadius", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("G");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Grid.Row"), "Grid's attached properties by their owner");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's dependency properties, its bases' included");
        window.KeyTextInput("orizontalA");
        Ui.Settle();
        Assert.Equal("HorizontalAlignment", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Center"), "the enum's members offered inside the new value's quotes");
        Assert.Equal(["Center", "Left", "Right", "Stretch"], window.Assist.CompletionLabels.Order());
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "END");
        window.KeyTextInput(" Foreground=");
        window.KeyTextInput("\"");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("ThemeResource"), "WinUI's markup extensions after a value's brace");
        Assert.Contains("x:Bind", window.Assist.CompletionLabels);
        window.KeyTextInput("ThemeRes");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Accent"), "the resources App.xaml defines");
        // Thousands of theme keys come after the app's own, so the list is narrowed to one.
        window.KeyTextInput("TextFillColorPr");
        Ui.Settle();
        Assert.Contains("TextFillColorPrimaryBrush", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    private void X_bind_offers_the_windows_members_its_view_models_through_a_path_and_a_data_templates_data_types()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Text=\"{x:Bind ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("ViewModel"), "the window's members, x:Bind's start");
        Assert.Contains("Heading", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{x:Bind ViewModel.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the view model's members after ViewModel.");
        Assert.Contains("People", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        // The data template's own x:Bind, its path taken out.
        Replace(editor, "{x:Bind Name}", "{x:Bind }");
        Ui.PlaceAfter(editor, "{x:Bind }");
        Ui.Press(window, "LEFT");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Name"), "Person's members, the data template's x:DataType's");
        Assert.DoesNotContain("ViewModel", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    private void A_window_that_compiles_is_marked_with_nothing_and_misspellings_are_marked_as_typed()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Assert.True(window.XamlService.For(document)?.IsLoaded, "MainWindow.xaml was not read as WinUI's XAML");
        Assert.True(document.Markers.Count == 0, $"The window, which compiles, is marked: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Replace(editor, "<StackPanel Spacing=\"8\"", "<StackPanel Spacng=\"8\"");
        Replace(editor, "HorizontalAlignment=\"Center\"", "HorizontalAlignment=\"Centre\"");
        Replace(editor, "{ThemeResource TextFillColorPrimaryBrush}", "{ThemeResource TextFillColorPrimaryBrsh}");
        Replace(editor, "{x:Bind ViewModel.Greeting", "{x:Bind ViewModel.Greting");
        Replace(editor, "x:Phase=\"1\"", "x:Phase=\"one\"");
        Ui.WaitFor(() => document.Markers.Count == 5, () => $"the five mistakes marked once typing paused: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Assert.Equal("Cannot resolve property 'Spacng' on StackPanel", MarkerOn(editor, document, "Spacng").Message);
        Assert.Equal("'Centre' is not a value of HorizontalAlignment", MarkerOn(editor, document, "Centre").Message);
        Assert.Equal("Resource 'TextFillColorPrimaryBrsh' is not found", MarkerOn(editor, document, "TextFillColorPrimaryBrsh").Message);
        Marker path = MarkerOn(editor, document, "Greting");
        // x:Bind is compiled, so a path the view model lacks fails the build.
        Assert.Equal(MarkerKind.Error, path.Kind);
        Assert.Equal("Cannot resolve property 'Greting' on MainViewModel", path.Message);
        Assert.Single(document.Markers, m => m.Message == "'one' is not a phase, a whole number from 0");
        window.Close();
    }

    private void Go_to_declaration_from_x_bind_opens_the_bound_property_and_renaming_it_in_csharp_renames_the_x_bind()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{x:Bind ViewModel.Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Greeting", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Click=\"OnG");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.xaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("private void |OnGo(", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();

        string view = app.PathOf("MainWindow.xaml");
        MainWindow renaming = app.Open(app.PathOf("MainViewModel.cs"));
        renaming.Confirm = (_, _, _) => Task.FromResult(true);
        RenameInCode(renaming, "string Gree", "Salutation");
        string renamed = Gallery.Window.Replace("{x:Bind ViewModel.Greeting", "{x:Bind ViewModel.Salutation", StringComparison.Ordinal);
        Assert.True(File.ReadAllText(view).ReplaceLineEndings("\n") == renamed, $"The window's x:Bind did not follow the property's rename ({renaming.Status}):\n{File.ReadAllText(view)}");
        renaming.Close();
    }

    private void Go_to_declaration_from_the_code_behinds_use_of_a_named_element_lands_on_its_name_whose_rename_takes_its_x_bind_along()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "        G");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("<Button x:Name=\"|Go\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "x:Name=\"Head");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Heading", "the rename box on the text block's name");
        window.KeyTextInput("Banner");
        Ui.Press(window, "ENTER");
        OpenDocument view = window.EditorArea.Documents.First(d => d.Name == "MainWindow.xaml");
        Ui.WaitFor(() => view.Text.Snapshot.ToString().Contains("{x:Bind Banner.Text}", StringComparison.Ordinal), () => $"the x:Bind use renamed with it:\n{view.Text.Snapshot}");
        Assert.Contains("x:Name=\"Banner\"", view.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>The app, as Visual Studio's WinUI template makes one, unpackaged and carrying the Windows App SDK in its output
    /// (so it runs whatever runtime the machine has), with a view model its window binds by x:Bind and a list whose items a
    /// data template binds by its x:DataType; restored from nuget.org, on Windows alone.</summary>
    public sealed class Gallery : SharedProject
    {
        /// <summary>The Windows App SDK the app is made with, its newest release when the test was written.</summary>
        public const string WindowsAppSdk = "2.7.0";

        /// <summary>What the app writes once its window is made.</summary>
        public const string Up = "Gallery's window is up";

        public Gallery()
            : base("winui-")
        {
            if (OperatingSystem.IsWindows())
            {
                TestFolders.Write(Folder, "nuget.config", TestRun.FromNuGetOrg);
                TestFolders.Write(Folder, "Gallery.csproj", Project);
                TestFolders.Write(Folder, "App.xaml", AppXaml);
                TestFolders.Write(Folder, "App.xaml.cs", AppCode);
                TestFolders.Write(Folder, "MainWindow.xaml", Window);
                TestFolders.Write(Folder, "MainWindow.xaml.cs", CodeBehind);
                TestFolders.Write(Folder, "MainViewModel.cs", MainViewModel);
            }
        }

        public override ValueTask InitializeAsync() => OperatingSystem.IsWindows() ? base.InitializeAsync() : ValueTask.CompletedTask;

        /// <summary>The project for the test project's own target, on Windows 10's 19041 SDK, for this machine's processor.</summary>
        public static string Project => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{WpfApp.Framework}-windows10.0.19041.0</TargetFramework>
                <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
                <RootNamespace>Gallery</RootNamespace>
                <Platforms>x64;ARM64</Platforms>
                <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
                <UseWinUI>true</UseWinUI>
                <WindowsPackageType>None</WindowsPackageType>
                <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.WindowsAppSDK" Version="{WindowsAppSdk}" />
              </ItemGroup>
            </Project>

            """;

        public const string AppXaml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Application
                x:Class="Gallery.App"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
                        </ResourceDictionary.MergedDictionaries>
                        <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                    </ResourceDictionary>
                </Application.Resources>
            </Application>

            """;

        public const string AppCode = """
            using Microsoft.UI.Xaml;

            namespace Gallery;

            public partial class App : Application
            {
                private Window? window;

                public App()
                {
                    InitializeComponent();
                }

                protected override void OnLaunched(LaunchActivatedEventArgs args)
                {
                    window = new MainWindow();
                    window.Activate();
                }
            }

            """;

        public const string Window = """
            <?xml version="1.0" encoding="utf-8"?>
            <Window
                x:Class="Gallery.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:Gallery"
                Title="Gallery">
                <Grid x:Name="Root" RowSpacing="8" Padding="12">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>
                    <StackPanel Spacing="8">
                        <TextBlock x:Name="Heading" Text="{x:Bind ViewModel.Greeting, Mode=OneWay}" Style="{StaticResource TitleTextBlockStyle}" />
                        <Button x:Name="Go" Content="Go" Click="OnGo" Foreground="{ThemeResource Accent}" />
                        <TextBlock Text="{x:Bind Heading.Text}" HorizontalAlignment="Center" />
                        <TextBlock x:Name="Caption" Text="{Binding Greting}" />
                    </StackPanel>
                    <ListView Grid.Row="1" ItemsSource="{x:Bind ViewModel.People}">
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="local:Person">
                                <TextBlock Text="{x:Bind Name}" x:Phase="1" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </Grid>
            </Window>

            """;

        public const string CodeBehind = """
            using Microsoft.UI.Xaml;

            namespace Gallery;

            public sealed partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                    Root.DataContext = ViewModel;
                    System.Console.WriteLine("Gallery's window is up");
                }

                public MainViewModel ViewModel { get; } = new();

                private void OnGo(object sender, RoutedEventArgs e)
                {
                    Go.Content = "Going";
                    Caption.DataContext = ViewModel.People[0];
                }
            }

            """;

        public const string MainViewModel = """
            using System.Collections.ObjectModel;

            namespace Gallery;

            public class MainViewModel
            {
                public string Greeting { get; set; } = "Hello";

                public ObservableCollection<Person> People { get; } = [new() { Name = "Ada" }, new() { Name = "Grace" }];
            }

            public class Person
            {
                public string Name { get; set; } = "";
            }

            """;
    }
}
