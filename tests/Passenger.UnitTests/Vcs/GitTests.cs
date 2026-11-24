using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>What Git reads of git's answers and gives it, apart from any git: the working tree's top as rev-parse names
/// it, a space it ends with kept, a path as git takes it, a backslash a letter of its name off Windows, why git did not
/// start, why its output could not be read, a tag's name, a tag and a tag's push as git takes and writes them, and
/// worktrees as git lists them.</summary>
public class GitTests
{
    [Fact]
    public void Gits_answers_and_failures_are_read_as_git_means_them_and_a_path_given_as_git_takes_it() => Cases.All(
        ("the working trees top and gits folder keep the spaces their names begin or end with", The_working_trees_top_and_gits_folder_keep_the_spaces_their_names_begin_or_end_with),
        ("an answer of other than two lines names no working tree", An_answer_of_other_than_two_lines_names_no_working_tree),
        ("a path reaches git with slashes and a backslash is a letter of its name off windows", A_path_reaches_git_with_slashes_and_a_backslash_is_a_letter_of_its_name_off_windows),
        ("git is said not installed only where the system found no file to run", Git_is_said_not_installed_only_where_the_system_found_no_file_to_run),
        ("a read of gits output that failed is a failure in the systems words never git missing nor unstarted", A_read_of_gits_output_that_failed_is_a_failure_in_the_systems_words_never_git_missing_nor_unstarted),
        ("a tags name is refused where git refuses it and a tag and a push its remote refused are read as git writes them", A_tags_name_is_refused_where_git_refuses_it_and_a_tag_and_a_push_its_remote_refused_are_read_as_git_writes_them),
        ("worktrees are read as git lists them and a new ones folder goes beside the main one named for its branch", Worktrees_are_read_as_git_lists_them_and_a_new_ones_folder_goes_beside_the_main_one_named_for_its_branch));

    private void The_working_trees_top_and_gits_folder_keep_the_spaces_their_names_begin_or_end_with()
    {
        Assert.Equal(("/work/repo ", "/work/repo /.git"), Git.Places("/work/repo \n/work/repo /.git\n"));
        Assert.Equal(("/work/ repo", "/work/ repo/.git"), Git.Places("/work/ repo\n/work/ repo/.git\n"));
    }

    private void A_path_reaches_git_with_slashes_and_a_backslash_is_a_letter_of_its_name_off_windows()
    {
        string root = Path.Combine(Path.GetTempPath(), "repo");
        Assert.Equal("src/Shop.cs", Git.RelativeIn(root, Path.Combine(root, "src", "Shop.cs")));
        Assert.Equal(OperatingSystem.IsWindows() ? "a/b.txt" : "a\\b.txt", Git.RelativeIn(root, Path.Combine(root, "a\\b.txt")));
    }

    private void Git_is_said_not_installed_only_where_the_system_found_no_file_to_run()
    {
        GitResult missing = Git.StartFailure(new System.ComponentModel.Win32Exception(2, "No such file or directory"));
        Assert.Equal((127, "git is not installed: No such file or directory"), (missing.Code, missing.Message));
        GitResult tooLong = Git.StartFailure(new System.ComponentModel.Win32Exception(7, "Argument list too long"));
        Assert.Equal((126, "git could not be started: Argument list too long"), (tooLong.Code, tooLong.Message));
        GitResult unrunnable = Git.StartFailure(new System.ComponentModel.Win32Exception(13, "Permission denied"));
        Assert.Equal("git could not be started: Permission denied", unrunnable.Message);
    }

    private void A_read_of_gits_output_that_failed_is_a_failure_in_the_systems_words_never_git_missing_nor_unstarted()
    {
        GitResult unread = Git.ReadFailure(new IOException("Broken pipe"));
        Assert.False(unread.Succeeded, "A call whose output could not be read was taken for git's success");
        Assert.Equal("git's output could not be read: Broken pipe", unread.Message);
        Assert.DoesNotContain(unread.Code, (int[])[126, 127, 128]);
    }

    private void An_answer_of_other_than_two_lines_names_no_working_tree()
    {
        Assert.Null(Git.Places(string.Empty));
        Assert.Null(Git.Places("/work/repo\n"));
        Assert.Null(Git.Places("/work/repo\n\n"));
        Assert.Null(Git.Places("/work/repo\n/work/repo/.git\n/elsewhere\n"));
    }

