using Passenger.Xaml;
using Passenger.Xaml.Dialects;

namespace Passenger.UnitTests.Xaml;

/// <summary>The URI a project's build names one of its XAML files by, which Hot Reload and the previewers give the
/// file's text to resolve its relative URIs against: escaped, so a folder named C# is no fragment; a file linked in
/// from outside the project's folder named as its build names it, in the project's own assembly; a MAUI file
/// none.</summary>
public class XamlUrisTests
{
    [Fact]
    public void A_xaml_files_uri_is_the_one_its_build_names_it_by_escaped_and_read_back_to_the_file() => Cases.All(
        ("a wpf page in a folder named c sharp is named by a pack uri whose folder is no fragment", A_wpf_page_in_a_folder_named_c_sharp_is_named_by_a_pack_uri_whose_folder_is_no_fragment),
        ("a file linked in from outside the project keeps the projects assembly in its uri", A_file_linked_in_from_outside_the_project_keeps_the_projects_assembly_in_its_uri),
        ("a maui file has no uri its build names it by", A_maui_file_has_no_uri_its_build_names_it_by));

    private void A_wpf_page_in_a_folder_named_c_sharp_is_named_by_a_pack_uri_whose_folder_is_no_fragment()
    {
        XamlProject project = XamlFiles.Project(WpfDialect.Instance);
        string page = XamlFiles.PathOf(Path.Combine("Views", "C#", "Page.xaml"));

        string? uri = project.UriOf(page);

        Assert.Equal("pack://application:,,,/App;component/Views/C%23/Page.xaml", uri);
        Assert.True(project.ResolveSource(uri!, page) == page, $"{uri} reads back as {project.ResolveSource(uri!, page)}, where the page is {page}");
    }

    private void A_file_linked_in_from_outside_the_project_keeps_the_projects_assembly_in_its_uri()
    {
        XamlProject project = XamlFiles.Project(WpfDialect.Instance);
        string linked = Path.GetFullPath(Path.Combine(XamlFiles.Folder, "..", "Shared", "Theme.xaml"));

        Assert.Equal("pack://application:,,,/App;component/Theme.xaml", project.UriOf(linked));
    }

    private void A_maui_file_has_no_uri_its_build_names_it_by()
    {
        Assert.Null(XamlFiles.Project(MauiDialect.Instance).UriOf(XamlFiles.PathOf("MainPage.xaml")));
    }
}
