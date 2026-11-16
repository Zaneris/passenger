using System.Text;
using Passenger.App.Editors;
using Passenger.Core;

namespace Passenger.UnitTests.Editor;

/// <summary>What an HTML preview's page and its folder are served as where the web view reads no folder: the page at its
/// name with the editor's text; each file below its folder as it is on disk in its media type, but for one whose name or a
/// folder's on its way begins with a dot and one .gitignore leaves out, each refused saying why; and nothing else on the
/// disk, however an address is spelled and whatever a link inside the folder leads to.</summary>
public sealed class ServedPageTests : IDisposable
{
    private const string Html = "<!doctype html>\n<h1>Grüße</h1>\n";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Folder => Path.Join(root, "site");

    private string Index => Path.Join(Folder, "index.html");

    [Fact]
    public void The_page_is_served_at_its_files_name_with_the_editors_text_not_the_disks()
    {
        Write(Index, "<h1>As saved</h1>\n");
        ServedPage served = new(Index, Html);

        Assert.Equal($"https://{ServedPage.Host}/index.html", served.Address.AbsoluteUri);
        PageAnswer answer = served.Answer(served.Address);
        Assert.Equal(200, answer.Status);
        Assert.Equal("text/html; charset=utf-8", answer.Type);
        Assert.Equal(Html, Read(answer));
    }

    [Fact]
    public void A_page_never_saved_is_served_with_nothing_on_the_disk()
    {
        Directory.CreateDirectory(Folder);
        ServedPage served = new(Index, Html);
        Assert.Equal(Html, Read(served.Answer(served.Address)));
    }

    [Theory]
    [InlineData("my page.html")]
    [InlineData("été #1.html")]
    [InlineData("50% off?.html")]
    public void A_page_of_any_name_is_found_at_its_own_address(string name)
    {
        if (OperatingSystem.IsWindows() && name.Contains('?', StringComparison.Ordinal))
        {
            Assert.Skip("Windows names no file with a question mark");
        }
        string page = Write(Path.Join(Folder, name), "<h1>As saved</h1>\n");
        ServedPage served = new(page, Html);
        Assert.Equal(Html, Read(served.Answer(served.Address)));
        Assert.Equal(page, served.FileOf(served.Address));
    }

    [Fact]
    public void A_file_beside_or_below_the_page_is_served_from_the_disk_in_its_media_type()
    {
        string css = Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        string picture = Write(Path.Join(Folder, "img", "a dot.png"), "not really a picture");
        ServedPage served = new(Index, Html);

        PageAnswer style = served.Answer(At("site.css"));
        Assert.Equal((200, css, "text/css"), (style.Status, style.File, style.Type));
        Assert.Equal("h1 { color: green; }\n", Read(style));

        PageAnswer image = served.Answer(At("img/a%20dot.png"));
        Assert.Equal((200, picture, "image/png"), (image.Status, image.File, image.Type));
        Assert.Equal("not really a picture", Read(image));

        Assert.Equal(css, served.Answer(At("site.css?v=2")).File);
    }

    [Fact]
    public void A_page_in_a_subfolder_is_served_only_what_its_own_folder_holds()
    {
        string about = Write(Path.Join(Folder, "pages", "about.html"), "<h1>As saved</h1>\n");
        Write(Path.Join(Folder, "css", "site.css"), "h1 { color: green; }\n");
        string beside = Write(Path.Join(Folder, "pages", "dot.png"), "dot");
        string below = Write(Path.Join(Folder, "pages", "img", "dot.png"), "dot");
        ServedPage served = new(about, Html);

        Assert.Equal($"https://{ServedPage.Host}/about.html", served.Address.AbsoluteUri);
        Assert.Equal(Html, Read(served.Answer(served.Address)));
        Assert.Equal(beside, served.Answer(new Uri(served.Address, "dot.png")).File);
        Assert.Equal(below, served.Answer(new Uri(served.Address, "img/dot.png")).File);
        foreach (string climbing in (string[])["../css/site.css", "/css/site.css", "..%2Fcss%2Fsite.css"])
        {
            PageAnswer answer = served.Answer(new Uri(served.Address, climbing));
            Assert.True(answer == PageAnswer.Missing, $"The address {climbing} was answered {answer.Status} with {answer.File}, beside the page's folder");
        }
    }

    [Fact]
    public void A_page_below_folders_of_any_name_is_found_at_its_own_name()
    {
        string page = Write(Path.Join(Folder, "my pages", "été #1", "index.html"), "<h1>As saved</h1>\n");
        ServedPage served = new(page, Html);

        Assert.Equal($"https://{ServedPage.Host}/index.html", served.Address.AbsoluteUri);
        Assert.Equal(Html, Read(served.Answer(served.Address)));
    }

