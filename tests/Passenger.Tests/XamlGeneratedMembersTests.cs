using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>XAML knows a view model's members as the build does, whichever source generator made them, on an app from
/// Avalonia's template whose view model uses ReactiveUI's generators (their packages from the package cache, running in the
/// language server): a binding to a <c>[Reactive]</c> field's property or a <c>[ReactiveCommand]</c> method's command is not
/// marked, completion offers both, Go to Declaration from each lands on the field or method it is made of and Find Usages
/// from that lists it, while a binding to a name nothing declares or generates is still marked; a command typed into the
/// open view model is known to a binding to it once the generators have made it.</summary>
public sealed class XamlGeneratedMembersTests : IClassFixture<XamlGeneratedMembersTests.Reactive>
{
    private const string ViewModel = """
        using ReactiveUI;
        using ReactiveUI.SourceGenerators;

        namespace Shop.ViewModels;

        public partial class MainViewModel : ReactiveObject
        {
            [Reactive]
            private string _offer = "Tea";

            [ReactiveCommand]
            private void Save()
            {
            }
        }

        """;

    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="using:Shop.ViewModels"
                x:Class="Shop.MainWindow"
                x:DataType="vm:MainViewModel">
            <StackPanel>
                <TextBox Text="{Binding Offer}" />
                <Button Command="{Binding SaveCommand}" />
                <TextBlock Text="{Binding Price}" />
            </StackPanel>
        </Window>

        """;

    private readonly Reactive project;

    public XamlGeneratedMembersTests(Reactive project)
    {
        this.project = project;
        project.Reset();
    }

    private string ViewModelPath => project.PathOf("ViewModels/MainViewModel.cs");

    /// <summary>Bindings to what ReactiveUI's generators make are known, offered and lead to the field or method they
    /// are made of, a command typed into the open view model known once the generator has made it.</summary>
    [AvaloniaFact]
    public void Bindings_to_what_reactiveuis_generators_make_are_known_as_they_are_made()
    {
        Flow.StepApart("bindings to what ReactiveUI's generators make are known, offered and lead to the field or method they are made of", () => new XamlGeneratedMembersTests(project), t => t.Bindings_to_what_reactiveuis_generators_make_are_known_offered_and_lead_to_the_field_or_method_they_are_made_of());
        Flow.StepApart("a command typed into the open view model is known to a binding to it once the generator has made it", () => new XamlGeneratedMembersTests(project), t => t.A_command_typed_into_the_open_view_model_is_known_to_a_binding_to_it_once_the_generator_has_made_it());
    }

    /// <summary>Once the language server has loaded the app, the bindings to what ReactiveUI's generators make are not marked
    /// and the one to a property nothing makes is; completion in a binding offers the generated property and command, Go to
    /// Declaration from each binding lands on the field or method it is made of, and Find Usages from the method lists the
    /// binding to its command.</summary>
    private void Bindings_to_what_reactiveuis_generators_make_are_known_offered_and_lead_to_the_field_or_method_they_are_made_of()
    {
        MainWindow window = project.Open(project.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;

        Ui.WaitFor(() => view.Markers.Select(m => m.Message).SequenceEqual(["Cannot resolve property 'Price' on MainViewModel"]),
            () => $"only the binding to the property nothing declares or generates marked: [{string.Join(", ", view.Markers.Select(m => m.Message))}]");

        Ui.PlaceAfter(editor, "<TextBlock Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("SaveCommand"), () => $"the generated command offered in a binding: [{string.Join(", ", window.Assist.CompletionLabels)}]");
        Assert.Contains("Offer", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "{Binding SaveComm");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "MainViewModel.cs", () => $"Go to Declaration of the command to open the view model: {window.Status}");
        Assert.Contains("private void |Save()", XamlTests.CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Off");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "MainViewModel.cs", () => $"Go to Declaration of the property to open the view model: {window.Status}");
        Assert.Contains("private string |_offer", XamlTests.CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "private void Sa");
        Assert.Contains("MainWindow.axaml:8 <Button Command=\"{Binding SaveCommand}\" />", XamlTests.FoundUsages(window));
        window.Close();
    }

    /// <summary>A binding to a command no method makes yet is marked; the method typed into the open view model, marked for
    /// ReactiveUI's generator, makes it known to the binding once typing pauses, nothing saved.</summary>
    private void A_command_typed_into_the_open_view_model_is_known_to_a_binding_to_it_once_the_generator_has_made_it()
    {
        MainWindow window = project.Open(project.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<TextBlock Text=\"{Binding Price}\" />");
        window.KeyTextInput("\n        <Button Command=\"{Binding ClearCommand}\" />");
        Ui.WaitFor(() => view.Markers.Any(m => m.Message == "Cannot resolve property 'ClearCommand' on MainViewModel"),
            () => $"the binding to a command nothing makes yet marked: [{string.Join(", ", view.Markers.Select(m => m.Message))}]");

        window.OpenFile(ViewModelPath);
        Ui.Settle();
        CodeEditor code = window.EditorArea.CodeEditor;
        int end = code.Document.Snapshot.ToString().LastIndexOf('}');
        code.ApplyEdits([new TextEdit(end, 0, "\n    [ReactiveCommand]\n    private void Clear()\n    {\n    }\n")]);

        Ui.WaitFor(() => view.Markers.Select(m => m.Message).SequenceEqual(["Cannot resolve property 'Price' on MainViewModel"]),
            () => $"the binding to the command typed into the view model no longer marked: [{string.Join(", ", view.Markers.Select(m => m.Message))}]");
        Assert.Equal(ViewModel, File.ReadAllText(ViewModelPath));
        window.Close();
    }

    /// <summary>The app from Avalonia's template with ReactiveUI and its source generators referenced, a view model of a
    /// <c>[Reactive]</c> field and a <c>[ReactiveCommand]</c> method, and a window binding both and a property nothing makes.</summary>
    public sealed class Reactive : SharedProject
    {
        public Reactive()
            : base("xaml-generated-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(() => MakeAsync(Folder), Folder);
            await PrepareAsync();
        }

        private static async Task MakeAsync(string folder)
        {
            await AvaloniaApp.CreateAsync(folder, "Shop");
            string project = Path.Combine(folder, "Shop.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
                $"  <ItemGroup>\n    <PackageReference Include=\"ReactiveUI\" Version=\"{AvaloniaApp.Metadata("ReactiveUiVersion")}\" />\n"
                + $"    <PackageReference Include=\"ReactiveUI.SourceGenerators\" Version=\"{AvaloniaApp.Metadata("ReactiveUiGeneratorsVersion")}\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal));
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), ViewModel);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), Window);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
        }
    }
}
