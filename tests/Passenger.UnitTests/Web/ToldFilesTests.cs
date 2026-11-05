using Passenger.App.Debugging;

namespace Passenger.UnitTests.Web;

/// <summary>Which of a web app's files its pages have yet to be told of: one written, once for each writing, and one deleted,
/// which only a component's scoped CSS asks about: a sheet deleted is news once, whether or not it was ever told of, and put
/// back as it was it is news again.</summary>
public sealed class ToldFilesTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("passenger-unit-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_file_is_news_once_for_each_writing()
    {
        ToldFiles told = new();
        string sheet = Path.Combine(root, "site.css");
        File.WriteAllText(sheet, "a");

        Assert.True(told.Written(sheet), "A file written was passed over, so its page would keep the old one");
        Assert.False(told.Written(sheet), "A file told of and unchanged was told again");
        File.WriteAllText(sheet, "ab");
        Assert.True(told.Written(sheet), "A file written since it was told of was passed over");
    }

    [Fact]
    public void A_sheet_deleted_is_news_once_though_its_writing_never_was()
    {
        ToldFiles told = new();
        string sheet = Path.Combine(root, "Banner.razor.css");
        File.WriteAllText(sheet, ".banner { }");
        File.Delete(sheet);

        Assert.True(told.WrittenOrGone(sheet), "A scoped sheet deleted was passed over, so its old rules would stay in the running bundle");
        Assert.False(told.WrittenOrGone(sheet), "A sheet's deletion heard twice would bundle its project twice");
    }

    [Fact]
    public void A_sheet_put_back_as_it_was_is_news_again()
    {
        ToldFiles told = new();
        string sheet = Path.Combine(root, "Banner.razor.css");
        File.WriteAllText(sheet, ".banner { }");
        Assert.True(told.WrittenOrGone(sheet));
        DateTime written = File.GetLastWriteTimeUtc(sheet);
        File.Delete(sheet);
        Assert.True(told.WrittenOrGone(sheet));

        File.WriteAllText(sheet, ".banner { }");
        File.SetLastWriteTimeUtc(sheet, written);
        Assert.True(told.WrittenOrGone(sheet), "A sheet undeleted with its old stamp was passed over, so the bundle would stay without its rules");
    }

    [Fact]
    public void A_file_gone_is_no_writing()
    {
        ToldFiles told = new();
        Assert.False(told.Written(Path.Combine(root, "gone.png")), "A file that is not there was taken for one written, so its page would reload for nothing");
    }
}
