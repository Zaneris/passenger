using Passenger.App.Debugging;
using Passenger.Text;

namespace Passenger.UnitTests.Debugger;

/// <summary>Where a breakpoint in Avalonia XAML stops, in a file declaring XAML's language and the designer's namespace
/// under prefixes of its own: a directive read only by the compiler or the designer sets nothing as its element is made,
/// so Toggle Breakpoint on it sets the line's breakpoint and a line holding only such directives begins nothing to stop at.</summary>
public class XamlBreakpointPlacesTests
{
    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:xaml="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:design="http://schemas.microsoft.com/expression/blend/2008"
                xmlns:vm="using:Shop"
                xaml:Class="Shop.MainWindow"
                xaml:DataType="vm:MainViewModel"
                design:DesignWidth="400"
                Title="Shop">
            <Button xaml:Name="Save"
                    xaml:FieldModifier="public" Content="Save" />
        </Window>
        """;

    [Fact]
    public void A_breakpoint_in_XAML_stops_only_where_an_attribute_sets_something_whatever_prefix_names_XAMLs_language() => Cases.All(
        ("an attribute has a breakpoint of its own only where it sets something", () => Cases.Each(
            [("xaml:DataType", false), ("design:DesignWidth", false), ("xaml:Name", true), ("Title", true)], row =>
        {
            (string name, bool sets) = row;
            TextSnapshot text = TextSnapshot.From(Window);
            int at = Window.IndexOf(name + "=", StringComparison.Ordinal);
            (string Name, TextPosition At)? found = XamlBreakpoints.AttributeAt(text, at + 1);
            Assert.True(sets == (found is not null), sets
                ? $"Toggle Breakpoint on {name} set no breakpoint of its own, though it sets its property as the element is made"
                : $"Toggle Breakpoint on {name} set a breakpoint of its own on a directive no code is made for, which never stops");
            Assert.True(found is null || found.Value.Name == name, $"Toggle Breakpoint on {name} set {found?.Name}'s breakpoint");
        })),
        ("a line breakpoint asks for the first name on its line that sets something", () => Cases.Each(
            [("xaml:Class", (string?)null), ("xaml:FieldModifier", "Content")], row =>
        {
            (string line, string? stops) = row;
            TextSnapshot text = TextSnapshot.From(Window);
            int number = text.GetLineFromOffset(Window.IndexOf(line, StringComparison.Ordinal));
            int? column = XamlBreakpoints.Column(text, number, null, null);
            int? expected = stops is null ? null : Window.IndexOf(stops + "=", StringComparison.Ordinal) - text.GetLineStart(number) + 1;
            Assert.True(expected == column, stops is null
                ? $"The line of {line} asks the debugger for column {column}, a directive no code is made for, where it begins nothing to stop at"
                : $"The line of {line} asks the debugger for column {column}, not {stops}'s {expected}, so it stops nowhere or elsewhere");
        })));
}
