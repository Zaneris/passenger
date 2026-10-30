using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>What Git reads of git's answers and gives it, apart from any git: the working tree's top as rev-parse names
/// it, a space it ends with kept, a path as git takes it, a backslash a letter of its name off Windows, why git did not
/// start, and why its output could not be read.</summary>
public class GitTests
{
    [Fact]
    public void The_working_trees_top_and_gits_folder_keep_the_spaces_their_names_begin_or_end_with()
    {
        Assert.Equal(("/work/repo ", "/work/repo /.git"), Git.Places("/work/repo \n/work/repo /.git\n"));
        Assert.Equal(("/work/ repo", "/work/ repo/.git"), Git.Places("/work/ repo\n/work/ repo/.git\n"));
    }

    [Fact]
    public void A_path_reaches_git_with_slashes_and_a_backslash_is_a_letter_of_its_name_off_windows()
    {
        string root = Path.Combine(Path.GetTempPath(), "repo");
        Assert.Equal("src/Shop.cs", Git.RelativeIn(root, Path.Combine(root, "src", "Shop.cs")));
        Assert.Equal(OperatingSystem.IsWindows() ? "a/b.txt" : "a\\b.txt", Git.RelativeIn(root, Path.Combine(root, "a\\b.txt")));
    }

    [Fact]
    public void Git_is_said_not_installed_only_where_the_system_found_no_file_to_run()
    {
        GitResult missing = Git.StartFailure(new System.ComponentModel.Win32Exception(2, "No such file or directory"));
        Assert.Equal((127, "git is not installed: No such file or directory"), (missing.Code, missing.Message));
        GitResult tooLong = Git.StartFailure(new System.ComponentModel.Win32Exception(7, "Argument list too long"));
        Assert.Equal((126, "git could not be started: Argument list too long"), (tooLong.Code, tooLong.Message));
        GitResult unrunnable = Git.StartFailure(new System.ComponentModel.Win32Exception(13, "Permission denied"));
        Assert.Equal("git could not be started: Permission denied", unrunnable.Message);
    }

    [Fact]
    public void A_read_of_gits_output_that_failed_is_a_failure_in_the_systems_words_never_git_missing_nor_unstarted()
    {
        GitResult unread = Git.ReadFailure(new IOException("Broken pipe"));
        Assert.False(unread.Succeeded, "A call whose output could not be read was taken for git's success");
        Assert.Equal("git's output could not be read: Broken pipe", unread.Message);
        Assert.DoesNotContain(unread.Code, (int[])[126, 127, 128]);
    }

    [Fact]
    public void An_answer_of_other_than_two_lines_names_no_working_tree()
    {
        Assert.Null(Git.Places(string.Empty));
        Assert.Null(Git.Places("/work/repo\n"));
        Assert.Null(Git.Places("/work/repo\n\n"));
        Assert.Null(Git.Places("/work/repo\n/work/repo/.git\n/elsewhere\n"));
    }
}
