using Passenger.App.LiveTree;
using Passenger.Text;

namespace Passenger.UnitTests.Xaml;

/// <summary>The bindings a debugged app's values are shown after, read from an .axaml file's markup: an attribute of XAML's
/// language, the designer's or markup compatibility's sets nothing in the running app, known by the namespace its prefix
/// is declared for, never by the prefix's spelling.</summary>
public sealed class XamlBindingsTests
{
    [Fact]
    public void Bindings_are_read_by_the_namespace_each_prefix_is_declared_for_not_by_its_spelling()
    {
        const string Text = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:xaml="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:design="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:compat="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:d="using:Shop.Hints"
                    compat:Ignorable="design"
                    xaml:Class="Shop.MainWindow"
                    design:DataContext="{Binding Sample}"
                    Title="{Binding Title}">
                <TextBlock xaml:Name="Greeting" Text="{Binding Greeting}" design:Text="{Binding SampleGreeting}" d:Hint.Text="{Binding Hint}" />
            </Window>
            """;
        string[] read = [.. XamlBindings.In(TextSnapshot.From(Text)).Select(b => $"{b.Property}={Text[b.Range.Start..b.Range.End]} in {b.Class ?? "no class"}")];
        string[] expected = ["Title={Binding Title} in Shop.MainWindow", "Text={Binding Greeting} in no class", "d:Hint.Text={Binding Hint} in no class"];
        Assert.True(expected.SequenceEqual(read), $"The bindings read were {string.Join("; ", read)}: a designer's sample was asked of the running app "
            + "as a binding, or a binding of the app's own under a prefix spelled d went unshown");
    }
}