    [Theory]
    [InlineData(".env", ".env as its name begins with a dot")]
    [InlineData(".git/config", ".git/config as it is in .git, whose name begins with a dot")]
    [InlineData("assets/.secret/key.json", "assets/.secret/key.json as it is in assets/.secret, whose name begins with a dot")]
    [InlineData("%2Eenv", ".env as its name begins with a dot")]
    public void A_file_whose_name_or_folder_begins_with_a_dot_is_refused_saying_why(string below, string said)
    {
        Write(Path.Join(Folder, below.Replace("%2E", ".", StringComparison.Ordinal)), "SECRET=1\n");
        ServedPage served = new(Index, Html);
        List<string> refused = [];
        served.Refused += (_, why) => refused.Add(why);

        PageAnswer answer = served.Answer(At(below));
        Assert.True(answer == PageAnswer.Forbidden && answer.Open() is null, $"The address {below} was answered {answer.Status} with {answer.File}");
        Assert.Equal((403, "Forbidden"), (answer.Status, answer.Reason));
        Assert.Equal([said], refused);
    }

    [Fact]
    public void A_file_or_folder_gitignore_leaves_out_is_refused_saying_why()
    {
        string secrets = Write(Path.Join(Folder, "secrets.json"), "{}\n");
        string built = Path.Join(Folder, "bin");
        Write(Path.Join(built, "Debug", "app.js"), "let a;\n");
        string css = Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        ServedPage served = new(Index, Html, [secrets, built]);
        List<string> refused = [];
        served.Refused += (_, why) => refused.Add(why);

        Assert.True(served.Answer(At("secrets.json")) == PageAnswer.Forbidden, "A file .gitignore leaves out was served");
        Assert.True(served.Answer(At("bin/Debug/app.js")) == PageAnswer.Forbidden, "A file in a folder .gitignore leaves out was served");
        Assert.Equal(css, served.Answer(At("site.css")).File);
        Assert.Equal(["secrets.json as .gitignore leaves it out", "bin/Debug/app.js as .gitignore leaves it out"], refused);
    }

    [Fact]
    public void A_page_refused_nothing_raises_no_refusal()
    {
        Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        ServedPage served = new(Index, Html);
        List<string> refused = [];
        served.Refused += (_, why) => refused.Add(why);
        served.Answer(served.Address);
        served.Answer(At("site.css"));
        served.Answer(At("nothing.css"));
        served.Answer(At("../outside.css"));
        Assert.Empty(refused);
    }

    [Fact]
    public void A_file_changed_on_disk_is_served_as_it_is_now()
    {
        string css = Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        ServedPage served = new(Index, Html);
        Assert.Equal("h1 { color: green; }\n", Read(served.Answer(At("site.css"))));

        File.WriteAllText(css, "h1 { color: teal; }\n");
        Assert.Equal("h1 { color: teal; }\n", Read(served.Answer(At("site.css"))));
    }

    [Theory]
    [InlineData("../outside.css")]
    [InlineData("..%2Foutside.css")]
    [InlineData("..%5Coutside.css")]
    [InlineData("%2E%2E/outside.css")]
    [InlineData("%2e%2e%2foutside.css")]
    [InlineData("img/..%2F..%2Foutside.css")]
    [InlineData("sub/../../outside.css")]
    public void A_file_beside_the_folder_is_not_served_however_its_address_is_spelled(string below)
    {
        Write(Path.Join(root, "outside.css"), "h1 { color: red; }\n");
        Write(Path.Join(Folder, "img", "dot.png"), "dot");
        ServedPage served = new(Index, Html);

        // Off Windows a backslash is a name's own letter, so ..\outside.css names a file of the folder whose name begins with a dot.
        PageAnswer answer = served.Answer(At(below));
        Assert.True(answer == PageAnswer.Missing || answer == PageAnswer.Forbidden, $"The address {below} was answered {answer.Status} with {answer.File}, outside the page's folder");
        Assert.Null(answer.Open());
    }

