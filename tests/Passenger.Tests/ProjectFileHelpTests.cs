using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.MSBuild;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Code help in project files, through the window: an SDK-style project and a Directory.Packages.props complete
/// their elements, properties and values, package ids and versions from the folder's feed (a stand-in on loopback, never
/// nuget.org), paths and the names MSBuild's evaluation knows; the card over a name says its evaluated value and where it
/// was set; Go to Declaration goes where a property, target or import is defined, the SDK's own targets read-only, and an
/// item's card counts the files its wildcards take in as they come and go on disk; the evaluation's errors and a
/// vulnerable package are marked on their lines and listed in Problems; and a project file saved has the Solution view
/// show what it says now.</summary>
public sealed class ProjectFileHelpTests : IClassFixture<ProjectFileHelpTests.Shop>
{
    private const string Advisory = "https://github.com/advisories/GHSA-pass-0000-test";

    /// <summary>A project with a property of its own over the SDK's, a condition on a property Directory.Build.props sets,
    /// a package at a vulnerable version, an import of its own and one of a file not there, and a target after Build.</summary>
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <PropertyGroup Condition="'$(ShopLogs)' == 'true'">
            <DefineConstants>$(DefineConstants);LOGS</DefineConstants>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Passenger.Vulnerable" Version="1.0.0" />
            <ProjectReference Include="../" />
          </ItemGroup>
          <Import Project="Shared.targets" />
          <Import Project="Missing.targets" />
          <Target Name="Shout" DependsOnTargets="Build">
            <Message Text="Built $(TargetFramework)" Importance="high" />
          </Target>
        </Project>

        """;

    private const string BuildProps = $"""
        <Project>
          <PropertyGroup>
            <TargetFramework>{TestProjects.Framework}</TargetFramework>
            <ShopLogs>true</ShopLogs>
            <SampleVersion>2.0.0</SampleVersion>
          </PropertyGroup>
        </Project>

