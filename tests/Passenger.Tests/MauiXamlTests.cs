using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>.NET MAUI's XAML through the window, on the files of the .NET 11 MAUI template's app (which declare no
/// namespace, MAUI's implicit ones standing in) with a page of its own binding a view model through <c>x:DataType</c>,
/// a data template, OnPlatform, OnIdiom and AppThemeBinding: what completion offers, what is marked as typed and after a
/// build (MAUI's source generator's warnings), and where names lead. The app is a plain net11.0 project on MAUI's
/// controls, which restores and builds without MAUI's workload, from the package cache tests/MauiGraphs fills.</summary>
public sealed class MauiXamlTests : IClassFixture<MauiXamlTests.Gallery>
{
    private readonly Gallery app;

    public MauiXamlTests(Gallery app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void The_templates_files_are_marked_with_nothing_and_a_misspelt_property_value_resource_handler_and_binding_path_are_marked_as_typed()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Assert.True(window.XamlService.For(document)?.IsLoaded, "MainPage.xaml was not read as MAUI's XAML");
        Assert.True(document.Markers.Count == 0, $"The page, which compiles, is marked: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        foreach (string other in (string[])["App.xaml", "AppShell.xaml", "PeoplePage.xaml", "Resources/Styles/Colors.xaml", "Resources/Styles/Styles.xaml"])
        {
            window.OpenFile(app.PathOf(other));
            Ui.Settle();
            OpenDocument opened = window.EditorArea.Active!;
            Assert.True(window.XamlService.For(opened)?.IsLoaded, $"{other} was not read as MAUI's XAML");
            Assert.True(opened.Markers.Count == 0, $"{other}, which compiles, is marked: {string.Join("; ", opened.Markers.Select(m => m.Message))}");
        }

        window.OpenFile(app.PathOf("MainPage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "Text=\"Click me\"");
        window.KeyTextInput(" Foregrund=\"Red\" FontAttributes=\"Bolt\"");
        Replace(editor, "{StaticResource Headline}", "{StaticResource Headlin}");
        Replace(editor, "Clicked=\"OnCounterClicked\"", "Clicked=\"OnCounterClick\"");
        Ui.WaitFor(() => document.Markers.Count == 4, "the four mistakes marked once typing paused");
        Assert.Equal("Cannot resolve property 'Foregrund' on Button", MarkerOn(editor, document, "Foregrund").Message);
        Assert.Equal("'Bolt' is not a value of FontAttributes", MarkerOn(editor, document, "Bolt").Message);
        Assert.Equal("Resource 'Headlin' is not found", MarkerOn(editor, document, "Headlin").Message);
        Assert.Equal("Cannot resolve method 'OnCounterClick' in MainPage", MarkerOn(editor, document, "OnCounterClick").Message);

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        OpenDocument page = window.EditorArea.Active!;
        // .NET 11's C# expressions are its source generator's to judge.
        Replace(editor, "<Label Text=\"{Binding Age}\" />", "<Label Text=\"{Binding Age}\" />\n<Label Text=\"{$'{Name} ({Age})'}\" IsVisible=\"{Age GT 17}\" />");
        Replace(editor, "Title=\"{Binding Greeting}\"", "Title=\"{Greeting}\"");
        // A generic element names its type arguments.
        Replace(editor, "<CollectionView.ItemTemplate>", "<CollectionView.Margin>\n<OnPlatform x:TypeArguments=\"Thickness\" Default=\"0\">\n<On Platform=\"iOS\" Value=\"0,8\" />\n</OnPlatform>\n</CollectionView.Margin>\n<CollectionView.ItemTemplate>");
        Replace(editor, "{Binding Owner.Name}", "{Binding Owner.Nam}");
        Replace(editor, "{Binding Age}", "{Binding Agee}");
        Replace(editor, "Light={StaticResource Primary}", "Light={StaticResource Primry}");
        Ui.WaitFor(() => page.Markers.Count == 3, "the three mistakes marked once typing paused");
        // MAUI's source generator binds a path its x:DataType lacks as the app runs, warning of it.
        Marker path = MarkerOn(editor, page, "Nam");
        Assert.Equal(MarkerKind.Warning, path.Kind);
        Assert.Equal("Cannot resolve property 'Nam' on Person", path.Message);
        Assert.Equal("Cannot resolve property 'Agee' on Person", MarkerOn(editor, page, "Agee").Message);
        Assert.Equal("Resource 'Primry' is not found", MarkerOn(editor, page, "Primry").Message);
        Replace(editor, "x:DataType=\"vm:Person\"", "x:DataType=\"vm:Persn\"");
        Ui.WaitFor(() => page.Markers.Any(m => m.Message == "Cannot resolve symbol 'vm:Persn'"), "a data template's x:DataType nothing resolves");
        // A data template whose type is not known has its bindings left to the compiler.
        Assert.DoesNotContain(page.Markers, m => m.Message.Contains("Agee", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void Completion_offers_mauis_controls_their_properties_events_and_values_its_markup_extensions_resources_and_a_compiled_bindings_path()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Spacing=\"25\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "MAUI's controls offered after a tag's '<', in a file that declares no namespace");
        Assert.Contains("CollectionView", window.Assist.CompletionLabels);
        // A type converter is no element.
        Assert.DoesNotContain("ColorTypeConverter", window.Assist.CompletionLabels);
        window.KeyTextInput("Butt");
        Ui.Settle();
        Assert.Equal("Button", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button|", CaretLine(editor), StringComparison.Ordinal);

        window.KeyTextInput(" ");
        window.KeyTextInput("C");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Clicked"), "a Button's events among its attributes");
        Assert.Contains("Command", window.Assist.CompletionLabels);
        Assert.Contains("CornerRadius", window.Assist.CompletionLabels);
        // What MAUI keeps out of sight (EditorBrowsable Never) is not offered.
        Assert.DoesNotContain("IsPlatformEnabled", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("G");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Grid.Row"), "Grid's attached properties by their owner");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("F");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("FontAttributes"), "a Button's bindable properties, its bases' included");
        window.KeyTextInput("ontA");
        Ui.Settle();
        Assert.Equal("FontAttributes", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button FontAttributes=\"|\"", CaretLine(editor), StringComparison.Ordinal);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Bold"), "the enum's members offered inside the new value's quotes");
        Assert.Equal(["Bold", "Italic", "None"], window.Assist.CompletionLabels.Order());
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "Style=\"");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("StaticResource"), "the markup extensions after a value's brace");
        foreach (string extension in (string[])["DynamicResource", "Binding", "OnPlatform", "OnIdiom", "AppThemeBinding", "x:Static", "x:Type", "x:Reference"])
        {
            Assert.Contains(extension, window.Assist.CompletionLabels);
        }
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{StaticResource ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Headline"), "the styles' keys, merged in by App.xaml");
        Assert.Contains("Primary", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Title=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the view model's properties, x:DataType's type's");
        Assert.Contains("Owner", window.Assist.CompletionLabels);
        Assert.Contains("Mode=", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{Binding Owner.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Age"), "the path's next step's properties, Person's after Owner.");
        Assert.DoesNotContain("Greeting", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "Spacing=\"8\">\n                        <Label Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Age"), "Person's properties, the data template's x:DataType's");
        Assert.DoesNotContain("People", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{OnPlatform ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Android="), "OnPlatform's platforms");
        Assert.Contains("iOS=", window.Assist.CompletionLabels);
        Assert.Contains("Default=", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{OnIdiom ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Tablet="), "OnIdiom's idioms");
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "{AppThemeBinding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Dark="), "AppThemeBinding's themes");
        Assert.Contains("Light=", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        window.OpenFile(app.PathOf("Resources/Styles/Styles.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Style TargetType=\"Button\">\n        <Setter Property=\"");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("TextColor"), "the bindable properties of the style's target type");
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_declaration_from_maui_xaml_opens_the_code_behind_a_resources_definition_a_bound_property_a_handler_and_a_data_type()
    {
        MainWindow window = app.Open(app.PathOf("MainPage.xaml"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "x:Class=\"MauiApp1.Main");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainPage.xaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public partial class |MainPage", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainPage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{StaticResource Headl");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Styles.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("x:Key=\"|Headline\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainPage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Clicked=\"OnCou");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainPage.xaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("private void |OnCounterClicked(", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Greeting", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "x:DataType=\"vm:Pers");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public class |Person", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Find Usages of a view model's property from its C# lists the MAUI page that binds it, and from the page the
    /// C#, as C#-in-XAML's one finder reads MAUI's XAML too.</summary>
    [AvaloniaFact]
    public void Find_usages_of_a_bound_property_lists_the_maui_page_binding_it_and_from_the_page_its_csharp()
    {
        MainWindow window = app.Open(app.PathOf("ViewModels/MainViewModel.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "string Gree");
        Assert.Contains("PeoplePage.xaml:4 Title=\"{Binding Greeting}\">", FoundUsages(window));

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Owner.Na");
        Assert.Contains("MainViewModel.cs:7 public string Name { get; set; } = \"\";", FoundUsages(window));
        window.Close();
    }

    /// <summary>A data template given no x:DataType has its bindings compiled against the page's by MAUI's source
    /// generator, which Passenger's own check leaves to it: its warning of each is on the binding's line as the page opens,
    /// the generator run over it before any build, and after a build listed with its code, marked once.</summary>
    [AvaloniaFact]
    public void Mauis_source_generators_warning_is_marked_on_its_line_before_a_build_and_after_it_listed_with_its_code()
    {
        app.Rewrite("PeoplePage.xaml", Gallery.People.Replace("<DataTemplate x:DataType=\"vm:Person\">", "<DataTemplate>", StringComparison.Ordinal));
        MainWindow window = app.Open(app.PathOf("PeoplePage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Any(m => m.Code == "MAUIG2045"), () => $"the generator's warning marked before any build: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Assert.Contains(document.Markers, m => m.Message.StartsWith("MAUIG2045: Binding: Property \"Name\" not found on", StringComparison.Ordinal));
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        int line = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("{Binding Name}", StringComparison.Ordinal));
        Problem built = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == "MAUIG2045" && p.Position.Line == line);
        Assert.Equal(PathNames.Canonical(app.PathOf("PeoplePage.xaml")), built.Path);
        Assert.Contains("Binding: Property \"Name\" not found on \"global::MauiApp1.ViewModels.MainViewModel\"", built.Message, StringComparison.Ordinal);
        Marker marked = Assert.Single(document.Markers, m => editor.Document.Snapshot.GetLineFromOffset(m.Range.Start) == line);
        Assert.Equal(MarkerKind.Warning, marked.Kind);
        Assert.StartsWith("MAUIG2045: Binding: Property \"Name\" not found on", marked.Message, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>.NET 11's C# expressions are C# to completion and navigation, as the language server reads the code MAUI's
    /// source generator writes of them: where an expression's name starts, x:DataType's members beside the markup
    /// extensions the name may still become; after a step's dot, its type's members; and Go to Declaration and Find Usages
    /// from a name in one.</summary>
    [AvaloniaFact]
    public void In_a_csharp_expression_completion_offers_the_data_types_members_and_its_names_lead_to_their_declarations_and_usages()
    {
        MainWindow window = app.Open(app.PathOf("PeoplePage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Replace(editor, "Title=\"{Binding Greeting}\"", "Title=\"\"");
        Ui.PlaceAfter(editor, "Title=\"");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), () => $"x:DataType's members after a value's brace: {string.Join(", ", window.Assist.CompletionLabels.Take(30))}");
        Assert.Contains("Owner", window.Assist.CompletionLabels);
        Assert.Contains("StaticResource", window.Assist.CompletionLabels);
        Assert.DoesNotContain(window.Assist.CompletionLabels, l => l.StartsWith("__", StringComparison.Ordinal));
        Ui.Press(window, "ESCAPE");

        Replace(editor, "Title=\"{\"", "Title=\"{Owner.}\"");
        Ui.PlaceAfter(editor, "{Owner.");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Age"), () => $"Person's members after Owner's dot: {string.Join(", ", window.Assist.CompletionLabels.Take(30))}");
        Assert.Contains("Name", window.Assist.CompletionLabels);
        Assert.DoesNotContain("Greeting", window.Assist.CompletionLabels);
        window.KeyTextInput("Na");
        Ui.Settle();
        Assert.Equal("Name", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.Contains("Title=\"{Owner.Name|}\"", CaretLine(editor), StringComparison.Ordinal);

        Replace(editor, "{Owner.Name}", "{Greeting}");
        Ui.PlaceAfter(editor, "{Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Greeting", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("PeoplePage.xaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Gree");
        Assert.Contains("MainViewModel.cs:14 public string Greeting { get; set; } = \"Hello\";", FoundUsages(window));
        window.Close();
    }

    /// <summary>MAUI's source generator runs over the page as it is typed, before any build: its error for an expression
    /// naming what neither the page nor its x:DataType has is marked on the expression, and C#'s for a member a step's
    /// type lacks on that member's name, the expression compiled as the generator writes it; each goes once fixed.</summary>
    [AvaloniaFact]
    public void Before_any_build_mauis_source_generators_error_and_csharps_in_an_expression_are_marked_as_typed()
    {
        MainWindow window = app.Open(app.PathOf("PeoplePage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument page = window.EditorArea.Active!;
        Ui.Settle();
        Assert.True(page.Markers.Count == 0, $"The page, which compiles, is marked: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        Replace(editor, "Title=\"{Binding Greeting}\"", "Title=\"{Greting}\"");
        Replace(editor, "<Label Text=\"{Binding Owner.Name}\"", "<Label Text=\"{Owner.Nam}\"");
        Ui.WaitFor(() => page.Markers.Count == 2, () => $"the two mistakes marked once typing paused: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        Marker generator = MarkerOn(editor, page, "{Greting}");
        Assert.Equal(MarkerKind.Error, generator.Kind);
        Assert.Equal("MAUIX2009: 'Greting' not found on 'PeoplePage' or 'MainViewModel'.", generator.Message);
        Marker member = MarkerOn(editor, page, "Nam");
        Assert.Equal(MarkerKind.Error, member.Kind);
        Assert.StartsWith("CS1061: 'Person' does not contain a definition for 'Nam'", member.Message, StringComparison.Ordinal);
        Replace(editor, "{Greting}", "{Greeting}");
        Replace(editor, "{Owner.Nam}", "{$'{Owner.Name} ({Owner.Age})'}");
        Ui.WaitFor(() => page.Markers.Count == 0, () => $"the fixed expressions' marks gone: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        window.Close();
    }

    /// <summary>In a page of a project built for a platform, the platform's views are XAML's to write: offered after the
    /// prefix of a namespace for that platform, read as MAUI's own types are (a misspelt view or property marked), from the
    /// reference pack the SDK names for the workload's framework (Android's Microsoft.Android.Ref.37); a namespace for the
    /// other platform is passed over, as MAUI passes it over there. Skips, saying why, where the project does not restore
    /// from the package cache alone (no workload, or MAUI's packages for it never restored here: tests/MauiGraphs).</summary>
    [AvaloniaTheory]
    [InlineData("android", "TextView", "EditText", "ios", "UILabel")]
    [InlineData("ios", "UILabel", "UIButton", "android", "TextView")]
    public void In_a_project_built_for_a_platform_its_views_complete_and_check_and_a_namespace_for_another_is_passed_over(string platform, string view,
        string offered, string other, string otherView)
    {
        string folder = PathNames.Canonical(TestFolders.Create($"maui-{platform}"));
        string version = AvaloniaApp.Metadata("MauiVersion");
        File.WriteAllText(Path.Combine(folder, "MauiPlatform.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{MobilePlatforms.FirstOnCoreClr}.0-{platform}</TargetFramework>
                {(platform == "android" ? "<SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>" : string.Empty)}
                <OutputType>Library</OutputType>
                <RootNamespace>MauiPlatform</RootNamespace>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="{version}" />
              </ItemGroup>
            </Project>

            """);
        File.WriteAllText(Path.Combine(folder, "PlatformPage.xaml"), """
            <ContentPage x:Class="MauiPlatform.PlatformPage"
                         xmlns:android="clr-namespace:Android.Widget;assembly=Mono.Android;targetPlatform=Android"
                         xmlns:ios="clr-namespace:UIKit;assembly=Microsoft.iOS;targetPlatform=iOS">
                <VerticalStackLayout>
                    <Label Text="Shared" />
                    <android:TextView Text="Hello from Android" />
                    <ios:UILabel Text="Hello from iOS" />
                </VerticalStackLayout>
            </ContentPage>

            """);
        File.WriteAllText(Path.Combine(folder, "PlatformPage.xaml.cs"), """
            namespace MauiPlatform;

            public partial class PlatformPage : ContentPage
            {
                public PlatformPage()
                {
                    InitializeComponent();
                }
            }

            """);
        ChildResult restored = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", "MauiPlatform.csproj"], folder), limit: Restoring)),
            $"the {platform} project's restore", Restoring);
        if (!restored.Succeeded)
        {
            Assert.Skip($"A MAUI project built for {platform} does not restore here from the package cache alone (its workload, and MAUI's packages for it "
                + $"restored by tests/MauiGraphs): {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
        }
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, "PlatformPage.xaml"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument page = window.EditorArea.Active!;
        Ui.WaitFor(() => window.XamlService.For(page)?.IsLoaded == true, "the page read as MAUI's XAML");
        Ui.WaitFor(() => window.XamlService.For(page)?.DiagnosedVersion == page.Text.Version, "the page checked");
        Assert.True(page.Markers.Count == 0, $"The page, whose {view} is {platform}'s and whose {otherView} its build passes over, is marked: {string.Join("; ", page.Markers.Select(m => m.Message))}");

        Replace(editor, "<Label Text=\"Shared\" />", $"<Label Text=\"Shared\" />\n<{platform}:");
        Ui.PlaceAfter(editor, $"\n<{platform}:");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains(view), () => $"{platform}'s views after its prefix: {string.Join(", ", window.Assist.CompletionLabels.Take(20))}");
        Assert.Contains(offered, window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Replace(editor, $"\n<{platform}:", string.Empty);

        string misspelt = view[..^1];
        Replace(editor, $"<{platform}:{view} Text=", $"<{platform}:{misspelt} Txt=");
        Replace(editor, $"<{other}:{otherView} Text=", $"<{other}:{otherView[..^1]} Txt=");
        Ui.WaitFor(() => page.Markers.Count == 1, () => $"the misspelt view marked once typing paused, the other platform's passed over: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        Assert.Equal($"Cannot resolve symbol '{platform}:{misspelt}'", MarkerOn(editor, page, $"{platform}:{misspelt}").Message);
        Replace(editor, $"<{platform}:{misspelt}", $"<{platform}:{view}");
        Ui.WaitFor(() => page.Markers.Count == 1 && page.Markers[0].Message.Contains("Txt", StringComparison.Ordinal),
            () => $"the misspelt property of {platform}'s view marked: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        Assert.Equal($"Cannot resolve property 'Txt' on {view}", MarkerOn(editor, page, "Txt").Message);
        window.Close();
    }

    /// <summary>How long a project's restore from the package cache may take.</summary>
    private static readonly TimeSpan Restoring = TimeSpan.FromMinutes(4);

    /// <summary>An expression is read as MAUI's source generator writes it: a type named by a XAML prefix is that type (Go to
    /// Declaration reaches it), an attached property read from an element is its getter's call, and a one-character string
    /// given to a char parameter is a char, where a longer one is marked as C# marks it (CS1503); and Find Usages from the
    /// C# lists the expressions that name a member.</summary>
    [AvaloniaFact]
    public void Prefixed_types_attached_properties_and_char_arguments_read_as_mauis_generator_writes_them_and_find_usages_from_csharp_lists_expressions()
    {
        MainWindow window = app.Open(app.PathOf("PeoplePage.xaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument page = window.EditorArea.Active!;
        Replace(editor, "Title=\"{Binding Greeting}\"", "Title=\"{vm:Helper.Shout(Greeting)}\"");
        Replace(editor, "<Label Text=\"{Binding Owner.Name}\"", "<Label Text=\"{Repeat('ab', 2)}\" IsVisible=\"{this.(Grid.Row) GT 0}\"");
        Ui.WaitFor(() => page.Markers.Count == 1, () => $"the string given to a char marked once typing paused: {string.Join("; ", page.Markers.Select(m => m.Message))}");
        Marker conversion = MarkerOn(editor, page, "'ab'");
        Assert.Equal("CS1503", conversion.Code);
        Replace(editor, "{Repeat('ab', 2)}", "{Repeat('a', 2)}");
        Ui.WaitFor(() => page.Markers.Count == 0, () => $"a character given to a char unmarked: {string.Join("; ", page.Markers.Select(m => m.Message))}");

        Ui.PlaceAfter(editor, "{vm:Helper.Sho");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public static string |Shout(", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "public string Repe");
        IReadOnlyList<string> found = FoundUsages(window);
        Assert.Contains(found, u => u.StartsWith("PeoplePage.xaml:9 <Label Text=\"{Repeat('a', 2)}\"", StringComparison.Ordinal));
        window.Close();
    }

    private static IReadOnlyList<string> FoundUsages(MainWindow window)
    {
        int searches = window.Find.Searches.Count;
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => window.Find.Searches.Count > searches, "the usages in the Find window");
        return [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => $"{Path.GetFileName(r.Usage!.Path)}:{r.Usage.Line + 1} {r.Label}")];
    }

    /// <summary>The app: the .NET 11 MAUI template's App, AppShell and MainPage with its colours and styles (the styles its
    /// page names and one of each kind the template writes), as a plain net11.0 project on MAUI's controls, with a page of
    /// its own binding a view model.</summary>
    public sealed class Gallery : SharedProject
    {
        public Gallery()
            : base("maui-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(() => Make(Folder), Folder);
            await PrepareAsync();
        }

        private static Task Make(string folder)
        {
            string version = AvaloniaApp.Metadata("MauiVersion");
            string cached = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "microsoft.maui.controls", version.ToLowerInvariant());
            Assert.True(Directory.Exists(cached), $"Microsoft.Maui.Controls {version} is not in the package cache at {cached}: restore the tests first (tests/MauiGraphs)");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(folder, "Resources", "Styles"));
            File.WriteAllText(Path.Combine(folder, "MauiApp1.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <RootNamespace>MauiApp1</RootNamespace>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Maui.Controls" Version="{version}" />
                  </ItemGroup>
                </Project>

                """);
            File.WriteAllText(Path.Combine(folder, "App.xaml"), AppXaml);
            File.WriteAllText(Path.Combine(folder, "App.xaml.cs"), AppCode);
            File.WriteAllText(Path.Combine(folder, "AppShell.xaml"), Shell);
            File.WriteAllText(Path.Combine(folder, "AppShell.xaml.cs"), ShellCode);
            File.WriteAllText(Path.Combine(folder, "MainPage.xaml"), MainPage);
            File.WriteAllText(Path.Combine(folder, "MainPage.xaml.cs"), MainPageCode);
            File.WriteAllText(Path.Combine(folder, "Resources/Styles/Colors.xaml"), Colors);
            File.WriteAllText(Path.Combine(folder, "Resources/Styles/Styles.xaml"), Styles);
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), MainViewModel);
            File.WriteAllText(Path.Combine(folder, "PeoplePage.xaml"), People);
            File.WriteAllText(Path.Combine(folder, "PeoplePage.xaml.cs"), PeopleCode);
            return Task.CompletedTask;
        }

        public const string AppXaml = """
            <Application xmlns:local="clr-namespace:MauiApp1"
                         x:Class="MauiApp1.App">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary Source="Resources/Styles/Colors.xaml" />
                            <ResourceDictionary Source="Resources/Styles/Styles.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>

            """;

        public const string AppCode = """
            using Microsoft.Extensions.DependencyInjection;

            namespace MauiApp1;

            public partial class App : Application
            {
                public App()
                {
                    InitializeComponent();
                }

                protected override Window CreateWindow(IActivationState? activationState)
                {
                    return new Window(new AppShell());
                }
            }

            """;

        public const string Shell = """
            <Shell
                x:Class="MauiApp1.AppShell"
                xmlns:local="clr-namespace:MauiApp1"
                Title="MauiApp1">

                <ShellContent
                    Title="Home"
                    ContentTemplate="{DataTemplate local:MainPage}"
                    Route="MainPage" />

            </Shell>

            """;

        public const string ShellCode = """
            namespace MauiApp1;

            public partial class AppShell : Shell
            {
                public AppShell()
                {
                    InitializeComponent();
                }
            }

            """;

        public const string MainPage = """
            <ContentPage x:Class="MauiApp1.MainPage">

                <ScrollView>
                    <VerticalStackLayout
                        Padding="30,0"
                        Spacing="25">
                        <Image
                            Source="dotnet_bot.png"
                            HeightRequest="185"
                            Margin="0,20,0,0"
                            Aspect="AspectFit"
                            SemanticProperties.Description="dot net bot riding a rocket" />

                        <Label
                            Text="Hello, World!"
                            Style="{StaticResource Headline}"
                            SemanticProperties.HeadingLevel="Level1" />

                        <Label
                            Text="Welcome to &#10;.NET Multi-platform App UI"
                            Style="{StaticResource SubHeadline}"
                            SemanticProperties.HeadingLevel="Level2"
                            SemanticProperties.Description="Welcome to dot net Multi platform App U I" />

                        <Button
                            x:Name="CounterBtn"
                            Text="Click me"
                            SemanticProperties.Hint="Counts the number of times you click"
                            Clicked="OnCounterClicked"
                            HorizontalOptions="Fill" />
                    </VerticalStackLayout>
                </ScrollView>

            </ContentPage>

            """;

        public const string MainPageCode = """
            namespace MauiApp1;

            public partial class MainPage : ContentPage
            {
                int count = 0;

                public MainPage()
                {
                    InitializeComponent();
                }

                private void OnCounterClicked(object? sender, EventArgs e)
                {
                    count++;

                    if (count == 1)
                        CounterBtn.Text = $"Clicked {count} time";
                    else
                        CounterBtn.Text = $"Clicked {count} times";

                    SemanticScreenReader.Announce(CounterBtn.Text);
                }
            }

            """;

        public const string Colors = """
            <ResourceDictionary>

                <!-- Note: For Android please see also Platforms\Android\Resources\values\colors.xml -->

                <Color x:Key="Primary">#512BD4</Color>
                <Color x:Key="PrimaryDark">#ac99ea</Color>
                <Color x:Key="PrimaryDarkText">#242424</Color>
                <Color x:Key="Secondary">#DFD8F7</Color>
                <Color x:Key="SecondaryDarkText">#9880e5</Color>
                <Color x:Key="Tertiary">#2B0B98</Color>

                <Color x:Key="White">White</Color>
                <Color x:Key="Black">Black</Color>
                <Color x:Key="Magenta">#D600AA</Color>
                <Color x:Key="MidnightBlue">#190649</Color>
                <Color x:Key="OffBlack">#1f1f1f</Color>

                <Color x:Key="Gray100">#E1E1E1</Color>
                <Color x:Key="Gray200">#C8C8C8</Color>
                <Color x:Key="Gray300">#ACACAC</Color>
                <Color x:Key="Gray400">#919191</Color>
                <Color x:Key="Gray500">#6E6E6E</Color>
                <Color x:Key="Gray600">#404040</Color>
                <Color x:Key="Gray900">#212121</Color>
                <Color x:Key="Gray950">#141414</Color>

                <SolidColorBrush x:Key="PrimaryBrush" Color="{StaticResource Primary}"/>
                <SolidColorBrush x:Key="SecondaryBrush" Color="{StaticResource Secondary}"/>
                <SolidColorBrush x:Key="TertiaryBrush" Color="{StaticResource Tertiary}"/>
                <SolidColorBrush x:Key="WhiteBrush" Color="{StaticResource White}"/>
                <SolidColorBrush x:Key="BlackBrush" Color="{StaticResource Black}"/>

                <SolidColorBrush x:Key="Gray100Brush" Color="{StaticResource Gray100}"/>
                <SolidColorBrush x:Key="Gray200Brush" Color="{StaticResource Gray200}"/>
                <SolidColorBrush x:Key="Gray300Brush" Color="{StaticResource Gray300}"/>
                <SolidColorBrush x:Key="Gray400Brush" Color="{StaticResource Gray400}"/>
                <SolidColorBrush x:Key="Gray500Brush" Color="{StaticResource Gray500}"/>
                <SolidColorBrush x:Key="Gray600Brush" Color="{StaticResource Gray600}"/>
                <SolidColorBrush x:Key="Gray900Brush" Color="{StaticResource Gray900}"/>
                <SolidColorBrush x:Key="Gray950Brush" Color="{StaticResource Gray950}"/>
            </ResourceDictionary>

            """;

        /// <summary>The template's styles the page names, and one of each kind it writes: visual states named alike in
        /// each style, a page's and a shell's for their derived types, the shell's attached properties and a navigation
        /// page's attached icon colour named as the target's own.</summary>
        public const string Styles = """
            <ResourceDictionary
                >

                <Style TargetType="ActivityIndicator">
                    <Setter Property="Color" Value="{AppThemeBinding Light={StaticResource Primary}, Dark={StaticResource White}}" />
                </Style>

                <Style TargetType="Button">
                    <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource PrimaryDarkText}}" />
                    <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource Primary}, Dark={StaticResource PrimaryDark}}" />
                    <Setter Property="FontFamily" Value="OpenSansRegular"/>
                    <Setter Property="FontSize" Value="14"/>
                    <Setter Property="BorderWidth" Value="0"/>
                    <Setter Property="CornerRadius" Value="8"/>
                    <Setter Property="Padding" Value="14,10"/>
                    <Setter Property="MinimumHeightRequest" Value="44"/>
                    <Setter Property="MinimumWidthRequest" Value="44"/>
                    <Setter Property="VisualStateManager.VisualStateGroups">
                        <VisualStateGroupList>
                            <VisualStateGroup x:Name="CommonStates">
                                <VisualState x:Name="Normal" />
                                <VisualState x:Name="Disabled">
                                    <VisualState.Setters>
                                        <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource Gray950}, Dark={StaticResource Gray200}}" />
                                        <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource Gray200}, Dark={StaticResource Gray600}}" />
                                    </VisualState.Setters>
                                </VisualState>
                                <VisualState x:Name="PointerOver" />
                            </VisualStateGroup>
                        </VisualStateGroupList>
                    </Setter>
                </Style>

                <Style TargetType="Label">
                    <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource Black}, Dark={StaticResource White}}" />
                    <Setter Property="BackgroundColor" Value="Transparent" />
                    <Setter Property="FontFamily" Value="OpenSansRegular" />
                    <Setter Property="FontSize" Value="14" />
                    <Setter Property="VisualStateManager.VisualStateGroups">
                        <VisualStateGroupList>
                            <VisualStateGroup x:Name="CommonStates">
                                <VisualState x:Name="Normal" />
                                <VisualState x:Name="Disabled">
                                    <VisualState.Setters>
                                        <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource Gray300}, Dark={StaticResource Gray600}}" />
                                    </VisualState.Setters>
                                </VisualState>
                            </VisualStateGroup>
                        </VisualStateGroupList>
                    </Setter>
                </Style>

                <Style TargetType="Label" x:Key="Headline">
                    <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource MidnightBlue}, Dark={StaticResource White}}" />
                    <Setter Property="FontSize" Value="32" />
                    <Setter Property="HorizontalOptions" Value="Center" />
                    <Setter Property="HorizontalTextAlignment" Value="Center" />
                </Style>

                <Style TargetType="Label" x:Key="SubHeadline">
                    <Setter Property="TextColor" Value="{AppThemeBinding Light={StaticResource MidnightBlue}, Dark={StaticResource White}}" />
                    <Setter Property="FontSize" Value="24" />
                    <Setter Property="HorizontalOptions" Value="Center" />
                    <Setter Property="HorizontalTextAlignment" Value="Center" />
                </Style>

                <Style TargetType="Page" ApplyToDerivedTypes="True">
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="BackgroundColor" Value="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource OffBlack}}" />
                </Style>

                <Style TargetType="Shell" ApplyToDerivedTypes="True">
                    <Setter Property="Shell.BackgroundColor" Value="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource OffBlack}}" />
                    <Setter Property="Shell.ForegroundColor" Value="{AppThemeBinding Light={StaticResource Black}, Dark={StaticResource SecondaryDarkText}}" />
                    <Setter Property="Shell.NavBarHasShadow" Value="False" />
                </Style>

                <Style TargetType="NavigationPage">
                    <Setter Property="BarBackgroundColor" Value="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource OffBlack}}" />
                    <Setter Property="BarTextColor" Value="{AppThemeBinding Light={StaticResource Gray200}, Dark={StaticResource White}}" />
                    <Setter Property="IconColor" Value="{AppThemeBinding Light={StaticResource Gray200}, Dark={StaticResource White}}" />
                </Style>

            </ResourceDictionary>

            """;

        public const string MainViewModel = """
            using System.Collections.ObjectModel;

            namespace MauiApp1.ViewModels;

            public class Person
            {
                public string Name { get; set; } = "";

                public int Age { get; set; }
            }

            public class MainViewModel
            {
                public string Greeting { get; set; } = "Hello";

                public Person Owner { get; } = new();

                public ObservableCollection<Person> People { get; } = [];

                public string Repeat(char c, int count) => new(c, count);
            }

            public static class Helper
            {
                public static string Shout(string text) => text.ToUpperInvariant();
            }

            """;

        /// <summary>A page of the app's own, compiled bindings through its x:DataType and a data template's, with values
        /// by platform, idiom and theme.</summary>
        public const string People = """
            <ContentPage x:Class="MauiApp1.PeoplePage"
                         xmlns:vm="clr-namespace:MauiApp1.ViewModels"
                         x:DataType="vm:MainViewModel"
                         Title="{Binding Greeting}">
                <ContentPage.Resources>
                    <Color x:Key="Accent">#512BD4</Color>
                </ContentPage.Resources>
                <Grid RowDefinitions="Auto,*" Padding="{OnPlatform iOS='0,20,0,0', Default='0'}">
                    <Label Text="{Binding Owner.Name}"
                           FontSize="{OnIdiom Phone=18, Tablet=24, Default=20}"
                           TextColor="{AppThemeBinding Light={StaticResource Primary}, Dark={StaticResource White}}" />
                    <CollectionView Grid.Row="1" ItemsSource="{Binding People}">
                        <CollectionView.ItemTemplate>
                            <DataTemplate x:DataType="vm:Person">
                                <HorizontalStackLayout Spacing="8">
                                    <Label Text="{Binding Name}" TextColor="{StaticResource Accent}" />
                                    <Label Text="{Binding Age}" />
                                </HorizontalStackLayout>
                            </DataTemplate>
                        </CollectionView.ItemTemplate>
                    </CollectionView>
                </Grid>
            </ContentPage>

            """;

        public const string PeopleCode = """
            namespace MauiApp1;

            public partial class PeoplePage : ContentPage
            {
                public PeoplePage()
                {
                    InitializeComponent();
                }
            }

            """;
    }
}
