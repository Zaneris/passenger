using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A rename started in XAML renames everywhere, and what the MVVM Toolkit makes of a field follows its rename, on an
/// app from Avalonia's template whose view model uses the toolkit (its package from the package cache, its generators
/// running in the language server): Rename on a binding renames the view model's property, its C# uses and every other
/// binding, one step of Undo taking it all back; Rename on an <c>[ObservableProperty]</c> field renames its property's C#
/// uses, its OnXChanged and the bindings to it, and Rename on such a binding renames the field so it makes the name typed;
/// Rename on a <c>[RelayCommand]</c> method renames its command's and cancel command's uses, and Rename on a binding to the
/// cancel command renames the method.</summary>
public sealed class XamlRenameTests : IClassFixture<XamlRenameTests.Shop>
{
    private const string ViewModel = """
        using CommunityToolkit.Mvvm.ComponentModel;

        namespace Shop.ViewModels;

        public partial class MainViewModel : ObservableObject
        {
            public string Greeting { get; set; } = "Hello";

            [ObservableProperty]
            private string _offer = "Tea";

            public int Changes { get; private set; }

            partial void OnOfferChanged(string value)
            {
                Changes++;
            }
        }

        """;

    private const string Reader = """
        namespace Shop.ViewModels;

        public static class Reader
        {
            public static string Read(MainViewModel model) => model.Greeting + " " + model.Offer;
        }

        """;

    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="using:Shop.ViewModels"
                xmlns:views="using:Shop.Views"
                x:Class="Shop.MainWindow"
                x:DataType="vm:MainViewModel">
            <StackPanel>
                <TextBlock Text="{Binding Greeting}" />
                <TextBlock Text="{Binding Offer}" />
                <views:Card />
            </StackPanel>
        </Window>

        """;

    private const string Card = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:Shop.ViewModels"
                     x:Class="Shop.Views.Card"
                     x:DataType="vm:MainViewModel">
            <StackPanel>
                <TextBlock Text="{Binding Greeting}" />
                <TextBlock Text="{Binding Offer}" />
            </StackPanel>
        </UserControl>

        """;

    private const string CardCode = """
        using Avalonia.Controls;

        namespace Shop.Views;

        public partial class Card : UserControl
        {
            public Card()
            {
                InitializeComponent();
            }
        }

        """;

    private readonly Shop shop;

    public XamlRenameTests(Shop shop)
    {
        this.shop = shop;
        shop.Reset();
    }

    private string ViewModelPath => shop.PathOf("ViewModels/MainViewModel.cs");

    private string ReaderPath => shop.PathOf("ViewModels/Reader.cs");

    private string CardPath => shop.PathOf("Views/Card.axaml");

