using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using static Passenger.Tests.XamlTests;

namespace Passenger.Tests;

/// <summary>What the MVVM Toolkit makes of a field or method follows its rename in a .NET MAUI app, as in any other (its
/// package from the package cache, its generators and MAUI's running in the language server): Rename on an
/// <c>[ObservableProperty]</c> field renames its property's C# use, a page's binding, a data template's compiled binding and
/// a C# expression reading it, and Rename on such a binding or on its read in an expression renames the field so it makes
/// the name typed; the same for a <c>[RelayCommand]</c> method and its command.</summary>
public sealed class MauiToolkitRenameTests : IClassFixture<MauiToolkitRenameTests.Greeters>
{
    private readonly Greeters app;

    public MauiToolkitRenameTests(Greeters app)
    {
        this.app = app;
        app.Reset();
    }

    private string ViewModelPath => app.PathOf("ViewModels/Greeter.cs");

    private string ReaderPath => app.PathOf("ViewModels/Reader.cs");

    private string PagePath => app.PathOf("GreeterPage.xaml");

    /// <summary>A toolkit field's or method's rename reaches every use of what it makes, and a rename from a binding or an
    /// expression renames the field or method.</summary>
    [AvaloniaFact]
    public void What_the_toolkit_makes_of_a_field_or_method_follows_its_rename_from_the_code_a_binding_or_an_expression()
    {
        Flow.StepApart("renaming an ObservableProperty field renames its property's C# use, bindings and expression, and a binding renames the field", () => new MauiToolkitRenameTests(app),
            t => t.Renaming_an_ObservableProperty_field_renames_its_propertys_csharp_use_bindings_and_expression_and_a_binding_renames_the_field());
        Flow.StepApart("renaming an ObservableProperty's property from an expression renames the field", () => new MauiToolkitRenameTests(app),
            t => t.Renaming_an_ObservablePropertys_property_from_an_expression_renames_the_field());
        Flow.StepApart("renaming a RelayCommand method renames its command's C# use, binding and expression, and a binding renames the method", () => new MauiToolkitRenameTests(app),
            t => t.Renaming_a_RelayCommand_method_renames_its_commands_csharp_use_binding_and_expression_and_a_binding_renames_the_method());
    }

