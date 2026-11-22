using Microsoft.CodeAnalysis.CSharp;
using Passenger.Core;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Projects;

namespace Passenger.UnitTests.Xaml;

/// <summary>A WPF XAML file whose x:Class no C# declares is a class of the project, as MarkupCompile makes it, where its
/// x:Class is a C# name as the compiler reads one.</summary>
public sealed class XamlOnlyClassesTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void An_x_class_no_csharp_declares_is_a_class_only_where_csharp_reads_it_as_a_name() => Cases.Each<(string, bool)>(
        [
            ("Gallery.Badge", true),
            ("Gallery.2Badge", false),
        ], row => An_x_class_no_csharp_declares_is_a_class_where_csharp_reads_it_as_a_name(row.Item1, row.Item2));

    private void An_x_class_no_csharp_declares_is_a_class_where_csharp_reads_it_as_a_name(string named, bool declared)
    {
        string project = Path.Combine(root, "Gallery", "Gallery.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");
        XamlProjectBuilder builder = XamlProjectBuilder.For(project, null, CSharpParseOptions.Default) ?? throw new InvalidOperationException("The project was not read as WPF's");
        string control = $"""
            <UserControl x:Class="{named}" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <TextBlock Text="New" />
            </UserControl>
            """;

        ClrType? found = builder.XamlChanged(Path.Combine(root, "Gallery", "Badge.xaml"), control).Types.Find(named);

        Assert.True((found is not null) == declared,
            declared ? $"{named} is no class of the project, so its x:Class and every use of it are marked" : $"{named}, no C# name, is a class of the project");
    }
}