    /// <summary>Names as git check-ref-format and git tag take them (each refusal's checked against git 2.54's), a tag
    /// read from for-each-ref, and a push --porcelain's refusal for the remote's own tag told from its other answers.</summary>
    private void A_tags_name_is_refused_where_git_refuses_it_and_a_tag_and_a_push_its_remote_refused_are_read_as_git_writes_them()
    {
        foreach (string taken in (string[])["v1.0", "release/1.0", "a@b", "@", "ü-1"])
        {
            Assert.True(RefNames.WhyNot(taken, "tag") is null, $"{taken}, a name git tags by, was refused: {RefNames.WhyNot(taken, "tag")}");
        }
        foreach (string refused in (string[])["", "-x", "a b", "a~1", "a^", "a:b", "a?", "a*", "a[", "a\\b", "a..b", "a@{b", "/a", "a/", "a//b", "a.", ".a", "a/.b", "a.lock", "a/b.lock/c", "a\tb"])
        {
            Assert.True(RefNames.WhyNot(refused, "tag") is not null, $"{refused}, a name git refuses to tag by, was taken");
        }
        string listed = "refs/tags/v1\u001fcommit\u001fabc\u001f\u001f\u001f\u001f\u001e\nrefs/tags/v2\u001ftag\u001ftagobj\u001fdef\u001fAda\u001f-----BEGIN SSH SIGNATURE-----\nU1N\n-----END SSH SIGNATURE-----\n\u001fRelease two\n\nNotes\n-----BEGIN SSH SIGNATURE-----\nU1N\n-----END SSH SIGNATURE-----\n\u001e\n";
        Assert.Equal(new TagInfo("v1", "abc"), TagNames.Read(listed, "refs/tags/v1"));
        Assert.Equal(new TagInfo("v2", "def", "Release two\n\nNotes", "Ada", Signed: true), TagNames.Read(listed, "refs/tags/v2"));
        Assert.Null(TagNames.Read(listed, "refs/tags/v"));
        GitResult ownTag = new(1, "To ../remote.git\n!\trefs/tags/v1:refs/tags/v1\t[rejected] (already exists)\nDone\n", "error: failed to push some refs");
        GitResult other = new(1, "To ../remote.git\n!\trefs/heads/main:refs/heads/main\t[rejected] (fetch first)\nDone\n", "error: failed to push some refs");
        Assert.True(TagNames.RemoteHasAnother(ownTag), "A push the remote refused for having its own tag of the name was not told so");
        Assert.False(TagNames.RemoteHasAnother(other), "A branch's push refused for another reason was told as the remote having its own tag");
    }

    /// <summary>`git worktree list --porcelain` as git 2.54 writes it: the main worktree first, a detached and locked one,
    /// one on a branch, and one whose folder is gone; and the folder New Worktree… offers, beside the main one.</summary>
    private void Worktrees_are_read_as_git_lists_them_and_a_new_ones_folder_goes_beside_the_main_one_named_for_its_branch()
    {
        string listed = "worktree /src/shop\nHEAD 0285f32\nbranch refs/heads/main\n\nworktree /src/shop-old\nHEAD caa5eae\ndetached\nlocked on a USB stick\n\n"
            + "worktree /src/shop-feature\nHEAD 0285f32\nbranch refs/heads/feature\n\nworktree /src/shop-gone\nHEAD 0285f32\nbranch refs/heads/gone\nprunable gitdir file points to non-existent location\n\n";
        Assert.Equal(
            [
                new WorktreeInfo("/src/shop", "0285f32", "refs/heads/main", true),
                new WorktreeInfo("/src/shop-old", "caa5eae", null, false, "on a USB stick"),
                new WorktreeInfo("/src/shop-feature", "0285f32", "refs/heads/feature", false),
                new WorktreeInfo("/src/shop-gone", "0285f32", "refs/heads/gone", false, Gone: "gitdir file points to non-existent location"),
            ], Worktrees.Read(listed));
        Assert.Equal("feature", Worktrees.Read(listed)[2].BranchName);
        string main = Path.Combine(Path.GetTempPath(), "shop");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "shop-feature-login"), Worktrees.Beside(main, "feature/login"));
        Assert.Equal("A branch cannot be named HEAD", RefNames.WhyNot("HEAD", "branch"));
        Assert.Null(RefNames.WhyNot("HEAD", "tag"));
    }
}
