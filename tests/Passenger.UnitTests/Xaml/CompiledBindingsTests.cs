using Microsoft.CodeAnalysis.CSharp;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which binding paths start at the data context, and so need a type to start from where Avalonia compiles them
/// (AVLN2100), which it refuses from a data context it has no type for (AVLN2000), which bindings name a source of their
/// own, and what a path's steps are, as its 12.1 compiler reads them.</summary>
public class CompiledBindingsTests
{
    [Fact]
    public void A_binding_starts_at_the_data_context_or_the_source_its_path_or_its_markup_names() => Cases.All(
        ("a path with no source of its own starts at the data context", () => Cases.Each<string?>(
        [
            null,
            " . ",
            "Owner.Name",
            "!!Flag",
            "[0]",
            "Greeting^",
            "((vm:Shop)Item).Name",
            "!((vm:Shop)Item).Closed",
        ], A_path_with_no_source_of_its_own_starts_at_the_data_context)),
        ("a path from an element a relative source an attached property or the data context cast does not", () => Cases.Each<string>(
        [
            "#Other.Text",
            "$parent[StackPanel].Tag",
            "(Grid.Row)",
            "(vm:Shop).Name",
            "((vm:Shop)(Grid.Row))",
            "!",
        ], A_path_from_an_element_a_relative_source_an_attached_property_or_the_data_context_cast_does_not)),
        ("a binding naming an element a source a data type or another relative source names its own", () => Cases.Each<string>(
        [
            "{Binding Text, ElementName=Other}",
            "{Binding Length, Source={StaticResource Word}}",
            "{Binding Greeting, DataType=vm:MainViewModel}",
            "{Binding Tag, RelativeSource={RelativeSource FindAncestor, AncestorType=StackPanel}}",
        ], A_binding_naming_an_element_a_source_a_data_type_or_another_relative_source_names_its_own)),
        ("a binding to its data context names no source of its own", () => Cases.Each<string>(
        [
            "{Binding}",
            "{Binding Greeting, RelativeSource={RelativeSource Mode=DataContext}}",
            "{CompiledBinding Path=Greeting, RelativeSource={RelativeSource DataContext}}",
            "{Binding Greeting, Mode=OneWay, Converter={StaticResource Upper}}",
        ], A_binding_to_its_data_context_names_no_source_of_its_own)),
        ("an elements own data context binding starts above it whatever data type or source it names", () => Cases.Each<string>(
        [
            "{Binding Owner, DataType=vm:Person}",
            "{Binding Greeting, Source={x:Static vm:Statics.Vm}}",
            "{Binding Greeting, RelativeSource={RelativeSource DataContext}}",
        ], An_elements_own_data_context_binding_starts_above_it_whatever_data_type_or_source_it_names)),
        ("an elements own data context binding from an element or a relative source starts there", () => Cases.Each<string>(
        [
            "{Binding Text, ElementName=Other}",
            "{Binding Tag, RelativeSource={RelativeSource Self}}",
        ], An_elements_own_data_context_binding_from_an_element_or_a_relative_source_starts_there)));

