using Microsoft.CodeAnalysis.CSharp;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;

namespace Passenger.UnitTests.Xaml;

/// <summary>What source generators add to a type of the project's C#, as the language server tells it, is part of that
/// type for its XAML: a binding to a generated member is checked as one to a member the C# declares, a generated member
/// the C# declares itself (the MVVM Toolkit's read from its field or method, a partial property) stays as the C# reads it,
/// and a part of a type the C# no longer declares makes no type of its own.</summary>
public sealed class GeneratedPartTests
{
    private const string ViewModel = """
        namespace App;

        public partial class MainViewModel
        {
            [Reactive]
            private string _offer = "Tea";

            [ObservableProperty]
            private string _price = "";

            private void Save()
            {
            }
        }
        """;

    private static readonly SourcePlace Offer = new(XamlFiles.PathOf("MainViewModel.cs"), 5, 19);
    private static readonly SourcePlace Save = new(XamlFiles.PathOf("MainViewModel.cs"), 10, 17);

    private static GeneratedMember Made(string name, string type, SourcePlace place) =>
        new(name, MemberKind.Property, TypeRef.Named(type), false, true, [], [], [], place);

    private static TypeSystem Types(params GeneratedPart[] generated) =>
        new([], [SourceTypes.Read(XamlFiles.PathOf("MainViewModel.cs"), ViewModel, "App", CSharpParseOptions.Default)], "App", generated: generated);

    [Fact]
    public void A_generated_member_is_a_member_of_its_type_once_and_a_binding_path_reaches_it() => Cases.All(
        ("a generated property and command are members of the type placed where they are made of", A_generated_property_and_command_are_members_of_the_type_placed_where_they_are_made_of),
        ("a generated member the csharp declares itself is kept once as the csharp reads it", A_generated_member_the_csharp_declares_itself_is_kept_once_as_the_csharp_reads_it),
        ("a part of a type the csharp no longer declares makes no type", A_part_of_a_type_the_csharp_no_longer_declares_makes_no_type),
        ("a binding path reaches a generated member and stops at a name nothing makes", () => Cases.Each<(string, PathEnd)>(
        [
            ("SaveCommand", PathEnd.Whole),
            ("Missing", PathEnd.Missing),
        ], row => A_binding_path_reaches_a_generated_member_and_stops_at_a_name_nothing_makes(row.Item1, row.Item2))));

    private void A_generated_property_and_command_are_members_of_the_type_placed_where_they_are_made_of()
    {
        TypeSystem types = Types(new GeneratedPart("App.MainViewModel", [Offer.Path], [Made("Offer", "System.String", Offer), Made("SaveCommand", "System.Windows.Input.ICommand", Save)]));
        ClrType model = types.Find("App.MainViewModel")!;
        Assert.Equal(Offer, Assert.Single(model.Members, m => m.Name == "Offer").Place);
        Assert.Equal(Save, Assert.Single(model.Members, m => m.Name == "SaveCommand").Place);
        Assert.Equal([new SourcePlace(Offer.Path, 2, 21)], types.PlacesOf("App.MainViewModel"));
    }

    private void A_generated_member_the_csharp_declares_itself_is_kept_once_as_the_csharp_reads_it()
    {
        SourcePlace generatedFile = new("/generated/MainViewModel.g.cs", 3, 4);
        ClrType model = Types(new GeneratedPart("App.MainViewModel", [Offer.Path], [Made("Price", "System.Object", generatedFile)])).Find("App.MainViewModel")!;
        ClrMember price = Assert.Single(model.Members, m => m.Name == "Price");
        Assert.True(price.Type.Name == "System.String" && price.Place == new SourcePlace(Offer.Path, 8, 19),
            $"the toolkit's property read from its field gave way to the generated one: {price.Type.Name} at {price.Place}");
    }

    private void A_part_of_a_type_the_csharp_no_longer_declares_makes_no_type()
    {
        TypeSystem types = Types(new GeneratedPart("App.Gone", [Offer.Path], [Made("Offer", "System.String", Offer)]));
        Assert.Null(types.Find("App.Gone"));
    }

    private void A_binding_path_reaches_a_generated_member_and_stops_at_a_name_nothing_makes(string path, PathEnd end)
    {
        TypeSystem types = Types(new GeneratedPart("App.MainViewModel", [Offer.Path], [Made("Offer", "System.String", Offer), Made("SaveCommand", "System.Windows.Input.ICommand", Save)]));
        BindingPath read = BindingPath.Read(types, types.Find("App.MainViewModel")!, path, 0, compiled: true, _ => null);
        Assert.True(read.End == end, $"'{path}' read as {read.End} where the build reads it {end}");
    }
}
