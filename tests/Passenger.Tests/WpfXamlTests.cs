using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>WPF XAML through the window, on an app made by <c>dotnet new wpf</c> with a view model its window names with
/// <c>d:DataContext</c>, brushes merged in by App.xaml and a style with triggers: what completion offers, what is marked
/// as typed and after a build (MarkupCompile's errors), where names lead, and that XAML Preview off Windows says WPF
/// previews only there. Off Windows the app says <c>EnableWindowsTargeting</c>, which the SDK gives for building Windows
/// apps elsewhere: the reference assemblies its XAML reads are the same, and MarkupCompile builds it.</summary>
public sealed class WpfXamlTests : IClassFixture<WpfXamlTests.Gallery>
{
    private readonly Gallery app;

    public WpfXamlTests(Gallery app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void Typing_a_tag_offers_wpfs_controls_then_a_controls_properties_and_events_and_then_the_values_of_the_one_chosen()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "WPF's controls offered after a tag's '<'");
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
        Assert.Contains("Content", window.Assist.CompletionLabels);
        Assert.Contains("Canvas.Left", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("G");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Grid.Row"), "Grid's attached properties by their owner");
        // A dependency property only a trigger reads is no attribute to set.
        Assert.DoesNotContain("IsMouseOver", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's dependency properties, its bases' included");
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

        // A trigger's property may be one WPF sets itself; its setter's, one of the element its TargetName names.
        Ui.PlaceAfter(editor, "<Trigger Property=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("IsMouseOver"), "the read-only dependency properties a trigger watches");
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "<Setter TargetName=\"Chrome\" Property=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("CornerRadius"), "the Border's properties, as TargetName names it");
        window.Close();
    }

    [AvaloniaFact]
    public void Resources_complete_from_what_app_xaml_merges_in_and_a_binding_from_its_design_time_data_context_through_a_path()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\"");
        window.KeyTextInput(" Foreground=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Red"), "the named colours a brush takes");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("StaticResource"), "the markup extensions after a value's brace");
        Assert.Contains("DynamicResource", window.Assist.CompletionLabels);
        Assert.Contains("Binding", window.Assist.CompletionLabels);
        Assert.Contains("RelativeSource", window.Assist.CompletionLabels);
        Assert.Contains("x:Static", window.Assist.CompletionLabels);
        Assert.Contains("x:Type", window.Assist.CompletionLabels);
        window.KeyTextInput("StaticRes");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Accent"), "the brushes' keys, merged in by App.xaml");
        Assert.Contains("Heading", window.Assist.CompletionLabels);
        Assert.Contains("Gap", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the view model's properties, d:DataContext's type's");
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
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "<TextBlock Text=\"{Binding Name}\" />");
        window.KeyTextInput("<TextBlock Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Age"), "Person's properties, People's items'");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Background=\"{x:Static SystemColors.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("ControlBrushKey"), "SystemColors' static members");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "xmlns:vm=\"clr-namespace:Gallery.ViewModels\"");
        window.KeyTextInput(" xmlns:items=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "the namespaces offered in a new xmlns's quotes");
        window.KeyTextInput("http");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("http://schemas.microsoft.com/winfx/2006/xaml/presentation"), "the URIs WPF's assemblies map");
        Ui.Press(window, "ESCAPE");
        Replace(editor, "xmlns:items=\"http", "xmlns:items=\"");
        Ui.PlaceAfter(editor, "xmlns:items=\"");
        window.KeyTextInput("clr-namespace:Gal");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("clr-namespace:Gallery.ViewModels"), "the project's own namespace");
        Ui.Press(window, "ESCAPE");
        Replace(editor, "xmlns:items=\"clr-namespace:Gal", "xmlns:items=\"");
        Ui.PlaceAfter(editor, "xmlns:items=\"");
        window.KeyTextInput("clr-namespace:System.Collections.Obj");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Any(l => l.StartsWith("clr-namespace:System.Collections.ObjectModel;assembly=", StringComparison.Ordinal)),
            "another assembly's namespace, with its assembly");
        Ui.Press(window, "ESCAPE");

        window.OpenFile(app.PathOf("ListsWindow.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<DataGridTextColumn Header=\"Name\" Binding=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Age"), "Person's properties, the grid's items'");
        Assert.DoesNotContain("Roots", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<HierarchicalDataTemplate ItemsSource=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Children"), "Node's properties, the tree's items'");
        window.Close();
    }

    [AvaloniaFact]
    public void A_window_that_compiles_is_marked_with_nothing_and_a_misspelt_property_value_resource_and_binding_path_are_marked_as_typed()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Assert.True(window.XamlService.For(document)?.IsLoaded, "MainWindow.xaml was not read as WPF's XAML");
        Assert.True(document.Markers.Count == 0, $"The window, which compiles, is marked: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        foreach (string other in (string[])["App.xaml", "Themes/Brushes.xaml", "ListsWindow.xaml"])
        {
            window.OpenFile(app.PathOf(other));
            Ui.Settle();
            OpenDocument opened = window.EditorArea.Active!;
            Assert.True(window.XamlService.For(opened)?.IsLoaded, $"{other} was not read as WPF's XAML");
            Assert.True(opened.Markers.Count == 0, $"{other}, which compiles, is marked: {string.Join("; ", opened.Markers.Select(m => m.Message))}");
        }

        window.OpenFile(app.PathOf("MainWindow.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "<Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\"");
        window.KeyTextInput(" Foregrund=\"Red\" HorizontalAlignment=\"Centre\"");
        Replace(editor, "{Binding Greeting}", "{Binding Greting}");
        Replace(editor, "{StaticResource Accent}", "{StaticResource Acent}");
        Ui.WaitFor(() => document.Markers.Count == 4, "the four mistakes marked once typing paused");
        Assert.Equal("Cannot resolve property 'Foregrund' on Button", MarkerOn(editor, document, "Foregrund").Message);
        Assert.Equal("'Centre' is not a value of HorizontalAlignment", MarkerOn(editor, document, "Centre").Message);
        Assert.Equal("Resource 'Acent' is not found", MarkerOn(editor, document, "Acent").Message);
        // WPF's bindings are read as the app runs, so a path its design-time data context lacks is a warning.
        Marker path = MarkerOn(editor, document, "Greting");
        Assert.Equal(MarkerKind.Warning, path.Kind);
        Assert.Equal("Cannot resolve property 'Greting' in data context of type MainViewModel", path.Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count == 4, "Problems listing the file's four problems");
        window.Close();
    }

    [AvaloniaFact]
    public void After_a_build_markup_compiles_error_is_marked_on_its_line_and_listed_with_its_code()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        // A button given two children: MarkupCompile's to find, not the live check's.
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\">");
        window.KeyTextInput("\n<Button><TextBlock Text=\"a\" /><TextBlock Text=\"b\" /></Button>");
        // MarkupCompile counts lines as an XML reader does, ending one at this lone '\r' the editor reads inside its line.
        Replace(editor, "Title=\"Gallery\" Height", "Title=\"Gallery\"\r Height");
        Ui.Settle();
        Assert.Empty(document.Markers);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Problem built = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == "MC3089");
        Assert.Equal(PathNames.Canonical(app.PathOf("MainWindow.xaml")), built.Path);
        int line = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("<Button><TextBlock", StringComparison.Ordinal));
        Assert.Equal(line, built.Position.Line);
        Assert.Contains($"Line {line + 1} Position ", built.Message, StringComparison.Ordinal);
        Marker marked = Assert.Single(document.Markers);
        Assert.Equal(MarkerKind.Error, marked.Kind);
        Assert.Equal("MC3089: The object 'Button' already has a child and cannot add 'TextBlock'. 'Button' can accept only one child.", marked.Message);
        Assert.Equal(line, editor.Document.Snapshot.GetLineFromOffset(marked.Range.Start));

        Replace(editor, "<TextBlock Text=\"b\" />", string.Empty);
        Ui.Settle();
        Assert.Empty(document.Markers);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_declaration_from_wpf_xaml_opens_a_controls_class_a_resources_definition_a_bound_property_and_a_handler()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "x:Class=\"Gallery.Main");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.xaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public partial class |MainWindow", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.xaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\">\n        <TextBl");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card describing the tag's type");
        Assert.Equal("class System.Windows.Controls.TextBlock : FrameworkElement", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        OpenDocument library = window.EditorArea.Active!;
        Assert.Equal("System.Windows.Controls.TextBlock.cs", library.Name);
        Assert.True(library.IsReadOnly, "the library's code opened editable");
        Assert.Contains("class |TextBlock", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{StaticResource Acc");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Brushes.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("<SolidColorBrush x:Key=\"|Accent\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Gree");
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
    }

    [AvaloniaFact]
    public void An_elements_x_name_renames_with_its_code_behinds_uses_and_go_to_declaration_from_the_code_lands_on_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml.cs"));
        CodeEditor code = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(code, "        G");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.xaml", window.EditorArea.Active?.Name);
        CodeEditor xaml = window.EditorArea.CodeEditor;
        Assert.Contains("<Button x:Name=\"|Go\"", CaretLine(xaml), StringComparison.Ordinal);

        Ui.Run(window, AppActions.FindUsages);
        Ui.Settle();
        Ui.WaitFor(() => window.Find.Rows.Count > 0, "the usages in the Find window");
        IReadOnlyList<string> found = [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];
        Assert.Contains(found, f => f.Contains("Go.Content = \"Start\";", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("ElementName=Go", StringComparison.Ordinal));

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the button's name");
        window.KeyTextInput("Start");
        Ui.Press(window, "ENTER");
        OpenDocument behind = window.EditorArea.Documents.First(d => d.Name == "MainWindow.xaml.cs");
        Ui.WaitFor(() => behind.Text.Snapshot.ToString().Contains("Start.Content = \"Start\";", StringComparison.Ordinal), "the code-behind's use renamed with it");
        string renamed = window.EditorArea.Documents.First(d => d.Name == "MainWindow.xaml").Text.Snapshot.ToString();
        Assert.Contains("<Button x:Name=\"Start\"", renamed, StringComparison.Ordinal);
        Assert.Contains("ElementName=Start", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"Go\"", renamed, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_user_control_with_no_code_behind_is_a_class_its_x_class_and_its_uses_are_not_marked_and_go_to_declaration_reaches_its_xaml()
    {
        string badge = app.Write("Badge.xaml", Badge);
        string uses = app.Write("Badges.xaml", Badges);
        MainWindow window = app.Open(badge);
        OpenDocument declared = window.EditorArea.Active!;
        Assert.True(window.XamlService.For(declared)?.IsLoaded, "Badge.xaml was not read as WPF's XAML");
        Ui.Settle();
        Assert.True(declared.Markers.Count == 0, $"The XAML-only control is marked: {string.Join("; ", declared.Markers.Select(m => m.Message))}");

        window.OpenFile(uses);
        Ui.Settle();
        OpenDocument user = window.EditorArea.Active!;
        Assert.True(user.Markers.Count == 0, $"Its use, with a UserControl's Margin, is marked: {string.Join("; ", user.Markers.Select(m => m.Message))}");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<local:Bad");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Badge.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("x:Class=\"|Gallery.Badge\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A user control whose XAML is all there is of it: MarkupCompile's generated class is its class.</summary>
    private const string Badge = """
        <UserControl x:Class="Gallery.Badge"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border Background="{StaticResource Accent}" Padding="4">
                <TextBlock Text="New" />
            </Border>
        </UserControl>

        """;

    /// <summary>A window that uses the badge, its UserControl's properties included.</summary>
    private const string Badges = """
        <Window x:Class="Gallery.Badges"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Gallery"
                Title="Badges" Height="200" Width="300">
            <StackPanel>
                <local:Badge Margin="4" HorizontalAlignment="Left" />
            </StackPanel>
        </Window>

        """;

    [AvaloniaFact]
    public void Xaml_preview_on_a_wpf_file_off_windows_says_where_the_preview_would_be_that_wpf_previews_only_on_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "WPF previews on Windows: WpfPreviewTests draw it there");
        MainWindow window = app.Open(app.PathOf("MainWindow.xaml"));
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.FindAction);
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        popup.Query = "xaml preview";
        Ui.Settle();
        Assert.Equal(AppActions.XamlPreview, popup.Rows[0].Id);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed nothing beside the WPF file");
        Ui.WaitFor(() => preview.Status.StartsWith("WPF XAML previews only", StringComparison.Ordinal), "the note that WPF previews only on Windows");
        Assert.Equal("WPF XAML previews only on Windows, where WPF draws", preview.Status);
        Assert.Null(preview.Frame);
        Assert.Null(preview.Error);
        window.Close();
    }

    /// <summary>The app: WPF's template with a view model its window names through <c>d:DataContext</c>, brushes merged in
    /// by App.xaml, and a button style whose template's triggers set its parts.</summary>
    public sealed class Gallery : SharedProject
    {
        public Gallery()
            : base("wpf-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(() => MakeAsync(Folder), Folder);
            await PrepareAsync();
        }

        /// <summary>Makes the app in a folder, not yet restored.</summary>
        internal static async Task MakeAsync(string folder)
        {
            await WpfApp.CreateAsync(folder, "Gallery");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(folder, "Themes"));
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), MainViewModel);
            File.WriteAllText(Path.Combine(folder, "Themes/Brushes.xaml"), Brushes);
            File.WriteAllText(Path.Combine(folder, "App.xaml"), AppXaml);
            File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), Window);
            File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.cs"), CodeBehind);
            File.WriteAllText(Path.Combine(folder, "ViewModels/ListsViewModel.cs"), ListsViewModel);
            File.WriteAllText(Path.Combine(folder, "ListsWindow.xaml"), Lists);
            File.WriteAllText(Path.Combine(folder, "ListsWindow.xaml.cs"), ListsCodeBehind);
        }

        /// <summary>What lists, grids and trees bind: their items, an interface that extends another.</summary>
        public const string ListsViewModel = """
            using System.Collections.ObjectModel;

            namespace Gallery.ViewModels;

            public class Node
            {
                public string Title { get; set; } = "";

                public ObservableCollection<Node> Children { get; } = [];
            }

            public interface INamed
            {
                string Name { get; }
            }

            public interface IShopper : INamed
            {
                int Visits { get; }
            }

            public class ListsViewModel
            {
                public ObservableCollection<Node> Roots { get; } = [];

                public ObservableCollection<Person> People { get; } = [];

                public IShopper? Shopper { get; set; }
            }

            """;

        public const string ListsCodeBehind = """
            using System.Windows;

            namespace Gallery;

            public partial class ListsWindow : Window
            {
                public ListsWindow()
                {
                    InitializeComponent();
                }
            }

            """;

        /// <summary>A window whose bindings are read against items, not its data context: a tree's hierarchical template, a
        /// data grid's columns and cell template, a list view's grid columns; with an x:Array, an x:XData island and a
        /// dictionary merged in from WPF's own Aero2 theme, whose keys nothing here reads. MarkupCompile builds it.</summary>
        public const string Lists = """
            <Window x:Class="Gallery.ListsWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:vm="clr-namespace:Gallery.ViewModels"
                    xmlns:sys="clr-namespace:System;assembly=mscorlib"
                    mc:Ignorable="d"
                    d:DataContext="{d:DesignInstance vm:ListsViewModel}"
                    Title="Lists" Height="450" Width="800">
                <Window.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="pack://application:,,,/PresentationFramework.Aero2;component/themes/aero2.normalcolor.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                        <x:Array x:Key="Sizes" Type="sys:Double">
                            <sys:Double>10</sys:Double>
                            <sys:Double>12</sys:Double>
                        </x:Array>
                        <XmlDataProvider x:Key="Books" XPath="Books">
                            <x:XData>
                                <Books xmlns="">
                                    <Book Title="Dune" />
                                </Books>
                            </x:XData>
                        </XmlDataProvider>
                    </ResourceDictionary>
                </Window.Resources>
                <UniformGrid Columns="3">
                    <TreeView ItemsSource="{Binding Roots}">
                        <TreeView.ItemTemplate>
                            <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                <TextBlock Text="{Binding Title}" />
                            </HierarchicalDataTemplate>
                        </TreeView.ItemTemplate>
                    </TreeView>
                    <DataGrid ItemsSource="{Binding People}" AutoGenerateColumns="False" Background="{StaticResource {x:Static SystemColors.WindowBrushKey}}">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Name" Binding="{Binding Name}" />
                            <DataGridTemplateColumn Header="Age">
                                <DataGridTemplateColumn.CellTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding Age}" />
                                    </DataTemplate>
                                </DataGridTemplateColumn.CellTemplate>
                            </DataGridTemplateColumn>
                        </DataGrid.Columns>
                    </DataGrid>
                    <StackPanel>
                        <ListView ItemsSource="{Binding People}">
                            <ListView.View>
                                <GridView>
                                    <GridViewColumn Header="Age" DisplayMemberBinding="{Binding Age}" />
                                </GridView>
                            </ListView.View>
                        </ListView>
                        <TextBlock Text="{Binding Shopper.Name}" />
                        <ComboBox ItemsSource="{StaticResource Sizes}" />
                        <ListBox ItemsSource="{Binding Source={StaticResource Books}, XPath=Book}" DisplayMemberPath="@Title" />
                    </StackPanel>
                </UniformGrid>
            </Window>

            """;

        public const string MainViewModel = """
            using System.Collections.ObjectModel;

            namespace Gallery.ViewModels;

            public class MainViewModel
            {
                public string Greeting { get; set; } = "Hello";

                public Person Owner { get; } = new();

                public ObservableCollection<Person> People { get; } = [];

                public bool IsBusy { get; set; }
            }

            public class Person
            {
                public string Name { get; set; } = "Ada";

                public int Age { get; set; }
            }

            """;

        public const string Brushes = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:sys="clr-namespace:System;assembly=mscorlib">
                <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                <SolidColorBrush x:Key="Muted" Color="#868A91" />
                <sys:Double x:Key="Gap">8</sys:Double>
            </ResourceDictionary>

            """;

        public const string AppXaml = """
            <Application x:Class="Gallery.App"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         StartupUri="MainWindow.xaml">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="Themes/Brushes.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                        <Style x:Key="Heading" TargetType="TextBlock">
                            <Setter Property="FontSize" Value="20" />
                            <Setter Property="Foreground" Value="{StaticResource Accent}" />
                        </Style>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>

            """;

        /// <summary>The window's code-behind, using the field MarkupCompile makes for the button named Go.</summary>
        public const string CodeBehind = """
            using System.Windows;

            namespace Gallery;

            public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                    Go.Content = "Start";
                }

                private void OnGo(object sender, RoutedEventArgs e)
                {
                }
            }

            """;

        public const string Window = """
            <Window x:Class="Gallery.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:vm="clr-namespace:Gallery.ViewModels"
                    mc:Ignorable="d"
                    d:DataContext="{d:DesignInstance Type=vm:MainViewModel}"
                    Title="Gallery" Height="450" Width="800">
                <Window.Resources>
                    <Style TargetType="Button">
                        <Setter Property="Template">
                            <Setter.Value>
                                <ControlTemplate TargetType="Button">
                                    <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="4">
                                        <ContentPresenter HorizontalAlignment="Center" />
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource Muted}" />
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Style>
                </Window.Resources>
                <StackPanel Margin="12">
                    <TextBlock Style="{StaticResource Heading}" Text="{Binding Greeting}" />
                    <Button x:Name="Go" Content="Go" Click="OnGo" Background="{StaticResource Accent}" />
                    <TextBlock Text="{Binding Content, ElementName=Go}" />
                    <ListBox ItemsSource="{Binding People}" Background="{x:Static SystemColors.ControlBrush}">
                        <ListBox.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Name}" />
                            </DataTemplate>
                        </ListBox.ItemTemplate>
                    </ListBox>
                </StackPanel>
            </Window>

            """;
    }
}
