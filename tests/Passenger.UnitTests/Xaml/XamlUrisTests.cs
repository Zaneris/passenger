using Passenger.Xaml;
using Passenger.Xaml.Dialects;

namespace Passenger.UnitTests.Xaml;

/// <summary>The URI a project's build names one of its XAML files by, which Hot Reload and the previewers give the file's
/// text to resolve its relative URIs against: escaped, so a folder named C# is no fragment; a file linked in from outside
/// the project's folder named as its build names it, in the project's own assembly; read back by the project to the file
/// it names.</summary>
public class XamlUrisTests
{
    [Fact]
    public void A_wpf_page_in_a_folder_named_c_sharp_is_named_by_a_pack_uri_whose_folder_is_no_fragment()
    {
        XamlProject project = XamlFiles.Project(WpfDialect.Instance);
        string page = XamlFiles.PathOf(Path.Combine("Views", "C#", "Page.xaml"));

        string? uri = project.UriOf(page);

        Assert.Equal("pack://application:,,,/App;component/Views/C%23/Page.xaml", uri);
        Assert.True(project.ResolveSource(uri!, page) == page, $"{uri} reads back as {project.ResolveSource(uri!, page)}, where the page is {page}");
    }

    [Fact]
    public void An_avalonia_view_in_a_folder_named_c_sharp_resolves_what_is_beside_it_in_that_folder()
    {
        XamlProject project = XamlFiles.Project(AvaloniaDialect.Instance);
        string view = XamlFiles.PathOf(Path.Combine("Views", "C#", "Card.axaml"));

        string? uri = project.UriOf(view);

        Assert.Equal("avares://App/Views/C%23/Card.axaml", uri);
        Assert.True(project.ResolveSource(uri!, view) == view, $"{uri} reads back as {project.ResolveSource(uri!, view)}, where the view is {view}");
        Uri beside = new(new Uri(uri!), "Palette.axaml");
        Assert.True(beside.AbsolutePath == "/Views/C%23/Palette.axaml", $"Palette.axaml beside the card resolves to {beside}, outside the card's folder");
    }

    [Fact]
    public void A_file_linked_in_from_outside_the_project_keeps_the_projects_assembly_in_its_uri()
    {
        XamlProject project = XamlFiles.Project(WpfDialect.Instance);
        string linked = Path.GetFullPath(Path.Combine(XamlFiles.Folder, "..", "Shared", "Theme.xaml"));

        Assert.Equal("pack://application:,,,/App;component/Theme.xaml", project.UriOf(linked));
    }

    [Fact]
    public void A_path_in_the_assembly_escapes_each_part_as_a_uri_writes_it()
    {
        Assert.Equal("My%20Views/Fen%C3%AAtre.xaml", XamlProject.PathIn(XamlFiles.Folder, XamlFiles.PathOf(Path.Combine("My Views", "Fenêtre.xaml"))));
        Assert.Equal("Page.xaml", XamlProject.PathIn(XamlFiles.Folder, XamlFiles.PathOf("Page.xaml")));
    }

    [Fact]
    public void A_maui_file_has_no_uri_its_build_names_it_by()
    {
        Assert.Null(XamlFiles.Project(MauiDialect.Instance).UriOf(XamlFiles.PathOf("MainPage.xaml")));
    }
}
