using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>Windows' user Path changed a folder at a time: the folder put at its end (or first) where no entry names it,
/// as Windows reads an entry (its %names% expanded, quoted or not, either slash, a closing one or not, any letter case),
/// and taken off wherever it is, every other entry kept as written and where it was, an empty one too.</summary>
public class UserEnvironmentTests
{
    private const string Folder = @"C:\Users\ana\AppData\Local\PassengerIDE\current\bin";

    [Fact]
    public void A_folder_goes_at_the_end_of_the_path_every_entry_before_it_kept_as_written()
    {
        Assert.Equal($@"C:\Tools;;%USERPROFILE%\.dotnet\tools;{Folder}", UserEnvironment.With(@"C:\Tools;;%USERPROFILE%\.dotnet\tools", Folder, first: false));
        Assert.True(UserEnvironment.With(@"C:\Tools;", Folder, first: false) == $@"C:\Tools;{Folder};",
            "A Path ending in its separator took the folder after it, an empty entry the person never wrote, or lost it");
        Assert.Equal(Folder, UserEnvironment.With(null, Folder, first: false));
        Assert.Equal(Folder, UserEnvironment.With(string.Empty, Folder, first: false));
    }

    /// <summary>An uninstall leaves the Path as the install found it, character for character.</summary>
    [Fact]
    public void A_folder_put_on_and_taken_off_leaves_the_path_as_it_was()
    {
        foreach (string had in (string[])[@"C:\Tools", @"C:\Tools;", @"C:\Tools;;%USERPROFILE%\bin;", @";C:\Tools", @"C:\Tools;;"])
        {
            Assert.True(UserEnvironment.Without(UserEnvironment.With(had, Folder, first: false), Folder) == had,
                $"'{had}' came back as '{UserEnvironment.Without(UserEnvironment.With(had, Folder, first: false), Folder)}' once the folder was put on and taken off");
        }
    }

    [Fact]
    public void A_folder_the_path_names_already_however_written_is_not_added_again()
    {
        foreach (string named in (string[])[Folder, Folder.ToUpperInvariant(), Folder + @"\", $"\"{Folder}\"", Folder.Replace('\\', '/'), $" {Folder} "])
        {
            Assert.True(UserEnvironment.With($@"C:\Tools;{named};D:\bin", Folder, first: false) is null, $"The Path naming the folder as {named} was to be left as it was");
        }
    }

    [Fact]
    public void An_entry_naming_the_folder_through_a_variable_is_the_folder()
    {
        string? before = Environment.GetEnvironmentVariable("UNIT_TEST_LOCAL_APP_DATA");
        Environment.SetEnvironmentVariable("UNIT_TEST_LOCAL_APP_DATA", @"C:\Users\ana\AppData\Local");
        try
        {
            Assert.True(UserEnvironment.Names(@"%UNIT_TEST_LOCAL_APP_DATA%\PassengerIDE\current\bin", Folder),
                "An entry naming the folder through a variable was taken for another folder");
            Assert.Equal(@"C:\Tools", UserEnvironment.Without(@"C:\Tools;%UNIT_TEST_LOCAL_APP_DATA%\PassengerIDE\current\bin", Folder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNIT_TEST_LOCAL_APP_DATA", before);
        }
    }

    [Fact]
    public void A_folder_taken_off_leaves_every_other_entry_as_written_where_it_was()
    {
        Assert.Equal(@"C:\Tools;;%USERPROFILE%\bin;", UserEnvironment.Without($@"C:\Tools;;{Folder};%USERPROFILE%\bin;{Folder.ToLowerInvariant()}\;", Folder));
        Assert.Equal(string.Empty, UserEnvironment.Without(Folder, Folder));
        Assert.Null(UserEnvironment.Without(@"C:\Tools;D:\bin", Folder));
        Assert.Null(UserEnvironment.Without(null, Folder));
    }

    [Fact]
    public void A_folder_put_first_moves_from_where_it_was_and_one_first_already_is_left()
    {
        Assert.Equal($@"{Folder};C:\Tools;D:\bin", UserEnvironment.With($@"C:\Tools;{Folder};D:\bin", Folder, first: true));
        Assert.Equal($@"{Folder};C:\Tools", UserEnvironment.With(@"C:\Tools", Folder, first: true));
        Assert.Null(UserEnvironment.With($@"{Folder};C:\Tools", Folder, first: true));
        Assert.Equal(Folder, UserEnvironment.With(null, Folder, first: true));
    }

    [Fact]
    public void Put_and_take_change_the_variable_once_and_take_it_away_where_nothing_else_was_in_it()
    {
        Dictionary<string, string> variables = new() { [UserEnvironment.PathVariable] = @"C:\Tools;;%USERPROFILE%\bin" };
        UserEnvironment.StandIn = variables;
        try
        {
            Assert.True(UserEnvironment.Put(Folder, first: false));
            Assert.False(UserEnvironment.Put(Folder, first: false), "The folder went on the Path a second time");
            Assert.Equal($@"C:\Tools;;%USERPROFILE%\bin;{Folder}", variables[UserEnvironment.PathVariable]);
            Assert.True(UserEnvironment.Take(Folder));
            Assert.Equal(@"C:\Tools;;%USERPROFILE%\bin", variables[UserEnvironment.PathVariable]);
            Assert.False(UserEnvironment.Take(Folder));

            variables.Clear();
            Assert.True(UserEnvironment.Put(Folder, first: false));
            Assert.True(UserEnvironment.Take(Folder));
            Assert.False(variables.ContainsKey(UserEnvironment.PathVariable), $"A Path made for the folder alone was left as '{variables.GetValueOrDefault(UserEnvironment.PathVariable)}'");
        }
        finally
        {
            UserEnvironment.StandIn = null;
        }
    }
}