    private void A_path_with_no_source_of_its_own_starts_at_the_data_context(string? path) =>
        Assert.True(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting elsewhere, so its binding goes unmarked where the build refuses it");

    private void A_path_from_an_element_a_relative_source_an_attached_property_or_the_data_context_cast_does_not(string path) =>
        Assert.False(CompiledBindings.StartsAtDataContext(path), $"'{path}' read as starting at the data context, so its binding is marked where the build takes it");

    private void A_binding_naming_an_element_a_source_a_data_type_or_another_relative_source_names_its_own(string binding) =>
        Assert.True(XamlFile.NamesSource(Parse(binding)), $"{binding} read as starting at the data context, so it is marked where the build takes it");

    private void A_binding_to_its_data_context_names_no_source_of_its_own(string binding) =>
        Assert.False(XamlFile.NamesSource(Parse(binding)), $"{binding} read as naming a source of its own, so it goes unmarked where the build refuses it");

    [Fact]
    public void A_paths_steps_are_each_read_on_their_type_and_refused_where_the_compiler_has_none() => Cases.All(
        ("a step from a data context the compiler has no type for is refused", () => Cases.Each<string>(
        [
            "Name",
            "!Flag",
            "[0]",
            "Greeting^",
            "((vm:Person)Owner).Name",
        ], A_step_from_a_data_context_the_compiler_has_no_type_for_is_refused)),
        ("the data context itself its cast or a path from elsewhere is taken from one of no type", () => Cases.Each<string?>(
        [
            null,
            " . ",
            "(vm:Person).Name",
            "#Other.Text",
        ], The_data_context_itself_its_cast_or_a_path_from_elsewhere_is_taken_from_one_of_no_type)),
        ("any path from the data context is refused where nothing gives it a type and none where something does", () => Cases.Each<string?>(
        [
            null,
            "Name",
            "((vm:Person)Owner).Name",
        ], Any_path_from_the_data_context_is_refused_where_nothing_gives_it_a_type_and_none_where_something_does)),
        ("a path through an indexer a null conditional step or a cast is read to its end each step on its type", () => Cases.Each<(string, string)>(
        [
            ("Children[0].Title", "Children on Shelf, Title on Shelf"),
            ("People[0].Name", "People on Shelf, Name on Person"),
            ("Map[ada].Name", "Map on Shelf, Name on Person"),
            ("Chosen?.Name", "Chosen on Shelf, Name on Person"),
            ("!Chosen.Name", "Chosen on Shelf, Name on Person"),
            ("Children[0].Children[1].Chosen?.Name", "Children on Shelf, Children on Shelf, Chosen on Shelf, Name on Person"),
            ("((s:Person)Chosen).Name", "Chosen on Shelf, s:Person on Shelf, Name on Person"),
        ], row => A_path_through_an_indexer_a_null_conditional_step_or_a_cast_is_read_to_its_end_each_step_on_its_type(row.Item1, row.Item2))),
        ("a step its type lacks after an indexer or a null conditional step is where the path stops", () => Cases.Each<(string, string, bool)>(
        [
            ("People[0].Nope", "Person", true),
            ("Children[0].Nope.Name", "Shelf", false),
            ("Chosen?.Nope", "Person", true),
        ], row => A_step_its_type_lacks_after_an_indexer_or_a_null_conditional_step_is_where_the_path_stops(row.Item1, row.Item2, row.Item3))),
        ("a paths type is its last steps as written an indexers its items", () => Cases.Each<(string, string)>(
        [
            ("People[0]", "Person"),
            ("Children[0].People", "List`1"),
            ("Map[ada]", "Person"),
        ], row => A_paths_type_is_its_last_steps_as_written_an_indexers_its_items(row.Item1, row.Item2))),
        ("an arrays items are given no type where a lists a collection classs and an enumerables are", An_arrays_items_are_given_no_type_where_a_lists_a_collection_classs_and_an_enumerables_are));

    private void A_step_from_a_data_context_the_compiler_has_no_type_for_is_refused(string path) =>
        Assert.True(CompiledBindings.Refused(CompiledContext.Untyped, path), $"'{path}' taken from a data context of no type, so it goes unmarked where the build refuses it");

    private void The_data_context_itself_its_cast_or_a_path_from_elsewhere_is_taken_from_one_of_no_type(string? path) =>
        Assert.False(CompiledBindings.Refused(CompiledContext.Untyped, path), $"'{path}' refused from a data context of no type, so it is marked where the build takes it");

    private void Any_path_from_the_data_context_is_refused_where_nothing_gives_it_a_type_and_none_where_something_does(string? path)
    {
        Assert.True(CompiledBindings.Refused(CompiledContext.None, path), $"'{path}' taken with no data type, so it goes unmarked where the build refuses it");
        Assert.False(CompiledBindings.Refused(CompiledContext.Typed, path), $"'{path}' refused from a typed data context, so it is marked where the build takes it");
    }

    private void An_elements_own_data_context_binding_starts_above_it_whatever_data_type_or_source_it_names(string binding) =>
        Assert.False(XamlFile.NamesSource(Parse(binding), dataContext: true), $"{binding} as a DataContext read as starting elsewhere, so it goes unmarked where the build refuses it");

    private void An_elements_own_data_context_binding_from_an_element_or_a_relative_source_starts_there(string binding) =>
        Assert.True(XamlFile.NamesSource(Parse(binding), dataContext: true), $"{binding} as a DataContext read as starting at the one above, so it is marked where the build takes it");

    /// <summary>A shelf's view models, and the collections of .NET's they use, as a project's source declares them.</summary>
    private const string Models = """
        using System.Collections.Generic;

        namespace System.Collections.Generic
        {
            public interface IList<T> { }
            public interface IReadOnlyDictionary<TKey, TValue> { }
            public class List<T> : IList<T> { }
            public class Dictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue> { }
        }

        namespace Shop
        {
            public class Shelf
            {
                public List<Person> People { get; } = new();
                public Person[] Array { get; } = [];
                public Dictionary<string, Person> Map { get; } = new();
                public Crowd Crowd { get; } = new();
                public Person? Chosen { get; set; }
                public List<Shelf> Children { get; } = new();
                public string Title { get; set; } = "";
            }

            public class Crowd : List<Person> { }

            public class Person
            {
                public string Name { get; set; } = "";
            }
        }
        """;

    private static readonly TypeSystem Types = new([], [SourceTypes.Read("Models.cs", Models, "Shop", CSharpParseOptions.Default)], "Shop");

    private static BindingPath Read(string path) =>
        BindingPath.Read(Types, Types.Find("Shop.Shelf")!, path, 0, compiled: true, cast => cast == "s:Person" ? Types.Find("Shop.Person") : null);

    private void A_path_through_an_indexer_a_null_conditional_step_or_a_cast_is_read_to_its_end_each_step_on_its_type(string path, string steps)
    {
        BindingPath read = Read(path);
        Assert.True(read.End == PathEnd.Whole && string.Join(", ", read.Steps.Select(s => $"{s.Name} on {s.On.Name}")) == steps,
            $"'{path}' read as {read.End}, {string.Join(", ", read.Steps.Select(s => $"{s.Name} on {s.On.Name}"))}: its steps are neither found as uses nor gone to");
    }

    private void A_step_its_type_lacks_after_an_indexer_or_a_null_conditional_step_is_where_the_path_stops(string path, string on, bool last)
    {
        BindingPath read = Read(path);
        Assert.True(read is { End: PathEnd.Missing, Stop: { Name: "Nope" } stop } && stop.On.Name == on && read.StopsLast == last,
            $"'{path}' read as {read.End} at {read.Stop?.Name} on {read.Stop?.On.Name}, where the build refuses Nope on {on}");
    }

    private void A_paths_type_is_its_last_steps_as_written_an_indexers_its_items(string path, string type) =>
        Assert.True(Read(path).Type?.Name == type, $"'{path}' has the type {Read(path).Type?.Name}, so what its items are is read from the wrong type");

    private void An_arrays_items_are_given_no_type_where_a_lists_a_collection_classs_and_an_enumerables_are()
    {
        Assert.False(CompiledBindings.TypesItemsOf(Read("Array").Type!), "An array's items were typed, so a template over them goes unmarked where the build refuses each step of it");
        Assert.All(["People", "Crowd", "Children"], path => Assert.True(CompiledBindings.TypesItemsOf(Read(path).Type!),
            $"{path}'s items were left untyped, so a template over them is marked where the build takes it"));
    }

    private static MarkupExtension Parse(string text) => MarkupExtension.Parse(text, 0, text.Length) ?? throw new ArgumentException($"{text} is no markup extension");
}