    /// <summary>Rename on a binding's property in a view hands over to C#'s: the view model's property, its use in another C#
    /// file and the other view's binding follow, the view model's file never opened in a tab, and one Undo takes it all back.</summary>
    [AvaloniaFact]
    public void Rename_on_a_binding_renames_the_view_models_property_its_csharp_uses_and_every_other_binding_as_one_step_of_undo()
    {
        MainWindow window = shop.Open(shop.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;

        XamlTests.RenameInCode(window, "{Binding Greet", "Salutation");

        Ui.WaitFor(() => File.ReadAllText(ViewModelPath).Contains("public string Salutation { get; set; }", StringComparison.Ordinal),
            () => $"the view model's property renamed from its binding: {window.Status}\n{File.ReadAllText(ViewModelPath)}");
        Assert.Contains("{Binding Salutation}", view.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(Reader.Replace("model.Greeting", "model.Salutation", StringComparison.Ordinal), File.ReadAllText(ReaderPath));
        Assert.Equal(Card.Replace("{Binding Greeting}", "{Binding Salutation}", StringComparison.Ordinal), File.ReadAllText(CardPath));
        Assert.DoesNotContain(window.EditorArea.Documents, d => d.Name == "MainViewModel.cs");
        Assert.Equal("Renamed in 4 files", window.Status);

        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename Greeting to Salutation? It changes ", StringComparison.Ordinal), $"Undo asked {string.Join(" | ", asked)}");
        Ui.WaitFor(() => File.ReadAllText(ViewModelPath) == ViewModel, () => $"Undo to put the view model back: {window.Status}\n{File.ReadAllText(ViewModelPath)}");
        Assert.Equal(Reader, File.ReadAllText(ReaderPath));
        Assert.Equal(Card, File.ReadAllText(CardPath));
        Assert.Equal(Window, view.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>A view nested past what Passenger reads, its one binding to the property past where reading stopped: renaming
    /// the property is refused saying so, as that use would go unseen, and nothing is renamed.</summary>
    [AvaloniaFact]
    public void A_rename_whose_use_stands_past_where_a_view_was_read_is_refused_and_renames_nothing()
    {
        string deep = "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:vm=\"using:Shop.ViewModels\" x:Class=\"Shop.Views.Deep\" x:DataType=\"vm:MainViewModel\">\n"
            + string.Concat(Enumerable.Repeat("<Border>", 300)) + "<TextBlock Text=\"{Binding Greeting}\" />" + string.Concat(Enumerable.Repeat("</Border>", 300))
            + "\n</UserControl>\n";
        shop.Write([("Views/Deep.axaml", deep)]);
        MainWindow window = shop.Open(ViewModelPath);
        OpenDocument model = window.EditorArea.Active!;

        XamlTests.RenameInCode(window, "public string Greet", "Salutation");

        Ui.WaitFor(() => window.Status?.Contains("Deep.axaml is nested too deeply for Passenger to read whole", StringComparison.Ordinal) == true,
            () => $"the rename refused for the view read in part: {window.Status}");
        Assert.Equal(ViewModel, model.Text.Snapshot.ToString());
        Assert.Equal((Reader, Card, deep), (File.ReadAllText(ReaderPath), File.ReadAllText(CardPath), File.ReadAllText(shop.PathOf("Views/Deep.axaml"))));
        window.Close();
    }

    /// <summary>Rename on an <c>[ObservableProperty]</c> field renames what the toolkit makes of it, which Roslyn's rename of the
    /// field alone leaves: its property's use in another C# file, the code's own OnXChanged, and both views' bindings; then
    /// Rename on such a binding renames the field, keeping its underscore, so its property is the name typed.</summary>
    [AvaloniaFact]
    public void Renaming_an_ObservableProperty_field_renames_its_propertys_uses_and_change_method_and_a_binding_to_its_property_renames_the_field()
    {
        MainWindow window = shop.Open(ViewModelPath);
        OpenDocument model = window.EditorArea.Active!;

        XamlTests.RenameInCode(window, "private string _off", "_deal");

        Ui.WaitFor(() => File.ReadAllText(ReaderPath).Contains("model.Deal", StringComparison.Ordinal),
            () => $"the field's property's C# use renamed with it: {window.Status}\n{File.ReadAllText(ReaderPath)}");
        string renamed = model.Text.Snapshot.ToString();
        Assert.Contains("private string _deal = \"Tea\";", renamed, StringComparison.Ordinal);
        Assert.Contains("partial void OnDealChanged(string value)", renamed, StringComparison.Ordinal);
        Assert.Contains("{Binding Deal}", File.ReadAllText(CardPath), StringComparison.Ordinal);
        Assert.Contains("{Binding Deal}", File.ReadAllText(shop.PathOf("MainWindow.axaml")), StringComparison.Ordinal);

        window.OpenFile(CardPath);
        Ui.Settle();
        XamlTests.RenameInCode(window, "{Binding De", "Bargain");

        Ui.WaitFor(() => model.Text.Snapshot.ToString().Contains("private string _bargain = \"Tea\";", StringComparison.Ordinal),
            () => $"the field renamed from a binding to its property: {window.Status}\n{model.Text.Snapshot}");
        Assert.Contains("partial void OnBargainChanged(string value)", model.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("model.Bargain", File.ReadAllText(ReaderPath), StringComparison.Ordinal);
        Assert.Contains("{Binding Bargain}", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding Bargain}", File.ReadAllText(shop.PathOf("MainWindow.axaml")), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Rename on a <c>[RelayCommand(IncludeCancelCommand = true)]</c> method renames what the toolkit makes of it, which
    /// Roslyn's rename of the method alone leaves: the C# uses and bindings of both its commands, so the code goes on
    /// compiling; then Rename on a binding to the cancel command renames the method, keeping its Async.</summary>
    [AvaloniaFact]
    public void Renaming_a_RelayCommand_method_renames_its_command_and_cancel_commands_uses_and_a_binding_to_the_cancel_command_renames_the_method()
    {
        shop.Write([("ViewModels/Loader.cs", Loader), ("ViewModels/Starter.cs", Starter), ("Views/LoadCard.axaml", LoadCard), ("Views/LoadCard.axaml.cs", LoadCardCode)]);
        string starter = shop.PathOf("ViewModels/Starter.cs");
        string view = shop.PathOf("Views/LoadCard.axaml");
        MainWindow window = shop.Open(shop.PathOf("ViewModels/Loader.cs"));
        OpenDocument model = window.EditorArea.Active!;

        XamlTests.RenameInCode(window, "async Task Loa", "RefreshAsync");

        Ui.WaitFor(() => File.ReadAllText(starter).Contains("loader.RefreshCancelCommand", StringComparison.Ordinal),
            () => $"the cancel command's C# use renamed with the method: {window.Status}\n{File.ReadAllText(starter)}");
        Assert.Contains("private async Task RefreshAsync(", model.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("loader.RefreshCommand", File.ReadAllText(starter), StringComparison.Ordinal);
        Assert.Contains("{Binding RefreshCommand}", File.ReadAllText(view), StringComparison.Ordinal);
        Assert.Contains("{Binding RefreshCancelCommand}", File.ReadAllText(view), StringComparison.Ordinal);

        window.OpenFile(view);
        Ui.Settle();
        XamlTests.RenameInCode(window, "{Binding RefreshCan", "ReloadCancelCommand");

        Ui.WaitFor(() => model.Text.Snapshot.ToString().Contains("private async Task ReloadAsync(", StringComparison.Ordinal),
            () => $"the method renamed from a binding to its cancel command: {window.Status}\n{model.Text.Snapshot}");
        Assert.Contains("loader.ReloadCommand", File.ReadAllText(starter), StringComparison.Ordinal);
        Assert.Contains("loader.ReloadCancelCommand", File.ReadAllText(starter), StringComparison.Ordinal);
        Assert.Contains("{Binding ReloadCancelCommand}", window.EditorArea.Active!.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    private const string Loader = """
        using System.Threading;
        using System.Threading.Tasks;
        using CommunityToolkit.Mvvm.ComponentModel;
        using CommunityToolkit.Mvvm.Input;

        namespace Shop.ViewModels;

        public partial class Loader : ObservableObject
        {
            [RelayCommand(IncludeCancelCommand = true)]
            private async Task LoadAsync(CancellationToken token)
            {
                await Task.Delay(1, token);
            }
        }

        """;

    private const string Starter = """
        namespace Shop.ViewModels;

        public static class Starter
        {
            public static void Start(Loader loader)
            {
                loader.LoadCommand.Execute(null);
                loader.LoadCancelCommand.Execute(null);
            }
        }

        """;

    private const string LoadCard = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:Shop.ViewModels"
                     x:Class="Shop.Views.LoadCard"
                     x:DataType="vm:Loader">
            <StackPanel>
                <Button Command="{Binding LoadCommand}" />
                <Button Command="{Binding LoadCancelCommand}" />
            </StackPanel>
        </UserControl>

        """;

    private const string LoadCardCode = """
        using Avalonia.Controls;

        namespace Shop.Views;

        public partial class LoadCard : UserControl
        {
            public LoadCard()
            {
                InitializeComponent();
            }
        }

        """;

    /// <summary>The app from Avalonia's template with the MVVM Toolkit referenced, a view model of a plain property and an
    /// <c>[ObservableProperty]</c> field, a C# file reading both, and two views binding both.</summary>
    public sealed class Shop : SharedProject
    {
        public Shop()
            : base("xaml-rename-")
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
                $"  <ItemGroup>\n    <PackageReference Include=\"CommunityToolkit.Mvvm\" Version=\"{AvaloniaApp.Metadata("CommunityToolkitMvvmVersion")}\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal));
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(folder, "Views"));
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), ViewModel);
            File.WriteAllText(Path.Combine(folder, "ViewModels/Reader.cs"), Reader);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), Window);
            File.WriteAllText(Path.Combine(folder, "Views/Card.axaml"), Card);
            File.WriteAllText(Path.Combine(folder, "Views/Card.axaml.cs"), CardCode);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
        }
    }
}
