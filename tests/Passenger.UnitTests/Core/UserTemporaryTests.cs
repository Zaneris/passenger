using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A state folder's own folders in the user's temporary folder: one each, named by the state whatever its
/// spelling, a kind of it beside its own, all going with a state let go of; a folder made the user's alone, a link in its
/// place refused.</summary>
public class UserTemporaryTests
{
    private static readonly string State = Path.Combine(Path.DirectorySeparatorChar + "no-such-folder", "Passenger");

    [Fact]
    public void A_states_temporary_folders_are_its_own_and_the_users_alone() => Cases.All(
        ("A states folders are in the users temporary folder its kinds named beside its own", A_states_folders_are_in_the_users_temporary_folder_its_kinds_named_beside_its_own),
        ("Two states have folders of their own and one state spelled two ways has one", Two_states_have_folders_of_their_own_and_one_state_spelled_two_ways_has_one),
        ("A folder made the users own is theirs alone and a link in its place is refused", A_folder_made_the_users_own_is_theirs_alone_and_a_link_in_its_place_is_refused),
        ("A state let go of takes its own folders and those of its kinds and leaves anothers", A_state_let_go_of_takes_its_own_folders_and_those_of_its_kinds_and_leaves_anothers),
        ("The folders of a process no longer running go and this ones and the rest stay", The_folders_of_a_process_no_longer_running_go_and_this_ones_and_the_rest_stay));

    private void A_states_folders_are_in_the_users_temporary_folder_its_kinds_named_beside_its_own()
    {
        string own = UserTemporary.Of(State);
        string updates = UserTemporary.Of(State, "updates");
        Assert.True(Path.GetDirectoryName(own) == Path.TrimEndingDirectorySeparator(UserTemporary.Folder()),
            $"The state's folder {own} is not in the user's temporary folder, {UserTemporary.Folder()}");
        Assert.Matches("^passenger-[0-9a-f]{10}$", Path.GetFileName(own));
        Assert.True(updates == $"{own}-updates", $"The state's updates folder is {updates}, not its own folder's name with its kind, {own}-updates");
    }

    private void Two_states_have_folders_of_their_own_and_one_state_spelled_two_ways_has_one()
    {
        Assert.Equal(UserTemporary.Of(State, "updates"), UserTemporary.Of(State + Path.DirectorySeparatorChar, "updates"));
        Assert.NotEqual(UserTemporary.Of(State, "updates"), UserTemporary.Of(State + "-other", "updates"));
    }

    private void A_folder_made_the_users_own_is_theirs_alone_and_a_link_in_its_place_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows' temporary folder, under the user's profile, is theirs alone already");
            return;
        }
        string parent = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            string open = Path.Combine(parent, "open");
            Directory.CreateDirectory(open, Owner | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(open, Owner | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Assert.Equal(open, UserTemporary.Own(open));
            Assert.True(File.GetUnixFileMode(open) == Owner, $"A folder made the user's own has mode {File.GetUnixFileMode(open)}, so others may still write in it");

            string link = Path.Combine(parent, "link");
            Directory.CreateSymbolicLink(link, open);
            Assert.Throws<IOException>(() => UserTemporary.Own(link));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>A state thrown away (a screenshot's, a test's) leaves nothing in the user's temporary folder: its own folder
    /// and every kind of it go, its cache's too, what they hold with them; another state's, whose path merely begins as its
    /// own does, stays.</summary>
    private void A_state_let_go_of_takes_its_own_folders_and_those_of_its_kinds_and_leaves_anothers()
    {
        string state = Path.Combine(Path.DirectorySeparatorChar + "no-such-folder", $"let-go-{Guid.NewGuid():N}");
        string cache = Path.Combine(state, "cache");
        string other = state + "-other";
        string[] going = [UserTemporary.Own(UserTemporary.Of(state)), UserTemporary.Own(UserTemporary.Of(state, "roslyn")), UserTemporary.Own(UserTemporary.Of(cache, "sdk"))];
        string[] staying = [UserTemporary.Own(UserTemporary.Of(other)), UserTemporary.Own(UserTemporary.Of(other, "roslyn"))];
        try
        {
            Directory.CreateDirectory(Path.Combine(going[2], "1234", "MSBuildTemp1a2b3c"));
            UserTemporary.LetGoOf([state, cache]);
            Assert.True(going.All(folder => !Directory.Exists(folder)),
                $"A state let go of left {string.Join(", ", going.Where(Directory.Exists))} in the user's temporary folder, which no Passenger names again");
            Assert.True(staying.All(Directory.Exists), $"Letting go of {state} took another state's folders: {string.Join(", ", staying.Where(folder => !Directory.Exists(folder)))}");
        }
        finally
        {
            UserTemporary.LetGoOf([state, cache, other]);
        }
    }

    /// <summary>In a state's folder of a kind, what a Passenger no longer running made for itself (its id, with or without
    /// what follows a dash) goes as another Passenger makes its own, so a killed one's builds' leftovers go with it; this
    /// process's own stays, and so does what no process names.</summary>
    private void The_folders_of_a_process_no_longer_running_go_and_this_ones_and_the_rest_stay()
    {
        // Past any system's largest process id, so no process runs by it.
        const string Gone = "2000000000";
        string kept = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);
        try
        {
            string[] going = [Path.Combine(kept, Gone), Path.Combine(kept, $"{Gone}-2")];
            string[] staying = [Path.Combine(kept, $"{Environment.ProcessId}-1"), Path.Combine(kept, "MetadataAsSource"), Path.Combine(kept, "-3")];
            foreach (string folder in going.Concat(staying))
            {
                Directory.CreateDirectory(Path.Combine(folder, "MSBuildTemp1a2b3c"));
            }
            UserTemporary.LetGoOfGone(kept);
            Assert.True(going.All(folder => !Directory.Exists(folder)),
                $"A Passenger no longer running left {string.Join(", ", going.Where(Directory.Exists))} in its state's folder, which nothing would take now");
            Assert.True(staying.All(Directory.Exists), $"Another Passenger's look took {string.Join(", ", staying.Where(folder => !Directory.Exists(folder)))}, which no Passenger gone made");
        }
        finally
        {
            Directory.Delete(kept, recursive: true);
        }
    }
}
