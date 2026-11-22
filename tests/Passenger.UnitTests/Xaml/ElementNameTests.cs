using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which new names an element's rename refuses: one another element of the file's name scope has, and, for an
/// element whose field the code-behind's class is given, the class's own name and what it declares.</summary>
public sealed class ElementNameTests
{
    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.MainWindow">
            <StackPanel>
                <TextBlock Name="Title" />
                <Button Name="Go" Click="OnGo" />
                <ItemsControl>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <StackPanel>
                                <TextBlock x:Name="Row" />
                                <TextBlock x:Name="Cell" />
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </Window>
        """;

    private const string CodeBehind = """
        namespace App;

        public partial class MainWindow
        {
            public MainWindow()
            {
            }

            public int Count { get; set; }

            private void OnGo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            {
            }
        }
        """;

    private static XamlFile File => XamlFiles.Read("MainWindow.axaml", Window, AvaloniaDialect.Instance, ("MainWindow.axaml.cs", CodeBehind));

    [Fact]
    public void An_elements_rename_refuses_a_name_its_scope_or_its_class_already_has() => Cases.All(
        ("a name another element of the files scope has is refused naming its line", A_name_another_element_of_the_files_scope_has_is_refused_naming_its_line),
        ("a name the code behinds class declares or is is refused naming what has it", A_name_the_code_behinds_class_declares_or_is_is_refused_naming_what_has_it),
        ("a name nothing has or the elements own is not refused", A_name_nothing_has_or_the_elements_own_is_not_refused));

    private void A_name_another_element_of_the_files_scope_has_is_refused_naming_its_line()
    {
        Assert.Equal("'Title' names another element already, on line 3", XamlRefactorings.ElementNameProblem(File, "Go", "Title"));
    }

    private void A_name_the_code_behinds_class_declares_or_is_is_refused_naming_what_has_it()
    {
        Assert.Equal("'OnGo' names a method of MainWindow already", XamlRefactorings.ElementNameProblem(File, "Go", "OnGo"));
        Assert.Equal("'Count' names a property of MainWindow already", XamlRefactorings.ElementNameProblem(File, "Go", "Count"));
        Assert.Equal("'MainWindow' names the code-behind's class itself", XamlRefactorings.ElementNameProblem(File, "Go", "MainWindow"));
    }

    private void A_name_nothing_has_or_the_elements_own_is_not_refused()
    {
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Go", "Start"));
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Go", "Go"));
    }
}
