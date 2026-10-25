using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia XAML through the window, on an app made by <c>dotnet new avalonia.app</c> with a view model, a theme
/// palette and an asset of its own, and on Passenger's own XAML: what completion offers as a file is typed, read by
/// Passenger's own XAML from the project's referenced assemblies and its C#.</summary>
public sealed class XamlTests : IClassFixture<XamlTests.Gallery>
{
    private readonly Gallery app;

    public XamlTests(Gallery app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void Deeply_nested_markup_extensions_do_not_end_the_editor_and_diagnostics_still_finish()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            const int depth = 100_000;
            string nested = string.Concat(Enumerable.Repeat("{Binding Path=", depth)) + "Greeting" + new string('}', depth);
            Replace(editor, "{Binding Greeting}", nested);
            Replace(editor, "Content=\"Go\"", "Contnt=\"Go\"");
            Ui.WaitFor(() => window.EditorArea.Active!.Markers.Any(m => m.Message.Contains("Contnt", StringComparison.Ordinal)),
                "diagnostics after a deeply nested attribute");
            Assert.Contains(nested, editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
            Replace(editor, nested, "{Binding Greeting}");
            Replace(editor, "Contnt=\"Go\"", "Content=\"Go\"");
            Ui.WaitFor(() => window.EditorArea.Active!.Markers.Count == 0, "the repaired XAML's diagnostics");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Typing_a_tag_offers_the_controls_then_a_controls_properties_and_then_the_values_of_the_one_chosen()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<'");
        Assert.Contains("Border", window.Assist.CompletionLabels);
        window.KeyTextInput("Butt");
        Ui.Settle();
        Assert.Equal("Button", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button|", CaretLine(editor), StringComparison.Ordinal);

        window.KeyTextInput(" ");
        window.KeyTextInput("C");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Click"), "a Button's events among its attributes");
        Assert.Contains("Command", window.Assist.CompletionLabels);
        Assert.Contains("Canvas.Left", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's properties, its bases' included");
        window.KeyTextInput("orizontalA");
        Ui.Settle();
        Assert.Equal("HorizontalAlignment", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button HorizontalAlignment=\"|\"", CaretLine(editor), StringComparison.Ordinal);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Center"), "the enum's members offered inside the new value's quotes");
        Assert.Equal(["Center", "Left", "Right", "Stretch"], window.Assist.CompletionLabels.Order());
        window.KeyTextInput("Ce");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button HorizontalAlignment=\"Center|\"", CaretLine(editor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_value_chosen_with_nothing_typed_from_the_list_its_property_opened_leaves_the_caret_after_it_to_type_on()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<'");
        window.KeyTextInput("Butt");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        window.KeyTextInput("V");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("VerticalAlignment"), "a Button's properties");
        window.KeyTextInput("erticalA");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Center"), "the enum's members offered inside the new value's quotes");
        Assert.EndsWith("<Button VerticalAlignment=\"|\"", CaretLine(editor), StringComparison.Ordinal);

        // Chosen with nothing typed: the caret goes after the value, so the quote typed next steps over the closing one.
        string first = window.Assist.CompletionLabels[0];
        Ui.Press(window, "ENTER");
        Assert.True(CaretLine(editor).EndsWith($"<Button VerticalAlignment=\"{first}|\"", StringComparison.Ordinal),
            $"The value chosen with nothing typed left the caret before it, where typing on goes in front of it: {CaretLine(editor)}");
        window.KeyTextInput("\"");
        window.KeyTextInput(" ");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's properties after the value");
        Assert.EndsWith($"<Button VerticalAlignment=\"{first}\" H|", CaretLine(editor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_resource_reference_offers_the_keys_the_app_merges_in_and_a_binding_its_data_types_properties_through_a_path()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foreground=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Red"), "the named colours a brush takes");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("StaticResource"), "the markup extensions after a value's brace");
        Assert.Contains("Binding", window.Assist.CompletionLabels);
        Assert.Contains("x:Static", window.Assist.CompletionLabels);
        window.KeyTextInput("StaticRes");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Accent"), "the palette's keys, merged in by App.axaml");
        Assert.Contains("Brand", window.Assist.CompletionLabels);
        Assert.Contains("Gallery.Gap", window.Assist.CompletionLabels);

        Ui.PlaceAfter(editor, "Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the view model's properties, x:DataType's type's");
        Assert.Contains("Owner", window.Assist.CompletionLabels);
        Assert.Contains("Mode=", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        int greeting = editor.Document.Snapshot.ToString().IndexOf("{Binding Greeting}", StringComparison.Ordinal) + "{Binding ".Length;
        editor.ApplyEdits([new TextEdit(greeting, "Greeting".Length, "Owner.")]);
        editor.Selections = SelectionSet.Single(greeting + "Owner.".Length);
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Name"), "the path's next step's properties, Person's after Owner.");
        Assert.Contains("Age", window.Assist.CompletionLabels);
        Assert.DoesNotContain("Greeting", window.Assist.CompletionLabels);
        window.Close();
    }

    [AvaloniaFact]
    public void Passengers_own_main_window_completes_its_theme_tokens_and_its_style_classes()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        window.OpenFile(Path.Combine(Repository, "src", "Passenger.App", "MainWindow.axaml"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Background=\"{DynamicResource Passenger.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains(TokenKeys.Panel), "Tokens.axaml's keys, which App.axaml merges in");
        Assert.Contains(TokenKeys.Editor_Background, window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "<PathIcon Data=\"{StaticResource Icon.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains(IconKeys.Menu), "Icons.axaml's geometries");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<Button Name=\"MenuButton\" Classes=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("tool"), "the style classes Shell.axaml styles");
        Assert.Contains("widget", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<editors:");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("EditorArea"), "Passenger's own controls, from its C#, under their prefix");
        window.Close();
    }

    [AvaloniaFact]
    public void A_misspelt_property_enum_value_resource_and_binding_path_are_marked_as_typed_listed_in_problems_and_go_once_put_right()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Assert.Empty(document.Markers);
        Ui.PlaceAfter(editor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foregrund=\"Red\" HorizontalAlignment=\"Centre\"");
        Replace(editor, "{Binding Greeting}", "{Binding Greting}");
        Replace(editor, "{StaticResource Accent}", "{StaticResource Acent}");
        Ui.WaitFor(() => document.Markers.Count(m => m.Kind == MarkerKind.Error) == 4, "the four mistakes marked as errors once typing paused");
        Assert.Equal("Cannot resolve property 'Foregrund' on Button", MarkerOn(editor, document, "Foregrund").Message);
        Assert.Equal("'Centre' is not a value of HorizontalAlignment", MarkerOn(editor, document, "Centre").Message);
        Assert.Equal("Cannot resolve property 'Greting' on MainViewModel", MarkerOn(editor, document, "Greting").Message);
        Assert.Equal("Resource 'Acent' is not found", MarkerOn(editor, document, "Acent").Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count == 4, "Problems listing the file's four errors");
        Assert.All(window.BottomPanel.Problems.FileProblems, p => Assert.Equal(ProblemSeverity.Error, p.Severity));

        Replace(editor, "Foregrund", "Foreground");
        Replace(editor, "Greting", "Greeting");
        Ui.WaitFor(() => document.Markers.Count == 2, "the two put right no longer marked");
        Assert.DoesNotContain(document.Markers, m => m.Message.Contains("Foregrund", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>Markup extensions nested a hundred thousand deep in one value leave the file checked and the IDE running: a
    /// mistake beside them is marked and put right.</summary>
    [AvaloniaFact]
    public void A_value_of_markup_extensions_nested_a_hundred_thousand_deep_leaves_the_xaml_checked_and_the_ide_running()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        const int Depth = 100_000;
        string nested = string.Concat(Enumerable.Repeat("{Binding Path=", Depth)) + "Greeting" + new string('}', Depth);
        Replace(editor, "{Binding Greeting}", nested);
        Replace(editor, "Content=\"Go\"", "Contnt=\"Go\"");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("Contnt", StringComparison.Ordinal)), "the misspelt property beside the nested value marked");
        Replace(editor, nested, "{Binding Greeting}");
        Replace(editor, "Contnt=\"Go\"", "Content=\"Go\"");
        Ui.WaitFor(() => document.Markers.Count == 0, "the file, put back as it was, no longer marked");
        window.Close();
    }

    [AvaloniaFact]
    public void An_interface_typed_as_its_own_base_or_in_a_cycle_beside_open_xaml_leaves_the_xaml_checked_and_the_ide_running()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<TextBlock Text=\"{Binding #Go.Content}\" />");
        window.KeyTextInput("\n        <TextBlock Text=\"{Binding Subtitle}\" />\n        <TextBlock Text=\"{Binding Caption}\" />");
        Ui.WaitFor(() => view.Markers.Count == 2, "the two properties the view model lacks marked");

        // On the way to IMainViewModelBase the interface names itself, which a binding the view model lacks then asks of.
        window.OpenFile(app.PathOf("ViewModels/MainViewModel.cs"));
        Ui.Settle();
        CodeEditor code = window.EditorArea.CodeEditor;
        Replace(code, "public class MainViewModel\n{", "public interface IMainViewModel : IMainViewModel\n{\n}\n\npublic class MainViewModel : IMainViewModel\n{\n    public string Caption { get; set; } = \"\";\n");
        Ui.WaitFor(() => view.Markers.Count == 1, "the property typed into the view model no longer marked, its types read again");
        Assert.Equal("Cannot resolve property 'Subtitle' on MainViewModel", Assert.Single(view.Markers).Message);

        // Two interfaces, each the other's base.
        Replace(code, "public interface IMainViewModel : IMainViewModel\n{\n}", "public interface IMainViewModel : IViewModelBase\n{\n}\n\npublic interface IViewModelBase : IMainViewModel\n{\n}");
        Replace(code, "public string Caption", "public string Heading");
        Ui.WaitFor(() => view.Markers.Count == 2, "the property renamed in the view model marked again, its types read again");
        Assert.Equal(["Cannot resolve property 'Caption' on MainViewModel", "Cannot resolve property 'Subtitle' on MainViewModel"], view.Markers.Select(m => m.Message).Order());

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Subt");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding's line");
        Assert.Equal("Create property 'Subtitle'", window.Assist.Offered[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void A_character_reference_to_half_a_surrogate_pair_leaves_its_file_and_the_projects_others_with_their_help()
    {
        // An emoji written as two references, each half a UTF-16 pair: no character XML allows, read as written.
        string emoji = app.Write("Emoji.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Emoji">
                <StackPanel>
                    <TextBlock Text="&#xD83D;&#xDE00;" />
                    <TextBlock Text="&#55357;" />
                </StackPanel>
            </UserControl>

            """);
        app.Write("Emoji.axaml.cs", "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class Emoji : UserControl\n{\n}\n");
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument view = window.EditorArea.Active!;
        Ui.WaitFor(() => window.XamlService.For(view)?.IsLoaded == true, "the project's XAML read, the file with the references among it");
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        window.KeyTextInput("\n        <TextBlock Text=\"&#xD83D;\" />\n        ");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered beside a typed reference to half a pair");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        Ui.Settle();
        Assert.Empty(view.Markers);

        window.OpenFile(emoji);
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<TextBlock Text=\"&#55357;\"");
        window.KeyTextInput(" ");
        window.KeyTextInput("F");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Foreground"), "a TextBlock's properties in the file holding the references");
        window.Close();
    }

    [AvaloniaFact]
    public void A_property_added_to_a_view_model_in_a_referenced_project_reaches_the_xaml_that_binds_to_it()
    {
        // The usual layout: the app's XAML in Shop, its view models in Shop.Core, which Shop references.
        string folder = PathNames.Canonical(TestFolders.Create("xaml-core-"));
        string shop = Path.Combine(folder, "Shop");
        string core = Path.Combine(folder, "Shop.Core");
        string viewModel = Path.Combine(core, "ViewModels", "MainViewModel.cs");
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(shop, "Shop");
                string project = Path.Combine(shop, "Shop.csproj");
                File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
                    "  <ItemGroup>\n    <ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
                Directory.CreateDirectory(Path.GetDirectoryName(viewModel)!);
                File.WriteAllText(Path.Combine(core, "Shop.Core.csproj"),
                    TestProjects.Library("<Nullable>enable</Nullable>"));
                File.WriteAllText(viewModel, "namespace Shop.Core.ViewModels;\n\npublic class MainViewModel\n{\n    public string Greeting { get; set; } = \"Hello\";\n}\n");
                File.WriteAllText(Path.Combine(shop, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:vm="using:Shop.Core.ViewModels"
                            x:Class="Shop.MainWindow"
                            x:DataType="vm:MainViewModel"
                            Title="Shop">
                        <StackPanel>
                            <TextBlock Text="{Binding Greeting}" />
                            <TextBlock Text="{Binding Subtitle}" />
                            <TextBlock Text="{Binding Caption}" />
                        </StackPanel>
                    </Window>

                    """);
                Restore.Run(shop);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            OpenDocument view = window.EditorArea.Active!;
            Ui.WaitFor(() => view.Markers.Count == 2, "the two properties the referenced project's view model lacks marked");
            Assert.Equal(["Cannot resolve property 'Caption' on MainViewModel", "Cannot resolve property 'Subtitle' on MainViewModel"], view.Markers.Select(m => m.Message).Order());

            // Written on disk (a pull, another editor), and typed into the view model open in the editor.
            File.WriteAllText(viewModel, "namespace Shop.Core.ViewModels;\n\npublic class MainViewModel\n{\n    public string Greeting { get; set; } = \"Hello\";\n\n    public string Subtitle { get; set; } = \"\";\n}\n");
            Ui.WaitFor(() => view.Markers.Count == 1, "the property written into the referenced project's view model no longer marked");
            window.OpenFile(viewModel);
            Ui.Settle();
            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "public string Subtitle { get; set; } = \"\";");
            window.KeyTextInput("\n\n    public string Caption { get; set; } = \"\";");
            Ui.WaitFor(() => view.Markers.Count == 0, "the property typed into the referenced project's view model no longer marked");

            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Text=\"{Binding ");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Caption"), "the property typed into the referenced project offered in a binding");
            Assert.Contains("Subtitle", window.Assist.CompletionLabels);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A project whose folders link back up its own tree gets its XAML help, and Go to Declaration from a named
    /// element's field in the code-behind finds the XAML that declares its class, deep among those links.</summary>
    [AvaloniaFact]
    public void A_project_holding_folder_links_back_up_its_own_tree_gets_its_xaml_help()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-links-"));
        string shop = Path.Combine(folder, "Shop");
        string views = Path.Combine(shop, "Views");
        string deep = Path.Combine(views, "Parts", "Deep");
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(shop, "Shop");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(views, "Parts", "Palette.axaml"), Gallery.Palette);
                File.WriteAllText(Path.Combine(deep, "Card.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 x:Class="Shop.Views.Card">
                        <Button Name="Pay" Content="Pay" />
                    </UserControl>

                    """);
                File.WriteAllText(Path.Combine(deep, "Card.axaml.cs"), """
                    using Avalonia.Controls;

                    namespace Shop.Views;

                    public partial class Card : UserControl
                    {
                        public Card()
                        {
                            InitializeComponent();
                            Pay.Content = "Pay now";
                        }
                    }

                    """);
                File.WriteAllText(Path.Combine(shop, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            x:Class="Shop.MainWindow"
                            Title="Shop">
                        <StackPanel>
                        </StackPanel>
                    </Window>

                    """);
                Restore.Run(shop);
            }, folder);
            // Links to the folder each is in and the one above it: a walk following them would double the folders each level.
            TestFolders.LinkFolder(Path.Combine(shop, "self"), shop);
            TestFolders.LinkFolder(Path.Combine(shop, "up"), folder);
            TestFolders.LinkFolder(Path.Combine(views, "here"), views);
            TestFolders.LinkFolder(Path.Combine(views, "Parts", "up"), views);

            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "<StackPanel>");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("<");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<', once the project's XAML files are read");
            Ui.Press(window, "ESCAPE");

            window.OpenFile(Path.Combine(deep, "Card.axaml.cs"));
            Ui.WaitFor(() => window.CSharpService?.IsReady == true, "the language server to load Shop");
            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "        Pa");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Card.axaml", "Go to Declaration of Pay to land in the XAML declaring Card, found among the links");
            Assert.Contains("<Button Name=\"|Pay\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
            Assert.Equal(PathNames.Canonical(Path.Combine(deep, "Card.axaml")), window.EditorArea.Active?.Path);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_project_that_takes_avalonia_while_its_xaml_is_open_gets_its_xaml_help_once_the_restore_lands()
    {
        // A library with XAML in it but no Avalonia yet: nothing Passenger reads until the package is added and restored.
        string folder = PathNames.Canonical(TestFolders.Create("xaml-later-"));
        string project = Path.Combine(folder, "Later.csproj");
        const string Plain = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n";
        try
        {
            TestFolders.Prepare(() =>
            {
                File.WriteAllText(project, Plain + "</Project>\n");
                File.WriteAllText(Path.Combine(folder, "MainView.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                        <StackPanel>
                            <TextBlock Text="Later" />
                        </StackPanel>
                    </UserControl>

                    """);
                Restore.Run(folder);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(folder, "MainView.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Passenger.App.Xaml.XamlDocument xaml = window.XamlService.For(document) ?? throw new InvalidOperationException("The XAML file of a C# project was given no XAML");
            Ui.WaitFor(() => xaml.Loaded.IsCompleted, "the project read and found to be neither Avalonia's nor WPF's");
            Assert.False(xaml.IsLoaded, "A project referencing neither Avalonia nor WPF had its XAML read as one of theirs");

            // Avalonia added, as NuGet's window or dotnet add package adds it, and restored.
            File.WriteAllText(project, $"{Plain}  <ItemGroup>\n    <PackageReference Include=\"Avalonia\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            // A tag begun before the restore lands takes its list from the project as it was, asked again once it is read.
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "<TextBlock Text=\"Later\" />");
            window.KeyTextInput("\n        ");
            window.KeyTextInput("<");
            Restore.Run(folder);
            Ui.WaitFor(() => xaml.IsLoaded, "the project's XAML read as Avalonia's once the package it now references was restored", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered, once the restore was read, after a tag's '<' typed before it landed");
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, "BACK_SPACE");
            window.KeyTextInput("<");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<' once the project references it");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_package_restored_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-restore-"));
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(folder, "Fresh");
                File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:h="using:Avalonia.Headless"
                            x:Class="Fresh.MainWindow"
                            Title="Fresh">
                        <Window.Resources>
                            <h:AvaloniaHeadlessPlatformOptions x:Key="Headless" />
                        </Window.Resources>
                        <TextBlock Text="Welcome" />
                    </Window>

                    """);
                Restore.Run(folder);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(folder, "MainWindow.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("AvaloniaHeadlessPlatformOptions", StringComparison.Ordinal)),
                "the type of a package the app does not reference marked");

            // The package added where only its restore says so: the restore's assets are what the XAML reads it from.
            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"),
                $"<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Avalonia.Headless\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            Restore.Run(folder);
            Ui.WaitFor(() => document.Markers.Count == 0, "the package's type resolved once its restore landed", TimeSpan.FromSeconds(30));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The same where the solution keeps its build output in artifacts/ (UseArtifactsOutput), its project in a folder
    /// below: the restore writes artifacts/obj/Fresh/project.assets.json, under no project's folder, on either way of
    /// watching (each folder on its own, as on Linux, or the whole tree).</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_package_restored_into_artifacts_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands(bool eachFolder)
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-artifacts-"));
        string app = Path.Combine(folder, "src", "Fresh");
        const string Artifacts = "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n";
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(app, "Fresh");
                File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), Artifacts + "</Project>\n");
                File.WriteAllText(Path.Combine(app, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:h="using:Avalonia.Headless"
                            x:Class="Fresh.MainWindow"
                            Title="Fresh">
                        <Window.Resources>
                            <h:AvaloniaHeadlessPlatformOptions x:Key="Headless" />
                        </Window.Resources>
                        <TextBlock Text="Welcome" />
                    </Window>

                    """);
                Restore.Run(app);
            }, folder);
            Assert.True(File.Exists(Path.Combine(folder, "artifacts", "obj", "Fresh", "project.assets.json")), "The restore wrote its assets somewhere other than artifacts/obj/Fresh");
            MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, Watching = eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree };
            window.Show();
            window.OpenFolder(folder);
            window.OpenFile(Path.Combine(app, "MainWindow.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("AvaloniaHeadlessPlatformOptions", StringComparison.Ordinal)),
                "the type of a package the app does not reference marked");

            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"),
                $"{Artifacts}  <ItemGroup>\n    <PackageReference Include=\"Avalonia.Headless\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            Restore.Run(app);
            Ui.WaitFor(() => document.Markers.Count == 0, "the package's type resolved once its restore landed in artifacts/", TimeSpan.FromSeconds(30));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void An_x_class_no_csharp_declares_is_marked_as_avalonias_compiler_refuses_it()
    {
        // Avalonia's compiler makes no class for it: it says "Unable to find type" (AVLN2000).
        string badge = app.Write("Badge.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Badge">
                <TextBlock Text="New" />
            </UserControl>

            """);
        MainWindow window = app.Open(badge);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count > 0, "the class nothing declares marked");
        Marker marked = MarkerOn(editor, document, "Gallery.Badge");
        Assert.Equal(MarkerKind.Error, marked.Kind);
        Assert.Equal("Cannot resolve class 'Gallery.Badge'", marked.Message);
        window.Close();
    }

    /// <summary>After a build the XAML compiler's error (a number only it reads) is marked on its line and listed with its
    /// code. Avalonia's XAML compiler counts lines as an XML reader does, ending one at the lone '\r' in the window's tag,
    /// which the editor reads inside its line, so below it the compiler's line runs one ahead of the editor's: its error is
    /// listed, marked, said in its message and gone to from the Build window's link at the editor's line, and an agent is
    /// told that line. The line edited, the build's word on it is the build's, no longer this text's.</summary>
    [AvaloniaFact]
    public void After_a_build_the_xaml_compilers_error_below_a_lone_carriage_return_is_listed_marked_and_gone_to_at_the_editors_line()
    {
        string xaml = app.Write("MainWindow.axaml", Gallery.Window
            .Replace("d:DesignWidth=\"800\" d:DesignHeight", "d:DesignWidth=\"800\"\r d:DesignHeight", StringComparison.Ordinal)
            .Replace("<TextBlock Name=\"Title\"", "<TextBlock Name=\"Title\" Width=\"wide\"", StringComparison.Ordinal));
        MainWindow window = app.Open(xaml);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        string text = editor.Document.Snapshot.ToString();
        TextPosition width = editor.Document.Snapshot.GetPosition(text.IndexOf("Width=\"wide\"", StringComparison.Ordinal));
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Problem built = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == "AVLN1000");
        Assert.True(built.Position == width, $"The XAML compiler's error is listed at {built.Where}, not MainWindow.axaml:{width.Line + 1}:{width.Column + 1}");
        Assert.Contains($"Line {width.Line + 1}, position {width.Column + 1}", built.Message, StringComparison.Ordinal);
        Marker marked = MarkerOn(editor, document, "Width");
        Assert.Equal(MarkerKind.Error, marked.Kind);
        Assert.StartsWith("AVLN1000: The input string 'wide' was not in a correct format.", marked.Message, StringComparison.Ordinal);

        // The compiler's message in the Build window names its own line, one on; its link goes to the editor's.
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Build;
        console.Flush();
        string message = console.Document.Snapshot.ToString().Split('\n').First(l => l.Contains("AVLN1000", StringComparison.Ordinal));
        Assert.True(message.Contains($"MainWindow.axaml({width.Line + 2},{width.Column + 1}", StringComparison.Ordinal), $"The Build window does not name the compiler's line: {message}");
        editor.GoTo(new TextPosition(0, 0));
        Ui.ClickLink(console, message);
        Assert.Equal(PathNames.Canonical(xaml), window.EditorArea.Active?.Path);
        Assert.True(editor.CaretPosition == width, $"The Build window's link went to {editor.CaretPosition.Line + 1}:{editor.CaretPosition.Column + 1}, not {width.Line + 1}:{width.Column + 1}");

        Passenger.App.Mcp.ProblemsInfo told = (Passenger.App.Mcp.ProblemsInfo)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Diagnostics(), "the problems an agent asks for");
        Assert.Equal((width.Line + 1, width.Column + 1), told.Build.Where(f => f.Path == "MainWindow.axaml").Select(f => (f.Line, f.Column)).Single());

        Replace(editor, "\"wide\"", "\"120\"");
        Ui.Settle();
        Assert.Empty(document.Markers);
        window.Close();
    }

    [AvaloniaFact]
    public void Passengers_own_xaml_which_compiles_is_marked_with_no_problem()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        string[] files = [.. Directory.EnumerateFiles(Path.Combine(Repository, "src", "Passenger.App"), "*.axaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        Assert.True(files.Length >= 9, $"Passenger.App has only {files.Length} XAML files");
        foreach (string file in files)
        {
            window.OpenFile(file);
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.XamlService.For(document)?.IsLoaded, $"{Path.GetFileName(file)} was not read as XAML");
            Assert.True(document.Markers.Count == 0,
                $"{Path.GetFileName(file)}, which compiles, is marked: {string.Join("; ", document.Markers.Select(m => $"{document.Text.Snapshot.GetPosition(m.Range.Start).Line + 1}: {m.Message}"))}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_declaration_from_xaml_opens_a_controls_class_a_resources_definition_and_a_bound_property()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "x:Class=\"Gallery.Main");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.axaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public partial class |MainWindow", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Butt");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card describing the tag's type");
        Assert.Equal("class Avalonia.Controls.Button : ContentControl", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        OpenDocument library = window.EditorArea.Active!;
        Assert.Equal("Avalonia.Controls.Button.cs", library.Name);
        Assert.True(library.IsReadOnly, "the library's code opened editable");
        Assert.Contains("class |Button", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "{StaticResource Acc");
        Ui.Run(window, AppActions.GoToDeclaration);
        // A key the theme's dark and light dictionaries both define: a place to choose for each.
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "Accent's dark and light definitions to choose from");
        Assert.Equal(["Accent (Dark) in Palette.axaml", "Accent (Light) in Palette.axaml"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("Palette.axaml", window.EditorArea.Active?.Name);
        Assert.Contains("<SolidColorBrush x:Key=\"|Accent\" Color=\"#3574F0\" />", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "{Binding Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Greeting", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void An_elements_name_renames_with_its_code_behinds_uses_and_go_to_declaration_from_the_code_lands_on_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        CodeEditor code = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(code, "        G");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.axaml", window.EditorArea.Active?.Name);
        CodeEditor xaml = window.EditorArea.CodeEditor;
        Assert.Contains("<Button Name=\"|Go\"", CaretLine(xaml), StringComparison.Ordinal);

        Ui.Run(window, AppActions.FindUsages);
        Ui.Settle();
        Ui.WaitFor(() => window.Find.Rows.Count > 0, "the usages in the Find window");
        IReadOnlyList<string> found = [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];
        Assert.Contains(found, f => f.Contains("Go.Content = \"Start\";", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("{Binding #Go.Content}", StringComparison.Ordinal));

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the button's name");
        window.KeyTextInput("Start");
        Ui.Press(window, "ENTER");
        OpenDocument behind = window.EditorArea.Documents.First(d => d.Name == "MainWindow.axaml.cs");
        Ui.WaitFor(() => behind.Text.Snapshot.ToString().Contains("Start.Content = \"Start\";", StringComparison.Ordinal), "the code-behind's use renamed with it");
        string renamed = window.EditorArea.Documents.First(d => d.Name == "MainWindow.axaml").Text.Snapshot.ToString();
        Assert.Contains("<Button Name=\"Start\"", renamed, StringComparison.Ordinal);
        Assert.Contains("{Binding #Start.Content}", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("Name=\"Go\"", renamed, StringComparison.Ordinal);
        List<string> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.Equal(["Undo Rename Go to Start? It changes MainWindow.axaml.cs and MainWindow.axaml."], asked);
        Assert.Contains("Go.Content = \"Start\";", behind.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_key_one_theme_variant_lacks_is_warned_of_where_defined_and_used_and_the_fix_adds_it_to_that_variant()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foreground=\"{DynamicResource Danger}\"");
        window.OpenFile(app.PathOf("Styles/Palette.axaml"));
        Ui.Settle();
        CodeEditor palette = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(palette, "<SolidColorBrush x:Key=\"Muted\" Color=\"#868A91\" />");
        window.KeyTextInput("\n            <SolidColorBrush x:Key=\"Danger\" Color=\"#E55765\" />");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning on the key only Dark defines");
        Assert.Equal("'Danger' is defined for Dark but not for Light", MarkerOn(palette, document, "Danger").Message);
        Ui.WaitFor(() => view.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning on the window's use of it, from the palette as typed");
        Marker used = Assert.Single(view.Markers);
        Assert.Equal("'Danger' is not defined for Light", used.Message);

        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fix offered on the key's line");
        Assert.Equal(["Add 'Danger' to Light"], window.Assist.Offered);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("<SolidColorBrush x:Key=\"Muted\" Color=\"#6C707E\" />\n            <SolidColorBrush x:Key=\"Danger\" Color=\"#E55765\" />\n        </ResourceDictionary>",
            palette.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.WaitFor(() => document.Markers.Count == 0 && view.Markers.Count == 0, "both warnings gone once Light has the key too");
        window.Close();
    }

    [AvaloniaFact]
    public void An_avares_path_completes_from_the_projects_assets_is_warned_of_when_missing_and_goes_to_its_file()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        window.KeyTextInput("\n        <Image Source=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("avares://Gallery/Assets/logo.png"), "the project's asset as an avares:// path");
        Assert.Contains("avares://Gallery/Styles/Palette.axaml", window.Assist.CompletionLabels);
        window.KeyTextInput("logo");
        Ui.Settle();
        Assert.Equal("avares://Gallery/Assets/logo.png", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Image Source=\"avares://Gallery/Assets/logo.png|\"", CaretLine(editor), StringComparison.Ordinal);
        Ui.Press(window, "END");
        window.KeyTextInput(" />");
        Ui.Settle();
        Assert.Empty(document.Markers);

        Replace(editor, "logo.png", "logo2.png");
        Ui.WaitFor(() => document.Markers.Count == 1, "the warning on the asset that is not there");
        Assert.Equal("'avares://Gallery/Assets/logo2.png' is not found", document.Markers[0].Message);
        Assert.Equal(MarkerKind.Warning, document.Markers[0].Kind);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Source=\"avares://Gallery/Sty");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Palette.axaml", window.EditorArea.Active?.Name);
        window.Close();
    }

    [AvaloniaFact]
    public void Colours_show_as_gutter_swatches_and_one_chosen_in_the_picker_is_written_back_as_it_was_written()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument view = window.EditorArea.Active!;
        int button = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("<Button Name=\"Go\"", StringComparison.Ordinal));
        // The button's background names the palette's Accent: its swatch is the dark theme's, which the window shows.
        Ui.WaitFor(() => view.ColourMarks.Any(m => m.Line == button), "the swatch of the brush the button's background names");
        Assert.Equal(Color.Parse("#3D7BF5"), view.ColourMarks.Single(m => m.Line == button).Colour);
        Point swatch = editor.TranslatePoint(editor.GutterColumnPoint(GutterColumn.Marks, button), window)!.Value;
        Assert.Equal(Color.Parse("#3D7BF5"), Ui.Pixel(window, swatch));

        window.MouseDown(swatch, MouseButton.Left);
        window.MouseUp(swatch, MouseButton.Left);
        Ui.Settle();
        Assert.Equal("#3D7BF5", window.PickingColour);
        window.KeyTextInput("#E2A33F");
        Ui.Settle();
        Assert.Equal("#E2A33F", window.PickingColour);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Null(window.PickingColour);
        string palette = File.ReadAllText(app.PathOf("Styles/Palette.axaml"));
        Assert.Contains("<SolidColorBrush x:Key=\"Accent\" Color=\"#E2A33F\" />", palette, StringComparison.Ordinal);
        Assert.Contains("<SolidColorBrush x:Key=\"Accent\" Color=\"#3574F0\" />", palette, StringComparison.Ordinal);
        Ui.WaitFor(() => view.ColourMarks.SingleOrDefault(m => m.Line == button).Colour == Color.Parse("#E2A33F"), "the swatch following the palette");

        window.OpenFile(app.PathOf("MainWindow.axaml.cs"));
        Ui.Settle();
        CodeEditor code = window.EditorArea.CodeEditor;
        OpenDocument behind = window.EditorArea.Active!;
        Ui.PlaceAfter(code, "Go.Content = \"Start\";");
        window.KeyTextInput("\n        Go.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x33, 0x66, 0x99));");
        int line = code.CaretPosition.Line;
        Ui.WaitFor(() => behind.ColourMarks.Any(m => m.Line == line), "the swatch of the colour FromRgb makes");
        Assert.Equal(Color.Parse("#336699"), behind.ColourMarks.Single(m => m.Line == line).Colour);
        Ui.Click(code, code.GutterColumnPoint(GutterColumn.Marks, line));
        window.KeyTextInput("#AA0000");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(170, 0, 0));", code.Document.Snapshot.GetLineText(line), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_declaration_reaches_the_themes_own_xaml_for_a_control_a_template_part_and_a_theme_resource()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        // Fluent's sources as its Source Link gives them, stood in: FluentControls.xaml merges Button.xaml in.
        ThemeSources sources = new(TestFolders.Create("xaml-theme-"));
        window.XamlService.LibraryFiles = sources;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Butt");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
        Assert.Equal(["Avalonia.Controls.Button", "Button's control theme"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        OpenDocument theme = window.EditorArea.Active!;
        Assert.Equal("Button.xaml", theme.Name);
        Assert.True(theme.IsReadOnly, "the theme's XAML opened editable");
        Assert.Contains("<|ControlTheme x:Key=\"{x:Type Button}\" TargetType=\"Button\">", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        Assert.Equal([("Avalonia.Themes.Fluent", "/Controls/FluentControls.xaml"), ("Avalonia.Themes.Fluent", "/Controls/Button.xaml")], sources.Asked);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<FluentTheme />");
        window.KeyTextInput("\n        <Style Selector=\"Button /template/ ContentPresenter#PART_ContentPresenter\">\n            <Setter Property=\"Background\" Value=\"{DynamicResource ButtonBackground}\" />\n        </Style>");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "#PART_Con");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Button.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("<ContentPresenter x:Name=\"|PART_ContentPresenter\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{DynamicResource ButtonBack");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Button.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("x:Key=\"|ButtonBackground\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_themes_include_that_climbs_out_of_its_sources_folder_is_refused_and_nothing_is_written_outside_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        Passenger.App.Xaml.XamlDocument xaml = window.XamlService.For(window.EditorArea.Active!)!;
        Ui.WaitFor(() => xaml.IsLoaded, "the project's XAML read");
        // Fluent's control list as an earlier session's Source Link gave it, merging in a file five folders up.
        Passenger.Xaml.Metadata.AssemblyTypes fluent = xaml.Loaded.Result!.Types.Assemblies.Single(a => a.Name == "Avalonia.Themes.Fluent");
        string entry = Path.Combine(Passenger.App.Xaml.LibrarySources.Folder, "SourceLink", fluent.Name,
            Passenger.App.Xaml.LibrarySources.Stamp(Passenger.App.Xaml.LibrarySources.Implementation(fluent.Path)));
        string escaped = Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "escaped");
        try
        {
            Directory.CreateDirectory(Path.Combine(entry, "Controls"));
            File.WriteAllText(Path.Combine(entry, "Controls", "FluentControls.xaml"),
                "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\">\n  <ResourceDictionary.MergedDictionaries>\n"
                + "    <MergeResourceInclude Source=\"../../../../../escaped/Button.xaml\" />\n  </ResourceDictionary.MergedDictionaries>\n</ResourceDictionary>\n");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Butt");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status?.Contains("leads outside its sources' folder", StringComparison.Ordinal) == true, "the include that climbs out refused, saying why");
            Assert.Equal("MainWindow.axaml", window.EditorArea.Active?.Name);
            Assert.False(Directory.Exists(escaped), $"Source Link wrote outside its folder: {escaped}");
            window.Close();
        }
        finally
        {
            // The run's other tests ask Source Link for Fluent's files afresh.
            string versions = Path.Combine(Passenger.App.Xaml.LibrarySources.Folder, "SourceLink", fluent.Name);
            if (Directory.Exists(versions))
            {
                Directory.Delete(versions, recursive: true);
            }
        }
    }

    /// <summary>A theme library's files as its Source Link would give them, written into a folder of the test's; what was
    /// asked for, in order.</summary>
    private sealed class ThemeSources(string folder) : Passenger.App.Xaml.ILibraryFiles
    {
        public List<(string Assembly, string Path)> Asked { get; } = [];

        public Task<string?> FileAsync(string assembly, string assemblyName, string path, IProgress<string>? why, CancellationToken cancel)
        {
            Asked.Add((assemblyName, path));
            string? text = path switch
            {
                "/Controls/FluentControls.xaml" => "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
                    + "  <ResourceDictionary.MergedDictionaries>\n    <MergeResourceInclude Source=\"Button.xaml\" />\n  </ResourceDictionary.MergedDictionaries>\n</ResourceDictionary>\n",
                "/Controls/Button.xaml" => "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n"
                    + "  <StaticResource x:Key=\"ButtonBackground\" ResourceKey=\"ControlFillColorDefaultBrush\" />\n"
                    + "  <ControlTheme x:Key=\"{x:Type Button}\" TargetType=\"Button\">\n    <Setter Property=\"Template\">\n      <ControlTemplate>\n"
                    + "        <ContentPresenter x:Name=\"PART_ContentPresenter\" Content=\"{TemplateBinding Content}\" />\n      </ControlTemplate>\n    </Setter>\n"
                    + "  </ControlTheme>\n</ResourceDictionary>\n",
                _ => null,
            };
            if (text is null)
            {
                why?.Report($"{path} is not in the stand-in");
                return Task.FromResult<string?>(null);
            }
            // Kept as Source Link's files are, under a MetadataAsSource folder, which the editor shows read-only.
            string local = Path.Combine(folder, "MetadataAsSource", "SourceLink", assemblyName, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, text);
            return Task.FromResult<string?>(local);
        }
    }

    [AvaloniaFact]
    public void The_preview_draws_a_window_from_the_last_build_follows_its_text_as_it_is_typed_and_draws_styles_as_their_PreviewWith()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.True(group.PreviewToggle.IsVisible && group.PreviewToggle.IsChecked == false && ActionTips.Of(group.PreviewToggle) == "XAML Preview",
            $"The XAML file's tab strip offers no XAML Preview toggle (shown {group.PreviewToggle.IsVisible}, tip {ActionTips.Of(group.PreviewToggle)})");
        Ui.Run(window, AppActions.FindAction);
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        popup.Query = "xaml preview";
        Ui.Settle();
        Assert.Equal(AppActions.XamlPreview, popup.Rows[0].Id);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Assert.True(group.PreviewToggle.IsChecked == true, "The tab strip's preview toggle stayed up while XAML Preview was on");
        Ui.WaitFor(() => preview.Frame is not null, "the previewer's first frame", TimeSpan.FromSeconds(60));
        Ui.Settle();
        Assert.Null(preview.Error);
        // Drawn at the size the window's design-time width and height give it.
        Assert.Equal(new PixelSize(800, 450), preview.Frame!.PixelSize);
        Avalonia.Controls.Image frame = window.PreviewPaneOf(document)!.Frame;
        // A point of the panel below its children, where its background shows.
        Color Below() => Ui.Pixel(window, frame.TranslatePoint(new Point(400, 300), window) ?? throw new InvalidOperationException("The preview's frame is not on the window"));
        Color orange = Color.Parse("#E2A33F");
        Assert.NotEqual(orange, Below());

        Ui.PlaceAfter(editor, "<StackPanel");
        window.KeyTextInput(" Background=\"#E2A33F\"");
        Ui.WaitFor(() => Below() == orange, "the preview drawing the panel's new background once typing paused", TimeSpan.FromSeconds(30));

        // The host reads the lone '\r' in the window's tag as a line's end, which the editor reads inside the line.
        Replace(editor, "d:DesignWidth=\"800\" d:DesignHeight", "d:DesignWidth=\"800\"\r d:DesignHeight");
        Replace(editor, "</StackPanel>", "</StackPane>");
        Ui.WaitFor(() => preview.Error is not null, "the previewer saying why the XAML did not load", TimeSpan.FromSeconds(30));
        Assert.Equal(13, editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("</StackPane>", StringComparison.Ordinal)));
        Assert.Contains("(line 14, column ", preview.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("15", preview.Error, StringComparison.Ordinal);
        Assert.Equal(orange, Below());
        Replace(editor, "</StackPane>", "</StackPanel>");
        Ui.WaitFor(() => preview.Error is null, "the error gone once the XAML loads again", TimeSpan.FromSeconds(30));

        string styles = app.PathOf("Styles/Buttons.axaml");
        File.WriteAllText(styles, Gallery.Buttons);
        window.OpenFile(styles);
        Ui.Settle();
        OpenDocument shown = window.EditorArea.Active!;
        Passenger.App.Xaml.XamlPreview styled = window.PreviewOf(shown) ?? throw new InvalidOperationException("The styles opened while previewing showed no preview");
        Ui.WaitFor(() => styled.Frame is not null, "the styles' preview", TimeSpan.FromSeconds(60));
        Avalonia.Controls.Image button = window.PreviewPaneOf(shown)!.Frame;
        Ui.WaitFor(() => button.TranslatePoint(new Point(24, styled.FrameSize.Height / 2), window) is { } inside && Ui.Pixel(window, inside) == orange,
            "the button Design.PreviewWith holds drawn in the style's colour", TimeSpan.FromSeconds(30));
        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Assert.True(window.PreviewOf(shown) is null && group.PreviewToggle.IsChecked == false, "The tab strip's toggle left XAML Preview on");
        window.Close();
    }

    /// <summary>An app still on Avalonia 11, whose package carries its previewer under tools/netstandard2.0 where Avalonia
    /// 12's are under tools/net8.0 and later, and whose Directory.Build.props moves its obj and bin folders, previews as
    /// any app does: turned on from its file's tab strip before the app was ever restored, it draws once Build has
    /// restored and built it.</summary>
    [AvaloniaFact]
    public void An_app_on_Avalonia_11_with_its_obj_and_bin_folders_moved_previews_from_its_packages_previewer()
    {
        string folder = TestFolders.Create("avalonia11-");
        string version = AvaloniaApp.Metadata("AvaloniaOlderVersion");
        TestFolders.Write(folder, "Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)build/obj/</BaseIntermediateOutputPath>
                <BaseOutputPath>$(MSBuildThisFileDirectory)build/bin/</BaseOutputPath>
              </PropertyGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Eleven.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{{TestProjects.Framework}}</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Avalonia" Version="{{version}}" />
                <PackageReference Include="Avalonia.Desktop" Version="{{version}}" />
                <PackageReference Include="Avalonia.Themes.Fluent" Version="{{version}}" />
              </ItemGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Program.cs", """
            using Avalonia;

            namespace Eleven;

            internal static class Program
            {
                [System.STAThread]
                public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

                public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
            }
            """);
        TestFolders.Write(folder, "App.axaml", """
            <Application xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Eleven.App">
                <Application.Styles>
                    <FluentTheme />
                </Application.Styles>
            </Application>
            """);
        TestFolders.Write(folder, "App.axaml.cs", """
            using Avalonia;
            using Avalonia.Controls.ApplicationLifetimes;
            using Avalonia.Markup.Xaml;

            namespace Eleven;

            public partial class App : Application
            {
                public override void Initialize() => AvaloniaXamlLoader.Load(this);

                public override void OnFrameworkInitializationCompleted()
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.MainWindow = new MainWindow();
                    }
                    base.OnFrameworkInitializationCompleted();
                }
            }
            """);
        TestFolders.Write(folder, "MainWindow.axaml", """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    mc:Ignorable="d" d:DesignWidth="400" d:DesignHeight="300" x:Class="Eleven.MainWindow" Title="Eleven">
                <StackPanel Background="#E2A33F">
                    <TextBlock Text="On Avalonia 11" />
                </StackPanel>
            </Window>
            """);
        TestFolders.Write(folder, "MainWindow.axaml.cs", """
            using Avalonia.Controls;

            namespace Eleven;

            public partial class MainWindow : Window
            {
                public MainWindow() => InitializeComponent();
            }
            """);
        AvaloniaApp.UseFeed(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("The tab strip's toggle showed no preview beside the file");
        Assert.StartsWith("Build Eleven to preview its XAML", preview.Status, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.WaitFor(() => preview.Frame is not null, () => $"the Avalonia 11 previewer's first frame (the pane says: {preview.Status})", TimeSpan.FromSeconds(60));
        Ui.Settle();
        Assert.Null(preview.Error);
        Assert.Equal(new PixelSize(400, 300), preview.Frame!.PixelSize);
        Avalonia.Controls.Image frame = window.PreviewPaneOf(document)!.Frame;
        Point inside = frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height * 0.75), window) ?? throw new InvalidOperationException("The preview's frame is not on the window");
        Assert.Equal(Color.Parse("#E2A33F"), Ui.Pixel(window, inside));
        window.Close();
    }

    /// <summary>An Avalonia previewer that ends as it draws (a crash) has its end in passenger.log, with the last words the
    /// pane says it ended with, as the WPF previewer's is.</summary>
    [AvaloniaFact]
    public void An_Avalonia_previewer_that_ends_leaves_its_end_and_last_words_in_the_log()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        int previewer = 0;
        void OnStarted(object? sender, ChildStart start)
        {
            if (start.Command.Contains("Avalonia.Designer.HostApp", StringComparison.Ordinal))
            {
                Volatile.Write(ref previewer, start.ProcessId);
            }
        }
        ChildProcesses.Started += OnStarted;
        Passenger.App.Xaml.XamlPreview preview;
        try
        {
            Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
            Ui.Settle();
            preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
            Ui.WaitFor(() => preview.Frame is not null, "the previewer's first frame", TimeSpan.FromSeconds(60));
        }
        finally
        {
            ChildProcesses.Started -= OnStarted;
        }
        Assert.True(Volatile.Read(ref previewer) != 0, "No Avalonia.Designer.HostApp was started for the preview");

        using (System.Diagnostics.Process host = System.Diagnostics.Process.GetProcessById(Volatile.Read(ref previewer)))
        {
            host.Kill();
        }
        const string Ended = "The previewer ended: ";
        Ui.WaitFor(() => preview.Status.StartsWith(Ended, StringComparison.Ordinal), () => $"the pane saying the previewer ended, not '{preview.Status}'");
        string last = preview.Status[Ended.Length..];
        IdeErrors.Flush();
        string log = Ui.Shared(IdeErrors.LogPath);
        string? line = log.Split('\n').LastOrDefault(l => l.Contains("The Avalonia previewer of MainWindow.axaml ended: ", StringComparison.Ordinal));
        Assert.True(line is not null && line.Contains(last, StringComparison.Ordinal),
            $"passenger.log has {(line is null ? "nothing of the Avalonia previewer's end" : $"its end as '{line}'")}, where the pane says it ended with '{last}'");
        window.Close();
    }

    internal static Marker MarkerOn(CodeEditor editor, OpenDocument document, string stretch)
    {
        string text = editor.Document.Snapshot.ToString();
        return Assert.Single(document.Markers, m => m.Range.End <= text.Length && text[m.Range.Start..m.Range.End] == stretch);
    }

    internal static void Replace(CodeEditor editor, string what, string with)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{what}' is not in the file");
        editor.ApplyEdits([new TextEdit(at, what.Length, with)]);
    }

    /// <summary>The repository the tests were built from.</summary>
    internal static string Repository
    {
        get
        {
            for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                if (File.Exists(Path.Combine(at.FullName, "Passenger.slnx")))
                {
                    return at.FullName;
                }
            }
            throw new DirectoryNotFoundException($"No folder above {AppContext.BaseDirectory} holds Passenger.slnx");
        }
    }

    /// <summary>The caret's line with a bar where the caret is.</summary>
    internal static string CaretLine(CodeEditor editor) =>
        editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Insert(editor.CaretPosition.Column, "|");

    /// <summary>The app: Avalonia's template with a view model its window binds through <c>x:DataType</c>, a palette of
    /// brushes in a theme dictionary merged in by App.axaml, and an asset.</summary>
    public sealed class Gallery : SharedProject
    {
        public Gallery()
            : base("xaml-")
        {
            TestFolders.Prepare(() => Make(Folder), Folder);
            Prepare();
        }

        /// <summary>Makes the app in a folder, not yet restored.</summary>
        internal static void Make(string folder)
        {
            AvaloniaApp.Create(folder, "Gallery");
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(folder, "Styles"));
            Directory.CreateDirectory(Path.Combine(folder, "Assets"));
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), MainViewModel);
            File.WriteAllText(Path.Combine(folder, "Styles/Palette.axaml"), Palette);
            File.WriteAllBytes(Path.Combine(folder, "Assets/logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            File.WriteAllText(Path.Combine(folder, "App.axaml"), AppXaml);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), Window);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml.cs"), CodeBehind);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
        }

        public const string MainViewModel = """
            namespace Gallery.ViewModels;

            public class MainViewModel
            {
                public string Greeting { get; set; } = "Hello";

                public Person Owner { get; } = new();

                public int Count { get; set; }
            }

            public class Person
            {
                public string Name { get; set; } = "Ada";

                public int Age { get; set; }
            }

            """;

        public const string Palette = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Dark">
                        <SolidColorBrush x:Key="Accent" Color="#3D7BF5" />
                        <SolidColorBrush x:Key="Muted" Color="#868A91" />
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="Light">
                        <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                        <SolidColorBrush x:Key="Muted" Color="#6C707E" />
                    </ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
                <Color x:Key="Brand">#E2A33F</Color>
                <Thickness x:Key="Gallery.Gap">8</Thickness>
            </ResourceDictionary>

            """;

        public const string AppXaml = """
            <Application xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.App"
                         RequestedThemeVariant="Default">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceInclude Source="avares://Gallery/Styles/Palette.axaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>

                <Application.Styles>
                    <FluentTheme />
                </Application.Styles>
            </Application>

            """;

        /// <summary>Styles that preview as the button their Design.PreviewWith holds, in the style's colour.</summary>
        public const string Buttons = """
            <Styles xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Design.PreviewWith>
                    <Border Padding="20">
                        <Button Classes="warm" Content="Warm" />
                    </Border>
                </Design.PreviewWith>
                <Style Selector="Button.warm">
                    <Setter Property="Background" Value="#E2A33F" />
                </Style>
            </Styles>

            """;

        /// <summary>The window's code-behind, using the field Avalonia's generator makes for the button named Go.</summary>
        public const string CodeBehind = """
            using Avalonia.Controls;

            namespace Gallery;

            public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                    Go.Content = "Start";
                }
            }

            """;

        public const string Window = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:vm="using:Gallery.ViewModels"
                    mc:Ignorable="d" d:DesignWidth="800" d:DesignHeight="450"
                    x:Class="Gallery.MainWindow"
                    x:DataType="vm:MainViewModel"
                    Title="Gallery">
                <StackPanel Margin="12" Spacing="8">
                    <TextBlock Name="Title" Text="{Binding Greeting}" />
                    <Button Name="Go" Content="Go" Background="{StaticResource Accent}" />
                    <TextBlock Text="{Binding #Go.Content}" />
                </StackPanel>
            </Window>

            """;
    }
}
