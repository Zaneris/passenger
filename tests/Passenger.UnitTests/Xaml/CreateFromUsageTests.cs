using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.UnitTests.Xaml;

/// <summary>Create from usage in XAML: the member a binding lacks made in the very type it binds, as its C# is written.</summary>
public class CreateFromUsageTests
{
    private const string ViewModels = """
        namespace A
        {
            public class MainViewModel
            {
                public string Title { get; set; }
            }
        }

        namespace B
        {
            public class MainViewModel
            {
                public string Title { get; set; }
            }
        }

        """;

    [Fact]
    public void A_property_a_binding_lacks_is_made_in_the_type_of_its_full_name_where_its_file_declares_another_of_the_same_name()
    {
        const string Window = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:vm="using:B" x:DataType="vm:MainViewModel">
                <TextBlock Text="{Binding Subtitle}" />
            </Window>
            """;
        XamlFile file = XamlFiles.Read("MainWindow.axaml", Window, AvaloniaDialect.Instance, ("MainViewModel.cs", ViewModels));
        ClrType owner = Assert.IsType<ClrType>(file.Types.Find("B.MainViewModel"));
        XmlAttribute text = file.Tree.Elements.Single(e => e.LocalName == "TextBlock").Attribute("Text")!;
        XamlFix fix = Assert.IsType<XamlFix>(CreateFromUsage.Property(file, owner, "Subtitle", text, last: true));
        Assert.Equal(XamlFiles.PathOf("MainViewModel.cs"), fix.Path);

        string made = XamlFiles.Applied(ViewModels, fix.Edits(ViewModels));
        string[] spaces = made.Split("namespace B", StringSplitOptions.None);
        Assert.True(spaces.Length == 2 && !spaces[0].Contains("Subtitle", StringComparison.Ordinal) && spaces[1].Contains("public object Subtitle { get; set; }", StringComparison.Ordinal),
            $"Create property for a binding of B.MainViewModel did not make it in B's class, the binding still finding nothing:\n{made}");
    }
}