        """;

    private readonly Shop shop;
    private readonly string folder;

    public ProjectFileHelpTests(Shop shop)
    {
        this.shop = shop;
        shop.Reset();
        folder = PathNames.Canonical(shop.Folder);
    }

    [AvaloniaFact]
    public void A_project_file_completes_its_properties_their_values_paths_and_the_names_its_evaluation_knows()
    {
        MainWindow window = Open("App/App.csproj");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<OutputType>Exe</OutputType>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("TargetFramework"), "the properties a PropertyGroup takes after a tag's '<'");
        Assert.Equal(["TargetFramework", "TargetFrameworks", "OutputType", "Nullable", "ImplicitUsings"], window.Assist.CompletionLabels.Take(5));
        Assert.Contains(("TargetFramework", TestProjects.Framework), window.Assist.CompletionDetails);
        window.KeyTextInput("Nul");
        Ui.Settle();
        Assert.Equal("Nullable", window.Assist.CompletionLabels.FirstOrDefault());
        Assert.StartsWith("Set the nullable annotations and warnings context", window.Assist.ItemDocumentation);
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Nullable>|</Nullable>", CaretLine(editor), StringComparison.Ordinal);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "Nullable's values offered between its tags");
        Assert.Equal(["enable", "disable", "warnings", "annotations"], window.Assist.CompletionLabels);
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Nullable>enable|</Nullable>", CaretLine(editor), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "<Nullable>enable</Nullable>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "the properties offered again");
        window.KeyTextInput("Shop");
        Ui.Settle();
        Assert.Equal(("ShopLogs", "true"), window.Assist.CompletionDetails.FirstOrDefault());
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "'$(Shop");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "the properties offered after '$('");
        Assert.Equal(("ShopLogs", "true"), window.Assist.CompletionDetails[0]);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<ProjectReference Include=\"../");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Core"), "the folders beside the project's");
        window.KeyTextInput("Co");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Core.csproj"), "the project in the folder chosen");
        Assert.DoesNotContain("Store.cs", window.Assist.CompletionLabels);
        Ui.Press(window, "ENTER");
        Assert.Contains("<ProjectReference Include=\"../Core/Core.csproj|\" />", CaretLine(editor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_package_reference_completes_ids_and_versions_from_the_folders_feed_and_a_vulnerable_version_is_marked()
    {
        MainWindow window = Open("App/App.csproj");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.StartsWith("NU1902", StringComparison.Ordinal)), "the vulnerable package's warning");
        Marker vulnerable = MarkerOn(editor, document, "1.0.0");
        Assert.Equal(MarkerKind.Warning, vulnerable.Kind);
        Assert.Equal($"NU1902: Package 'Passenger.Vulnerable' 1.0.0 has a known moderate severity vulnerability, {Advisory}", vulnerable.Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("Passenger.Vulnerable", StringComparison.Ordinal)),
            "Problems listing the vulnerable package");

        Ui.PlaceAfter(editor, "Include=\"Passenger.Vulnerable\" Version=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count == 3, "the package's three versions");
        Assert.Equal([("1.1.0", ""), ("1.0.1", ""), ("1.0.0", "Moderate severity vulnerability")], window.Assist.CompletionDetails);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<ProjectReference Include=\"../\" />");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("PackageReference"), "the item types an ItemGroup takes");
        window.KeyTextInput("PackageRef");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<PackageReference Include=\"|\" />", CaretLine(editor), StringComparison.Ordinal);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count == 3, "the feed's packages offered for the reference's Include");
        window.KeyTextInput("Sam");
        Ui.Settle();
        Assert.Equal(("Passenger.Sample", "2.0.0"), window.Assist.CompletionDetails[0]);
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<PackageReference Include=\"Passenger.Sample|\" Version=\"2.0.0\" />", CaretLine(editor), StringComparison.Ordinal);
        Assert.False(window.DockHost[ToolWindowIds.NuGet].IsBuilt, "The project file's package completion built the hidden NuGet window's view");
        window.Close();
    }

    [AvaloniaFact]
    public void The_card_over_a_name_says_its_evaluated_value_and_where_it_was_set_and_go_to_declaration_goes_there()
    {
        MainWindow window = Open("App/App.csproj");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "'$(Shop");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the property");
        Assert.Equal("ShopLogs = true\nMSBuild property, set in Directory.Build.props, line 4", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "<Output");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the project's own property");
        Assert.StartsWith("OutputType = Exe\nMSBuild property, set in App.csproj, line 3 (over ", window.Assist.HoverText);
        Assert.EndsWith("earlier definition)\nType of output to generate (WinExe, Exe, or Library)", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "'$(Shop");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Directory.Build.props", window.EditorArea.Active?.Name);
        Assert.Equal("    |<ShopLogs>true</ShopLogs>", CaretLine(window.EditorArea.CodeEditor));

        window.OpenFile(Path.Combine(folder, "App", "App.csproj"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Import Project=\"Sha");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Shared.targets", window.EditorArea.Active?.Name);

        window.OpenFile(Path.Combine(folder, "App", "App.csproj"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "DependsOnTargets=\"Bui");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the target");
        Assert.StartsWith("Target Build\nMSBuild target, defined in Microsoft.Common.CurrentVersion.targets, line ", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        OpenDocument targets = window.EditorArea.Active!;
        Assert.Equal("Microsoft.Common.CurrentVersion.targets", targets.Name);
        Assert.True(targets.IsReadOnly, "the SDK's targets opened editable");
        Assert.Equal("  |<Target", CaretLine(window.EditorArea.CodeEditor));
        Assert.Contains("Name=\"Build\"", targets.Text.Snapshot.GetLineText(window.EditorArea.CodeEditor.CaretPosition.Line + 1), StringComparison.Ordinal);
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "\"$(BuildDepends");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the SDK's property");
        Assert.StartsWith("BuildDependsOn = ", window.Assist.HoverText);
        window.Close();
    }

    /// <summary>Core takes in its own folder's C# by the SDK's wildcard and Shared's by its own element, so a file come into or
    /// gone from either folder, whether another program or the IDE made it, changes what the cards count with nothing typed.</summary>
    [AvaloniaFact]
    public void An_item_elements_card_counts_the_files_come_into_and_gone_from_the_folders_its_wildcards_walk_with_nothing_typed()
    {
        MainWindow window = Open("Core/Core.csproj");
        OpenDocument document = window.EditorArea.Active!;
        MSBuildDocument help = window.Session.MSBuild.For(document)!;
        void Counted(int count, string why) => Ui.WaitFor(() => help.Insight?.ItemType("Compile")?.Count == count, why);
        // As MSBuild evaluates the element's wildcard: by the system's separator.
        string price = Path.Combine("..", "Shared", "Price.cs");
        string tag = Path.Combine("..", "Shared", "Tag.cs");
        Assert.Equal($"MSBuild item, 2 Compile items in the project: {price}", ItemCard(window, "<Comp"));

        File.WriteAllText(Path.Combine(folder, "Core", "Shelf.cs"), "class Shelf\n{\n}\n");
        Counted(3, "the Compile items to count the file another program put in the project's folder");
        Assert.Equal($"MSBuild item, 3 Compile items in the project: {price}", ItemCard(window, "<Comp"));
        File.WriteAllText(Path.Combine(folder, "Shared", "Tag.cs"), "class Tag\n{\n}\n");
        Counted(4, "the Compile items to count the file put in the folder outside the project that its element takes in");
        Assert.Equal($"MSBuild item, 4 Compile items in the project: {price};{tag}", ItemCard(window, "<Comp"));
        File.Delete(Path.Combine(folder, "Core", "Shelf.cs"));
        Counted(3, "the Compile items to stop counting the file deleted from the project's folder");
        File.Delete(Path.Combine(folder, "Shared", "Tag.cs"));
        Counted(2, "the Compile items to stop counting the file deleted from Shared");
        Assert.Equal($"MSBuild item, 2 Compile items in the project: {price}", ItemCard(window, "<Comp"));

        // A file the IDE makes itself, which the folder watcher takes for a save.
        Assert.Null(window.CreateFile(Path.Combine(folder, "Core"), "Crate.cs", "class Crate\n{\n}\n"));
        Counted(3, "the Compile items to count the file New File made");
        window.OpenFile(Path.Combine(folder, "Core", "Core.csproj"));
        Ui.Settle();
        Assert.Equal($"MSBuild item, 3 Compile items in the project: {price}", ItemCard(window, "<Comp"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_evaluations_errors_are_marked_on_their_lines_as_typed_and_listed_in_problems()
    {
        MainWindow window = Open("App/App.csproj");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the import that is not there marked");
        Marker missing = MarkerOn(editor, document, "Missing.targets");
        Assert.StartsWith("MSB4019: The imported project \"", missing.Message);
        Assert.EndsWith("Missing.targets\" was not found. Confirm that the expression in the Import declaration \"Missing.targets\" is correct, and that the file exists on disk.",
            missing.Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error && p.Message.StartsWith("MSB4019", StringComparison.Ordinal)),
            "Problems listing the import not found");

        Replace(editor, "'$(ShopLogs)' == 'true'", "'$(ShopLogs)' ==");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.StartsWith("MSB4092", StringComparison.Ordinal)), "the malformed condition marked");
        Marker condition = MarkerOn(editor, document, "'$(ShopLogs)' ==");
        Assert.Equal(MarkerKind.Error, condition.Kind);
        Assert.StartsWith("MSB4092: An unexpected token", condition.Message);
        Replace(editor, "'$(ShopLogs)' ==", "'$(ShopLogs)' == 'true'");
        Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("MSB4092", StringComparison.Ordinal)), "the condition put right no longer marked");

        Replace(editor, "Sdk=\"Microsoft.NET.Sdk\"", "Sdk=\"Microsoft.NET.Sdk.Nowhere\"");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.StartsWith("MSB4236", StringComparison.Ordinal)), "the unknown SDK marked");
        Assert.Equal("MSB4236: The SDK 'Microsoft.NET.Sdk.Nowhere' specified could not be found.", MarkerOn(editor, document, "Microsoft.NET.Sdk.Nowhere").Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count(p => p.Severity == ProblemSeverity.Error) == 1, "Problems listing the one error the evaluation stopped on");
        window.Close();
    }

    [AvaloniaFact]
    public void Directory_packages_props_completes_versions_marks_a_vulnerable_one_and_takes_a_new_references_version()
    {
        Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Passenger.Sample" Version="$(SampleVersion)" />
                <PackageVersion Include="Passenger.Vulnerable" Version="1.0.0" />
              </ItemGroup>
            </Project>

            """);
        Write("App/App.csproj", Project.Replace(" Version=\"1.0.0\"", string.Empty, StringComparison.Ordinal));
        MainWindow window = Open("Directory.Packages.props");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument props = window.EditorArea.Active!;
        Ui.WaitFor(() => props.Markers.Any(m => m.Message.StartsWith("NU1902", StringComparison.Ordinal)), "the vulnerable version's warning");
        Assert.Equal(MarkerKind.Warning, MarkerOn(editor, props, "1.0.0").Kind);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count == 1, "Problems listing the vulnerable version");

        Ui.PlaceAfter(editor, "<PackageVersion Include=\"Passenger.Vulnerable\" Version=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count == 3, "the package's versions");
        Assert.Equal(["1.1.0", "1.0.1", "1.0.0"], window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<PackageVersion Include=\"Passenger.Vul");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the central version");
        Assert.Equal($"PackageVersion Passenger.Vulnerable\nVersion = 1.0.0\nPackage 'Passenger.Vulnerable' 1.0.0 has a known moderate severity vulnerability, {Advisory}\n"
            + "MSBuild item, 2 PackageVersion items in the project\nA package's version under central package management (Directory.Packages.props), which the projects' PackageReference items take",
            window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Version=\"$(SampleVer");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the property the version reads");
        Assert.Equal("SampleVersion = 2.0.0\nMSBuild property, set in Directory.Build.props, line 5", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Directory.Build.props", window.EditorArea.Active?.Name);
        Assert.Equal("    |<SampleVersion>2.0.0</SampleVersion>", CaretLine(window.EditorArea.CodeEditor));

        window.OpenFile(Path.Combine(folder, "App", "App.csproj"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        OpenDocument project = window.EditorArea.Active!;
        Ui.WaitFor(() => project.Markers.Any(m => m.Message.StartsWith("NU1902", StringComparison.Ordinal)), "the reference to the vulnerable central version warned of");
        Assert.Equal(MarkerKind.Warning, MarkerOn(editor, project, "Passenger.Vulnerable").Kind);
        Ui.PlaceAfter(editor, "<ProjectReference Include=\"../\" />");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("PackageReference"), "the item types an ItemGroup takes");
        window.KeyTextInput("PackageRef");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count == 3, "the feed's packages");
        window.KeyTextInput("Oth");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<PackageReference Include=\"Passenger.Other|\" />", CaretLine(editor), StringComparison.Ordinal);
        Assert.Contains("""
                <PackageVersion Include="Passenger.Other" Version="3.0.0" />
                <PackageVersion Include="Passenger.Sample" Version="$(SampleVersion)" />
            """.ReplaceLineEndings("\n"), props.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A lone '\r', U+0085, U+2028 and U+2029 end a line for the edits a workspace edit makes, not for the editor:
    /// the version a new reference takes goes into Directory.Packages.props, not open, beside the others below them.</summary>
    [AvaloniaFact]
    public void A_new_references_version_goes_beside_the_others_in_a_directory_packages_props_holding_line_breaks_only_roslyn_counts()
    {
        string props = $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <!-- Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c{'\r'}d -->
              <ItemGroup>
                <PackageVersion Include="Passenger.Sample" Version="$(SampleVersion)" />
                <PackageVersion Include="Passenger.Vulnerable" Version="1.0.0" />
              </ItemGroup>
            </Project>

            """;
        Write("Directory.Packages.props", props);
        Write("App/App.csproj", Project.Replace(" Version=\"1.0.0\"", string.Empty, StringComparison.Ordinal));
        MainWindow window = Open("App/App.csproj");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument project = window.EditorArea.Active!;
        Ui.WaitFor(() => project.Markers.Any(m => m.Message.StartsWith("NU1902", StringComparison.Ordinal)), "the reference to the vulnerable central version warned of");
        Ui.PlaceAfter(editor, "<ProjectReference Include=\"../\" />");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("PackageReference"), "the item types an ItemGroup takes");
        window.KeyTextInput("PackageRef");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count == 3, "the feed's packages");
        window.KeyTextInput("Oth");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal(props.Replace("    <PackageVersion Include=\"Passenger.Sample\"",
            "    <PackageVersion Include=\"Passenger.Other\" Version=\"3.0.0\" />\n    <PackageVersion Include=\"Passenger.Sample\"", StringComparison.Ordinal),
            File.ReadAllText(Path.Combine(folder, "Directory.Packages.props")));
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_file_saved_has_the_solution_view_and_the_language_server_take_what_it_says_now()
    {
        MainWindow window = Open("App/App.csproj", autosaves: true, languageServer: true);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.DoesNotContain("Passenger.Sample", Children(window, "Dependencies"));
        Assert.True(window.CSharpService?.Session is not null, "No language server started");
        string log = Directory.EnumerateFiles(Passenger.App.CSharp.CSharpService.LogFolder, $"server-*-{window.CSharpService!.Session!.Server.ProcessId}.log").Single();
        int Loads() => Ui.Shared(log).Split('\n').Count(l => l.Contains("Completed (re)load of all projects", StringComparison.Ordinal));
        int before = Loads();
        Ui.PlaceAfter(editor, "<ProjectReference Include=\"../\" />");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<PackageReference Include=\"Passenger.Sample\" Version=\"2.0.0\" />");
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.Save);
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Passenger.Sample"), "the Dependencies to show the package the saved project references");
        Assert.Contains(("Passenger.Sample", "2.0.0"), Children(window, "Dependencies").Select(name => (name, Row(window, name).Detail)));
        Ui.WaitFor(() => Loads() > before, "the language server to load the saved project again");
        window.Close();
    }

    /// <summary>A window on the solution, with no C# language server unless asked for, since the project files' help is the
    /// MSBuild host's; asked for, on the server the class's last window left.</summary>
    private MainWindow Open(string file, bool autosaves = false, bool languageServer = false)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = autosaves, WarmServer = shop.Server, StartsLanguageServer = languageServer };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        window.OpenFile(Path.Combine(folder, file));
        Ui.Settle();
        return window;
    }

    private static List<string> Children(MainWindow window, string name)
    {
        ExplorerTree tree = window.ExplorerView.Tree;
        if (tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project && r.Name == "App") is { IsExpanded: false } project)
        {
            tree.Expand(project);
        }
        ExplorerNode row = Row(window, name);
        tree.Expand(row);
        return [.. (row.Children ?? []).Select(c => c.Name)];
    }

    private static ExplorerNode Row(MainWindow window, string name) =>
        window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == name)
            ?? throw new InvalidOperationException($"The Explorer shows no {name}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

    /// <summary>The card over an item element: its item type's count, then the element's own items in the order of their names.</summary>
    private static string ItemCard(MainWindow window, string at)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, at);
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card over the item element");
        string[] lines = window.Assist.HoverText!.Split('\n');
        Ui.Press(window, "ESCAPE");
        string items = lines[0].Split(' ', 2) is [_, { } includes] ? string.Join(';', includes.Split(';').Order(StringComparer.Ordinal)) : lines[0];
        return $"{lines.FirstOrDefault(l => l.StartsWith("MSBuild item", StringComparison.Ordinal))}: {items}";
    }

    private static string CaretLine(CodeEditor editor) => XamlTests.CaretLine(editor);

    private static void Replace(CodeEditor editor, string what, string with) => XamlTests.Replace(editor, what, with);

    private static Marker MarkerOn(CodeEditor editor, OpenDocument document, string stretch) => XamlTests.MarkerOn(editor, document, stretch);

    private void Write(string relative, string text) => shop.Write(relative, text);

    /// <summary>The solution of App and Core over Directory.Build.props, its packages from a stand-in feed on loopback (for
    /// the audit too) into a package folder of its own, restored once for every test, and the server of the one test that
    /// asks the C# language server what it loaded.</summary>
    public sealed class Shop : SharedProject
    {
        private readonly StandInFeed feed = new();

        public Shop()
            : base("projectfiles-")
        {
            feed.Add("Passenger.Vulnerable", "1.0.0");
            feed.Add("Passenger.Vulnerable", "1.0.1");
            feed.Add("Passenger.Vulnerable", "1.1.0");
            feed.Add("Passenger.Sample", "2.0.0");
            feed.Add("Passenger.Other", "3.0.0");
            feed.Advise("Passenger.Vulnerable", 1, Advisory, "[1.0.0, 1.0.1)");
            // Only the stand-in, for packages and for the audit, and restores kept out of the person's package cache.
            Put("nuget.config", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
                  </packageSources>
                  <auditSources>
                    <clear />
                    <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
                  </auditSources>
                  <config>
                    <add key="globalPackagesFolder" value="packages" />
                  </config>
                </configuration>
                """);
            Put("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
            Put("Directory.Build.props", BuildProps);
            Put("App/App.csproj", Project);
            Put("App/Shared.targets", "<Project>\n  <Target Name=\"Greet\" BeforeTargets=\"Build\" />\n</Project>\n");
            Put("App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
            Put("Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Include=\"../Shared/*.cs\" />\n  </ItemGroup>\n</Project>\n");
            Put("Core/Store.cs", "class Store\n{\n}\n");
            Put("Shared/Price.cs", "class Price\n{\n}\n");
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            feed.Dispose();
        }

        private void Put(string relative, string text)
        {
            string path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }
}