    /// <summary>Rename on the field renames every use of its property, which Roslyn's rename of the field alone leaves; then
    /// Rename on a binding to the property renames the field, its underscore kept, and every use follows again.</summary>
    private void Renaming_an_ObservableProperty_field_renames_its_propertys_csharp_use_bindings_and_expression_and_a_binding_renames_the_field()
    {
        MainWindow window = app.Open(ViewModelPath);
        OpenDocument model = window.EditorArea.Active!;

        RenameInCode(window, "private string _gree", "_welcome");

        Ui.WaitFor(() => File.ReadAllText(PagePath).Contains("{vm:Helper.Shout(Welcome)}", StringComparison.Ordinal),
            () => $"the expression reading the field's property renamed with it: {window.Status}\n{File.ReadAllText(PagePath)}");
        Assert.Contains("private string _welcome = \"Hello\";", model.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(Greeters.Reader.Replace("greeter.Greeting", "greeter.Welcome", StringComparison.Ordinal), File.ReadAllText(ReaderPath));
        Assert.Equal(Greeters.Page.Replace("Greeting", "Welcome", StringComparison.Ordinal), File.ReadAllText(PagePath));

        window.OpenFile(PagePath);
        Ui.Settle();
        RenameInCode(window, "Title=\"{Binding Welc", "Salutation");

        Ui.WaitFor(() => model.Text.Snapshot.ToString().Contains("private string _salutation = \"Hello\";", StringComparison.Ordinal),
            () => $"the field renamed from a binding to its property: {window.Status}\n{model.Text.Snapshot}");
        Assert.Contains("greeter.Salutation", File.ReadAllText(ReaderPath), StringComparison.Ordinal);
        Assert.Equal(Greeters.Page.Replace("Greeting", "Salutation", StringComparison.Ordinal), window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Rename on the property's read in a C# expression renames the field it is made of, as Rename on a binding does.</summary>
    private void Renaming_an_ObservablePropertys_property_from_an_expression_renames_the_field()
    {
        MainWindow window = app.Open(PagePath);

        RenameInCode(window, "{vm:Helper.Shout(Gree", "Welcome");

        Ui.WaitFor(() => File.ReadAllText(ViewModelPath).Contains("private string _welcome = \"Hello\";", StringComparison.Ordinal),
            () => $"the field renamed from its property's read in an expression: {window.Status}\n{File.ReadAllText(ViewModelPath)}");
        Assert.Equal(Greeters.Page.Replace("Greeting", "Welcome", StringComparison.Ordinal), window.EditorArea.Active!.Text.Snapshot.ToString());
        Assert.Contains("greeter.Welcome", File.ReadAllText(ReaderPath), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Rename on a <c>[RelayCommand]</c> method renames its command's C# use, its binding and its expression; then
    /// Rename on the command's binding renames the method, and the command's C# use follows again.</summary>
    private void Renaming_a_RelayCommand_method_renames_its_commands_csharp_use_binding_and_expression_and_a_binding_renames_the_method()
    {
        MainWindow window = app.Open(ViewModelPath);
        OpenDocument model = window.EditorArea.Active!;

        RenameInCode(window, "private void Lo", "Refresh");

        Ui.WaitFor(() => File.ReadAllText(PagePath).Contains("Command=\"{RefreshCommand}\"", StringComparison.Ordinal),
            () => $"the expression reading the method's command renamed with it: {window.Status}\n{File.ReadAllText(PagePath)}");
        Assert.Contains("private void Refresh()", model.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("greeter.RefreshCommand.Execute(null);", File.ReadAllText(ReaderPath), StringComparison.Ordinal);
        Assert.Equal(Greeters.Page.Replace("LoadCommand", "RefreshCommand", StringComparison.Ordinal), File.ReadAllText(PagePath));

        window.OpenFile(PagePath);
        Ui.Settle();
        RenameInCode(window, "{Binding RefreshCom", "ReloadCommand");

        Ui.WaitFor(() => model.Text.Snapshot.ToString().Contains("private void Reload()", StringComparison.Ordinal),
            () => $"the method renamed from a binding to its command: {window.Status}\n{model.Text.Snapshot}");
        Assert.Contains("greeter.ReloadCommand.Execute(null);", File.ReadAllText(ReaderPath), StringComparison.Ordinal);
        Assert.Equal(Greeters.Page.Replace("LoadCommand", "ReloadCommand", StringComparison.Ordinal), window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>A MAUI app (a plain net11.0 project on MAUI's controls, which restores and builds without MAUI's workload)
    /// referencing the MVVM Toolkit: a view model of an <c>[ObservableProperty]</c> field and a <c>[RelayCommand]</c> method,
    /// a C# file using what the toolkit makes of both, and a page reading them through a binding, a data template's compiled
    /// binding and C# expressions.</summary>
    public sealed class Greeters : SharedProject
    {
        public Greeters()
            : base("maui-toolkit-")
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
            File.WriteAllText(Path.Combine(folder, "Greeters.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <RootNamespace>Greeters</RootNamespace>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Maui.Controls" Version="{version}" />
                    <PackageReference Include="CommunityToolkit.Mvvm" Version="{AvaloniaApp.Metadata("CommunityToolkitMvvmVersion")}" />
                  </ItemGroup>
                </Project>

                """);
            File.WriteAllText(Path.Combine(folder, "ViewModels/Greeter.cs"), ViewModel);
            File.WriteAllText(Path.Combine(folder, "ViewModels/Reader.cs"), Reader);
            File.WriteAllText(Path.Combine(folder, "GreeterPage.xaml"), Page);
            File.WriteAllText(Path.Combine(folder, "GreeterPage.xaml.cs"), PageCode);
            return Task.CompletedTask;
        }

        public const string ViewModel = """
            using System.Collections.ObjectModel;
            using CommunityToolkit.Mvvm.ComponentModel;
            using CommunityToolkit.Mvvm.Input;

            namespace Greeters.ViewModels;

            public partial class Greeter : ObservableObject
            {
                [ObservableProperty]
                private string _greeting = "Hello";

                public ObservableCollection<Greeter> Others { get; } = [];

                public int Loads { get; private set; }

                [RelayCommand]
                private void Load()
                {
                    Loads++;
                }
            }

            public static class Helper
            {
                public static string Shout(string text) => text.ToUpperInvariant();
            }

            """;

        public const string Reader = """
            namespace Greeters.ViewModels;

            public static class Reader
            {
                public static string Read(Greeter greeter)
                {
                    greeter.LoadCommand.Execute(null);
                    return greeter.Greeting;
                }
            }

            """;

        /// <summary>A page binding the property through its x:DataType and a data template's, reading it and the command in C#
        /// expressions too.</summary>
        public const string Page = """
            <ContentPage x:Class="Greeters.GreeterPage"
                         xmlns:vm="clr-namespace:Greeters.ViewModels"
                         x:DataType="vm:Greeter"
                         Title="{Binding Greeting}">
                <VerticalStackLayout>
                    <Label Text="{vm:Helper.Shout(Greeting)}" />
                    <Button Text="Load" Command="{Binding LoadCommand}" />
                    <Button Text="Again" Command="{LoadCommand}" />
                    <CollectionView ItemsSource="{Binding Others}">
                        <CollectionView.ItemTemplate>
                            <DataTemplate x:DataType="vm:Greeter">
                                <Label Text="{Binding Greeting, Mode=OneWay}" />
                            </DataTemplate>
                        </CollectionView.ItemTemplate>
                    </CollectionView>
                </VerticalStackLayout>
            </ContentPage>

            """;

        public const string PageCode = """
            namespace Greeters;

            public partial class GreeterPage : ContentPage
            {
                public GreeterPage()
                {
                    InitializeComponent();
                }
            }

            """;
    }
}
