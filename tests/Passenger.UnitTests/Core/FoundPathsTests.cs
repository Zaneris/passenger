using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Which paths read out of a program's or a file's text the disk may be asked about, told from the text alone: a
/// path on this machine's own volumes, never a share's, a device's or one under an automount root, however it is written,
/// unless it lies inside a folder the IDE works in already.</summary>
public class FoundPathsTests
{
    private static readonly string Here = OperatingSystem.IsWindows() ? @"C:\work\Shop\Program.cs" : "/work/Shop/Program.cs";

    [Fact]
    public void A_path_on_this_machine_may_be_asked_whether_whole_relative_or_climbing_by_dots()
    {
        foreach (string path in (string[])[Here, "Program.cs", Path.Combine("src", "..", "Program.cs"), OperatingSystem.IsWindows() ? "C:/work/x.cs" : "/work/../x.cs", "/netbox/x.cs"])
        {
            Assert.True(FoundPaths.WhyNotAsked(path) is null, $"{path} is refused as {FoundPaths.WhyNotAsked(path)}, so its file is never linked");
            Assert.True(FoundPaths.Askable(path), $"{path} may not be asked");
        }
    }

    /// <summary>Two slashes of either kind begin a share's path on Windows, and are refused everywhere, as a share's path
    /// read from a Windows program's output; its dots never climb above its share.</summary>
    [Fact]
    public void A_shares_path_by_either_slash_is_never_asked()
    {
        foreach (string path in (string[])[@"\\otherhost\share\a.cs", "//otherhost/share/a.cs", @"\/otherhost/share/a.cs", @"/\otherhost\share\a.cs",
            @"\\otherhost\share\..\..\a.cs", @"\\\otherhost\share\a.cs", @"\\otherhost.\share\a.cs"])
        {
            Assert.Equal(FoundPathRefusal.Share, FoundPaths.WhyNotAsked(path));
            Assert.False(FoundPaths.Askable(path), $"{path} may be asked, which reaches its host");
        }
    }

    [Fact]
    public void A_devices_path_is_never_asked()
    {
        List<string> devices = [@"\\?\C:\work\a.cs", @"\\.\pipe\x", "//?/C:/work/a.cs", "//./COM1", @"\\?\UNC\otherhost\share\a.cs"];
        if (OperatingSystem.IsWindows())
        {
            // NT's own namespace, and a legacy device's name, which Windows makes \\.\NUL.
            devices.AddRange([@"\??\UNC\otherhost\share\a.cs", @"\??\C:\work\a.cs", "NUL", "COM1"]);
        }
        foreach (string path in devices)
        {
            Assert.Equal(FoundPathRefusal.Device, FoundPaths.WhyNotAsked(path));
            Assert.False(FoundPaths.Askable(path, [Path.GetPathRoot(Here)!]), $"{path} may be asked, which opens its device");
        }
    }

    /// <summary>Naming a path under /net (or macOS's /Network) mounts that host's exports: refused in the letter case its
    /// volume takes, and once its dots are resolved; a name that only begins the same is no such root.</summary>
    [Fact]
    public void A_path_under_an_automount_root_is_never_asked_off_Windows()
    {
        string[] automounted = ["/net/otherhost/share/a.cs", "/net", "/work/../net/otherhost/a.cs", "/./net/otherhost/a.cs"];
        foreach (string path in automounted)
        {
            Assert.Equal(OperatingSystem.IsWindows() ? null : FoundPathRefusal.Automounted, FoundPaths.WhyNotAsked(path));
        }
        Assert.Equal(OperatingSystem.IsMacOS() ? FoundPathRefusal.Automounted : null, FoundPaths.WhyNotAsked("/Network/Servers/otherhost/a.cs"));
        Assert.Equal(OperatingSystem.IsMacOS() ? FoundPathRefusal.Automounted : null, FoundPaths.WhyNotAsked("/NET/otherhost/a.cs"));
        Assert.Null(FoundPaths.WhyNotAsked("/network-tools/a.cs"));
    }