    [Fact]
    public void A_link_inside_the_folder_leading_out_of_it_is_not_followed()
    {
        string outside = Write(Path.Join(root, "outside.css"), "h1 { color: red; }\n");
        string below = Write(Path.Join(root, "elsewhere", "secret.txt"), "secret\n");
        Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        Link(() => File.CreateSymbolicLink(Path.Join(Folder, "linked.css"), outside));
        Link(() => Directory.CreateSymbolicLink(Path.Join(Folder, "linked"), Path.GetDirectoryName(below)!));
        ServedPage served = new(Index, Html);

        Assert.True(served.Answer(At("linked.css")) == PageAnswer.Missing, "A file link leading out of the page's folder was served");
        Assert.True(served.Answer(At("linked/secret.txt")) == PageAnswer.Missing, "A folder link leading out of the page's folder was served");
    }

    [Fact]
    public void A_link_staying_inside_the_folder_is_followed()
    {
        string css = Write(Path.Join(Folder, "theme", "site.css"), "h1 { color: green; }\n");
        Link(() => File.CreateSymbolicLink(Path.Join(Folder, "site.css"), css));
        ServedPage served = new(Index, Html);
        Assert.Equal("h1 { color: green; }\n", Read(served.Answer(At("site.css"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("img/")]
    [InlineData("nothing.css")]
    [InlineData("img/nothing.png")]
    public void An_address_of_no_file_answers_that_nothing_is_there(string below)
    {
        Write(Path.Join(Folder, "img", "dot.png"), "dot");
        ServedPage served = new(Index, Html);

        PageAnswer answer = served.Answer(At(below));
        Assert.Equal((404, "Not Found"), (answer.Status, answer.Reason));
        Assert.Null(answer.Open());
    }

    [Theory]
    [InlineData("https://example.com/index.html")]
    [InlineData("http://preview.passenger.invalid/index.html")]
    [InlineData("https://preview.passenger.invalid:8443/index.html")]
    [InlineData("https://preview.passenger.invalid.example.com/index.html")]
    [InlineData("file:///index.html")]
    public void An_address_of_anywhere_else_names_no_file(string address)
    {
        Write(Index, Html);
        ServedPage served = new(Index, Html);
        Assert.Null(served.FileOf(new Uri(address)));
        Assert.True(served.Answer(new Uri(address)) == PageAnswer.Missing, $"The address {address} was served as the page's own");
    }

    [Fact]
    public void A_link_to_a_file_not_there_yet_names_it_for_the_editor()
    {
        Directory.CreateDirectory(Folder);
        ServedPage served = new(Index, Html);
        Assert.Equal(Path.Join(Folder, "docs", "next page.html"), served.FileOf(At("docs/next%20page.html")));
    }

    [Fact]
    public void A_file_being_served_can_be_written_moved_or_deleted_meanwhile()
    {
        string css = Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        string moved = Path.Join(Folder, "moved.css");
        ServedPage served = new(Index, Html);

        using Stream reading = served.Answer(At("site.css")).Open()!;
        File.WriteAllText(css, "h1 { color: teal; }\n");
        File.Move(css, moved);
        Assert.Equal("h1 { color: teal; }\n", File.ReadAllText(moved));
        File.Delete(moved);
        Assert.False(File.Exists(moved), "A file the preview was reading could not be deleted");
    }

    [Fact]
    public void Every_answer_says_it_is_not_to_be_kept()
    {
        Write(Path.Join(Folder, "site.css"), "h1 { color: green; }\n");
        ServedPage served = new(Index, Html);
        Assert.Equal("Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store", served.Answer(served.Address).Headers);
        Assert.Equal("Content-Type: text/css\r\nCache-Control: no-store", served.Answer(At("site.css")).Headers);
        Assert.Equal("Cache-Control: no-store", PageAnswer.Missing.Headers);
    }

    [Theory]
    [InlineData("a.CSS", "text/css")]
    [InlineData("a.mjs", "text/javascript")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.woff2", "font/woff2")]
    [InlineData("a.html", "text/html")]
    [InlineData("a.unheard", null)]
    [InlineData("a", null)]
    public void A_files_media_type_comes_from_its_extension_or_is_left_to_the_web_view(string file, string? type) =>
        Assert.Equal(type, ServedPage.TypeOf(file));

    /// <summary>The address <paramref name="below"/> names, spelled as the page's own would be.</summary>
    private static Uri At(string below) => new($"https://{ServedPage.Host}/{below}");

    private static string Read(PageAnswer answer)
    {
        using Stream bytes = answer.Open() ?? throw new Xunit.Sdk.XunitException($"The answer {answer.Status} has no bytes");
        using StreamReader reader = new(bytes, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Makes a link, the test skipped where the system lets this user make none (Windows without Developer Mode).</summary>
    private static void Link(Func<FileSystemInfo> make)
    {
        try
        {
            make();
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
    }
}
