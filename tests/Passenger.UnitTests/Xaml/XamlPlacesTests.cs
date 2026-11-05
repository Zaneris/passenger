using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.UnitTests.Xaml;

/// <summary>Which of the files an app's build recorded its elements in is the XAML file the IDE asks of: the same file on the
/// disk, however reached, and never a file of its name in another folder; where the app was built elsewhere, so what it
/// recorded names nothing here, the path ending most alike.</summary>
public sealed class XamlPlacesTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"xaml-places-{Guid.NewGuid():N}")).FullName);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Written(string relative)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Window />");
        return path;
    }

    [Fact]
    public void Of_two_files_of_one_name_the_one_asked_of_is_the_one_at_its_path()
    {
        string first = Written("A/View.xaml");
        string second = Written("B/View.xaml");
        Assert.Equal(first, XamlPlaces.FileOf([second, first], first));
        Assert.Equal(second, XamlPlaces.FileOf([first, second], second));
    }

    [Fact]
    public void A_file_of_its_name_in_another_folder_is_never_the_one_asked_of()
    {
        string asked = Written("A/View.xaml");
        string other = Written("B/View.xaml");
        Assert.Null(XamlPlaces.FileOf([other], asked));
    }

    [Fact]
    public void A_file_of_another_name_is_never_the_one_asked_of()
    {
        Assert.Null(XamlPlaces.FileOf(["/elsewhere/Other.xaml"], "/project/View.xaml"));
    }

    [Fact]
    public void A_path_reaching_the_file_through_a_link_is_the_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A symbolic link needs a privilege Windows gives few");
        string asked = Written("real/Views/View.xaml");
        string link = Path.Combine(folder, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(folder, "real"));
        string recorded = Path.Combine(link, "Views", "View.xaml");
        Assert.Equal(recorded, XamlPlaces.FileOf([Written("other/View.xaml"), recorded], asked));
    }

    [Fact]
    public void Where_a_build_recorded_paths_of_another_machine_the_one_ending_most_alike_is_the_file()
    {
        string asked = Written("project/Views/A/View.xaml");
        Assert.Equal("/src/Views/A/View.xaml", XamlPlaces.FileOf(["/src/Views/B/View.xaml", "/src/Views/A/View.xaml"], asked));
    }

    [Fact]
    public void A_build_recording_its_file_by_a_resource_uri_is_known_by_how_the_path_ends()
    {
        string asked = Written("project/Views/View.axaml");
        Assert.Equal("avares://App/Views/View.axaml", XamlPlaces.FileOf(["avares://App/Other/View.axaml", "avares://App/Views/View.axaml"], asked));
    }

    [Fact]
    public void Asked_of_a_file_not_on_this_disk_the_path_ending_most_alike_is_the_file_though_it_is_here()
    {
        string recorded = Written("Views/A/View.xaml");
        Assert.Equal(recorded, XamlPlaces.FileOf([Written("Views/B/View.xaml"), recorded], "/Users/someone/app/Views/A/View.xaml"));
    }
}
