using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which new names an element's rename refuses: one another element of its name scope has (the file's own, or a
/// template's), and, for an element of the file's own scope, whose field the code-behind's class is given, the class's
/// own name and what it declares.</summary>
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
    public void A_name_another_element_of_the_files_scope_has_is_refused_naming_its_line()
    {
        Assert.Equal("'Title' names another element already, on line 3", XamlRefactorings.ElementNameProblem(File, "Go", "Title"));
    }

    [Fact]
    public void A_name_the_code_behinds_class_declares_or_is_is_refused_naming_what_has_it()
    {
        Assert.Equal("'OnGo' names a method of MainWindow already", XamlRefactorings.ElementNameProblem(File, "Go", "OnGo"));
        Assert.Equal("'Count' names a property of MainWindow already", XamlRefactorings.ElementNameProblem(File, "Go", "Count"));
        Assert.Equal("'MainWindow' names the code-behind's class itself", XamlRefactorings.ElementNameProblem(File, "Go", "MainWindow"));
    }

    [Fact]
    public void A_templates_names_are_a_scope_of_their_own_with_no_field_in_the_code_behind()
    {
        Assert.Equal("'Cell' names another element already, on line 10", XamlRefactorings.ElementNameProblem(File, "Row", "Cell"));
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Row", "Title"));
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Row", "OnGo"));
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Go", "Cell"));
    }

    [Fact]
    public void A_name_nothing_has_or_the_elements_own_is_not_refused()
    {
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Go", "Start"));
        Assert.Null(XamlRefactorings.ElementNameProblem(File, "Go", "Go"));
    }
}
