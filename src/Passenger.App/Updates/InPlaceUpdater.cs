using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>An updater that puts the release made ready in place itself as Passenger ends, before the process has gone:
/// macOS's app bundle and Linux's folder. Its install is one sequence over each system's own steps: the release made
/// ready found (<see cref="Staged"/>), held back while another Passenger runs from the install, the agents' relays had
/// follow it, the release put in place (<see cref="Replace"/>) and the system told (<see cref="Placed"/>), Restart to
/// Update's start, then what is left removed (<see cref="Tidy"/>). Why it did not install is kept
/// (<see cref="Updater.NotInstalled"/>) for the next start to say.</summary>
/// <param name="start">How Restart to Update starts the install, given its path.</param>
public abstract class InPlaceUpdater(Func<string, Task> start) : Updater
{
    /// <summary>Runs as each rename of <see cref="InTwoRenames"/> begins, given where from and where to: nothing but in a
    /// test, which takes the install's place there as another program may.</summary>
    public Action<string, string>? Renaming { get; init; }

    public sealed override void Install(bool restart)
    {
        try
        {
            InstallReady(restart);
        }
        finally
        {
            LetRelaysGo();
        }
    }

    /// <summary>Where the release <paramref name="version"/> was made ready: a folder, which may have gone since.</summary>
    protected abstract string Staged(string version);

    /// <summary>Puts the release made ready at <paramref name="staged"/> in place of the install, which stays as it was
    /// where it cannot: why not, or null.</summary>
    protected abstract string? Replace(string staged, string version);

    /// <summary>Tells the system of the release put in place, before it starts; a failure is logged.</summary>
    protected abstract void Placed();

    /// <summary>Removes what installing <paramref name="version"/> left that nothing needs, once it has started.</summary>
    protected abstract void Tidy(string version);

    private void InstallReady(bool restart)
    {
        NotInstalled = null;
        if (Ready is not { } version)
        {
            return;
        }
        if (Asks is not null && !restart)
        {
            IdeErrors.Write($"Updates: Passenger {version} stays ready: installing it asks for an administrator's password, which only Restart to Update asks for");
            return;
        }
        string staged = Staged(version);
        if (!Directory.Exists(staged))
        {
            NotInstalled = $"{staged} is gone";
            IdeErrors.Write($"Updates: Passenger {version} was not installed: {NotInstalled}");
            return;
        }
        IReadOnlyList<OtherCopy> others = Others();
        if (WaitsFor(others, version, Installed) is { } named)
        {
            NotInstalled = $"another Passenger ran from {Installed} ({named})";
            return;
        }
        FollowRelays(others, version);
        string? why = Replace(staged, version);
        // The release is in place, or the old one still is: either way the relays start again from it.
        LetRelaysGo();
        if (why is not null)
        {
            NotInstalled = why;
            IdeErrors.Write($"Updates: Passenger {version} was not installed in {Installed}: {why}");
            Restart(restart);
            IdeErrors.Flush();
            return;
        }
        Placed();
        IdeErrors.Write($"Updates: Passenger {version} installed in {Installed}{(restart ? ", and started" : string.Empty)}");
        Restart(restart);
        Ready = null;
        Tidy(version);
        IdeErrors.Flush();
    }

    /// <summary>The release <paramref name="version"/> at <paramref name="release"/> takes the install's place in two
    /// renames, where the file system cannot exchange two folders in one: the install moved to <paramref name="aside"/>,
    /// then the release into its place, the install put back should that fail. Why not, or null; and whether the install
    /// was left aside, which why not then says, with where the release was left.</summary>
    protected (string? Why, bool LeftAside) InTwoRenames(string release, string aside, string version)
    {
        try
        {
            Rename(Installed, aside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (error.Message, false);
        }
        try
        {
            Rename(release, Installed);
            return (null, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                Rename(aside, Installed);
                return (error.Message, false);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                return ($"{Clause(error.Message)}, and the Passenger installed could not be put back ({Clause(back.Message)}): it is in {aside}, "
                    + $"and Passenger {version} in {release}", true);
            }
        }
    }

    /// <summary>A failure's words as a clause of a longer sentence: without their full stop.</summary>
    private static string Clause(string said) => said.TrimEnd().TrimEnd('.');

    private void Rename(string from, string to)
    {
        Renaming?.Invoke(from, to);
        Directory.Move(from, to);
    }

    /// <summary>Starts the install again for Restart to Update, a failure logged.</summary>
    private void Restart(bool restart)
    {
        if (!restart)
        {
            return;
        }
        try
        {
            // As the process ends, after its last window: nothing is left to await it.
            Synchronously.Run(() => start(Installed));
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            IdeErrors.Write($"Updates: {Installed} could not be started again: {error.Message}");
        }
    }
}