    [Fact]
    public void An_empty_path_or_one_holding_a_nul_is_never_asked()
    {
        Assert.Equal(FoundPathRefusal.Malformed, FoundPaths.WhyNotAsked(string.Empty));
        Assert.Equal(FoundPathRefusal.Malformed, FoundPaths.WhyNotAsked("a\0b.cs"));
    }

    /// <summary>A folder opened on a share (or an automounted one) is the IDE's already: a path inside it may be asked,
    /// its dots resolved first, so none climbs out of it to the share beside it or another host.</summary>
    [Fact]
    public void A_shares_path_inside_a_folder_the_IDE_works_in_may_be_asked_and_none_outside_it()
    {
        string share = OperatingSystem.IsWindows() ? @"\\fileserver\code\Shop" : "/net/fileserver/code/Shop";
        string inside = Path.Combine(share, "src", "a.cs");
        Assert.True(FoundPaths.Askable(inside, [share]), $"{inside} inside {share} may not be asked");
        foreach (string outside in (string[])[Path.Combine(share, "..", "..", "other", "a.cs"), share + "Copy" + Path.DirectorySeparatorChar + "a.cs",
            OperatingSystem.IsWindows() ? @"\\otherhost\code\Shop\a.cs" : "/net/otherhost/code/Shop/a.cs"])
        {
            Assert.False(FoundPaths.Askable(outside, [share]), $"{outside} may be asked, though it lies outside {share}");
        }
    }

    /// <summary>A folder held as one the IDE works in (a window's open folder) lets its paths be asked while held, and no
    /// longer once let go.</summary>
    [Fact]
    public void A_folder_held_as_reached_lets_its_paths_be_asked_until_let_go()
    {
        string share = OperatingSystem.IsWindows() ? @"\\heldserver\code\Held" : "/net/heldserver/code/Held";
        string inside = Path.Combine(share, "a.cs");
        Assert.False(FoundPaths.Askable(inside));
        IDisposable held = FoundPaths.Reach(share);
        IDisposable again = FoundPaths.Reach(share);
        Assert.True(FoundPaths.Askable(inside), $"{inside} may not be asked while its folder is held");
        held.Dispose();
        held.Dispose();
        Assert.True(FoundPaths.Askable(inside), $"{inside} may not be asked while a second window holds its folder");
        again.Dispose();
        Assert.False(FoundPaths.Askable(inside), $"{inside} may be asked after its folder was let go");
    }

    /// <summary>A file address names a path here only with no host or localhost, its path as any found path's however
    /// escaped: another host's, a share's by four slashes or escaped slashes, a device's or an automounted one is none.</summary>
    [Fact]
    public void A_file_address_names_a_path_here_only_without_another_host_and_never_a_shares()
    {
        string here = OperatingSystem.IsWindows() ? @"C:\work\x.txt" : "/work/x.txt";
        Assert.Equal(here, FoundPaths.OfFileAddress(new Uri(OperatingSystem.IsWindows() ? "file:///C:/work/x.txt" : "file:///work/x.txt")));
        Assert.Equal(here, FoundPaths.OfFileAddress(new Uri(OperatingSystem.IsWindows() ? "file://localhost/C:/work/x.txt" : "file://localhost/work/x.txt")));
        Assert.Equal(OperatingSystem.IsWindows() ? @"C:\work\a b.txt" : "/work/a b.txt",
            FoundPaths.OfFileAddress(new Uri(OperatingSystem.IsWindows() ? "file:///C:/work/a%20b.txt" : "file:///work/a%20b.txt")));
        foreach (string none in (string[])["file://otherhost/share/x", "file:////otherhost/share/y", "file:///%5C%5Cotherhost%5Cshare%5Cz", "file:///%2F%2Fotherhost/z",
            "file://127.0.0.2/x", "file:///%5C%5C%3F%5CC:%5Cx", "file:///%5C%5C.%5Cpipe%5Cx", "file:///net/otherhost/x", "https://example.com/x"])
        {
            string? named = Uri.TryCreate(none, UriKind.Absolute, out Uri? address) ? FoundPaths.OfFileAddress(address) : null;
            Assert.True(named is null, $"{none} names {named} here, which asking reaches another machine");
        }
    }
}
