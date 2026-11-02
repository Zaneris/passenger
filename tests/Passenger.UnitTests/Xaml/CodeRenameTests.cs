using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;

namespace Passenger.UnitTests.Xaml;

/// <summary>What a view's XAML becomes as a C# symbol it uses is renamed: each use's name respelled where XAML resolves it
/// to the symbol (a binding's path, x:DataType, x:Static's type and member), the property and command the MVVM
/// Toolkit makes of a field and a method following theirs, and the rename refused where a use would name another member.</summary>
public sealed class CodeRenameTests
{
    private const string ViewModel = """
        namespace Shop;

        public partial class ShopViewModel
        {
            public string Greeting { get; set; } = "";

            public string Label { get; set; } = "";

            public static string Motto => "Tea";

            [ObservableProperty]
            private string _title = "";

            [ObservableProperty]
            private string _name = "";

            [RelayCommand]
            private void BuyAsync()
            {
            }
        }

        """;

    private const string CodeBehind = """
        namespace Shop;

        public partial class ShopView
        {
            private void OnBought(object? sender, System.EventArgs e)
            {
            }
        }

        """;

    private const string View = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:Shop" x:Class="Shop.ShopView" x:DataType="vm:ShopViewModel">
            <StackPanel>
                <TextBlock Text="{Binding Greeting}" />
                <TextBox Text="{Binding Path=Greeting, Mode=TwoWay}" />
                <TextBlock Text="{x:Static vm:ShopViewModel.Motto}" />
                <TextBlock Text="{Binding Title}" />
                <TextBlock Text="{Binding Name}" />
                <Button Command="{Binding BuyCommand}" Click="OnBought" />
            </StackPanel>
        </UserControl>
        """;

    [Fact]
    public void A_bound_property_renamed_respells_each_binding_of_it_and_nothing_else()
    {
        Assert.Equal(View.Replace("Binding Greeting", "Binding Salutation", StringComparison.Ordinal).Replace("Path=Greeting", "Path=Salutation", StringComparison.Ordinal),
            Renamed("P:Shop.ShopViewModel.Greeting", "Greeting", "Salutation"));
    }

    [Fact]
    public void A_type_renamed_respells_its_x_data_type_and_its_static_members_owner_after_their_prefix()
    {
        Assert.Equal(View.Replace("vm:ShopViewModel", "vm:StoreViewModel", StringComparison.Ordinal), Renamed("T:Shop.ShopViewModel", "ShopViewModel", "StoreViewModel"));
    }

    [Fact]
    public void A_static_member_renamed_respells_the_x_static_that_names_it_after_its_owner()
    {
        Assert.Equal(View.Replace("ShopViewModel.Motto", "ShopViewModel.Slogan", StringComparison.Ordinal), Renamed("P:Shop.ShopViewModel.Motto", "Motto", "Slogan"));
    }

    [Fact]
    public void An_observable_fields_property_and_a_relay_methods_command_follow_the_names_the_toolkit_makes_of_the_new_ones()
    {
        Assert.Equal(View.Replace("Binding Title", "Binding Caption", StringComparison.Ordinal), Renamed("F:Shop.ShopViewModel._title", "_title", "_caption"));
        Assert.Equal(View.Replace("Binding Title", "Binding Heading", StringComparison.Ordinal), Renamed("F:Shop.ShopViewModel._title", "_title", "heading"));
        Assert.Equal(View.Replace("BuyCommand", "OrderCommand", StringComparison.Ordinal), Renamed("M:Shop.ShopViewModel.BuyAsync", "BuyAsync", "Order"));
        Assert.Equal(View, Renamed("M:Shop.ShopViewModel.BuyAsync", "BuyAsync", "Buy"));
    }

    [Fact]
    public void A_rename_whose_binding_would_name_another_member_or_none_the_toolkit_makes_is_refused_saying_where()
    {
        Assert.Equal("'Label' names another property of ShopViewModel, which ShopView.axaml would then use on line 8", Refused("F:Shop.ShopViewModel._name", "_name", "_label"));
        Assert.Equal("the MVVM Toolkit would make no property of '_' for the Title ShopView.axaml uses on line 7", Refused("F:Shop.ShopViewModel._title", "_title", "_"));
    }

    [Theory]
    [InlineData("T:Shop.ShopExtension", "Shop", "Store", "Store")]
    [InlineData("T:Shop.ShopExtension", "Shop", "StoreExtension", "Store")]
    [InlineData("T:Shop.ShopExtension", "ShopExtension", "StoreExtension", "StoreExtension")]
    [InlineData("P:Shop.Order.Total", "Total", "@class", "class")]
    [InlineData("F:Shop.Order.m_total", "Total", "m_sum", "Sum")]
    [InlineData("F:Shop.Kind.Left", "left", "Start", "Start")]
    public void A_use_is_respelled_as_the_new_name_spells_what_it_named(string id, string written, string name, string expected)
    {
        CodeSymbol symbol = Assert.IsType<CodeSymbol>(CodeSymbol.Of(id, null));
        Assert.True(symbol.Respells(written, name, out string? renamed) && renamed == expected, $"{written} of {id} renamed to {name} was respelled {renamed ?? "as nothing"}");
    }

    [Fact]
    public void A_use_that_does_not_spell_the_symbols_name_is_not_respelled()
    {
        CodeSymbol symbol = Assert.IsType<CodeSymbol>(CodeSymbol.Of("F:Shop.Order.TotalProperty", null));
        Assert.False(symbol.Respells("Total", "SumProperty", out _), "An attached property's registered name, which its field does not spell, was respelled with the field");
    }

    /// <summary>The view with the edits its uses of a symbol take as it is renamed, the symbol declared in the view model at
    /// its name's first declaration there.</summary>
    private static string Renamed(string id, string declared, string name)
    {
        (XamlFile view, CodeSymbol symbol) = Read(id, declared);
        (IReadOnlyList<TextEdit> edits, string? problem) = XamlRefactorings.CodeRename(view, symbol, XamlNavigation.Uses(view, symbol.NamesIn(view.Types), symbol.Is), name);
        Assert.True(problem is null, $"Renaming {declared} to {name} was refused: {problem}");
        return XamlFiles.Applied(View, edits);
    }

    private static string? Refused(string id, string declared, string name)
    {
        (XamlFile view, CodeSymbol symbol) = Read(id, declared);
        (IReadOnlyList<TextEdit> edits, string? problem) = XamlRefactorings.CodeRename(view, symbol, XamlNavigation.Uses(view, symbol.NamesIn(view.Types), symbol.Is), name);
        Assert.True(edits.Count == 0, $"A refused rename of {declared} gave edits: {string.Join(", ", edits)}");
        return problem;
    }

    private static (XamlFile View, CodeSymbol Symbol) Read(string id, string declared)
    {
        XamlFile view = XamlFiles.Read("ShopView.axaml", View, AvaloniaDialect.Instance, ("ShopViewModel.cs", ViewModel), ("ShopView.axaml.cs", CodeBehind));
        int at = ViewModel.IndexOf($" {declared}", StringComparison.Ordinal) + 1;
        Assert.True(at > 0, $"The view model declares no {declared}");
        int line = ViewModel.AsSpan(0, at).Count('\n');
        SourcePlace place = new(XamlFiles.PathOf("ShopViewModel.cs"), line, at - (ViewModel.LastIndexOf('\n', at - 1) + 1));
        return (view, Assert.IsType<CodeSymbol>(CodeSymbol.Of(id, place)));
    }
}
