using System.Globalization;
using System.Runtime.CompilerServices;
using Passenger.Core;

[assembly: AssemblyFixture(typeof(Passenger.UnitTests.UnitRunTests.Run))]

namespace Passenger.UnitTests;

/// <summary>A unit test run keeps to a temporary folder of its own (<see cref="RunTemporary"/>), given the run from its
/// first moment, so every folder and file its tests make in a temporary folder goes with the run, as it ends; and what a
/// run killed left goes as the next begins. Nothing else in the unit tests holds the run itself, which only this class sets.</summary>
public sealed class UnitRunTests
{
    /// <summary>The run's own temporary folder, the person's where none could be made.</summary>
    private static string temporary = RunTemporary.Persons;

    /// <summary>Gives the run its own temporary folder, as TMPDIR (TMP and TEMP on Windows), once the folders of runs no
    /// longer running, its own id's from a run before it among them, are taken; the tests loaded again in another load
    /// context take the one it was given.</summary>
    [ModuleInitializer]
    internal static void Begin()
    {
        string given = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (RunTemporary.RunOf(Path.GetFileName(given)) == Environment.ProcessId)
        {
            temporary = given;
            return;
        }
        LetGoOfEnded(own: true);
        if (RunTemporary.Own() is not { } own)
        {
            return;
        }
        temporary = Directory.CreateDirectory(own).FullName;
        foreach (string variable in OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : (string[])["TMPDIR"])
        {
            Environment.SetEnvironmentVariable(variable, temporary);
        }
    }

    /// <summary>Takes the run folders, of either test project, whose process no longer runs, and this process's own where
    /// asked (one a run before it of the same id left).</summary>
    private static void LetGoOfEnded(bool own)
    {
        foreach (string under in RunTemporary.Bases().Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.Ordinal).Where(Directory.Exists))
        {
            foreach (string left in Directory.EnumerateDirectories(under, RunTemporary.Prefix + "*"))
            {
                if (RunTemporary.RunOf(Path.GetFileName(left)) is int id && (id == Environment.ProcessId ? own : !DotnetProcesses.Runs(id)))
                {
                    Delete(left);
                }
            }
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // What a process still holds goes as the next run begins.
        }
    }

    /// <summary>The run's end, which takes its folder with what its tests left there.</summary>
    public sealed class Run : IDisposable
    {
        public void Dispose()
        {
            if (temporary != RunTemporary.Persons)
            {
                Delete(temporary);
            }
        }
    }

    /// <summary>A temporary folder a test makes is in the run's own, which is named for the run's process, and the folder of
    /// a run no longer running is taken as a run begins, this one's staying.</summary>
    [Fact]
    public void A_unit_runs_temporary_files_are_made_in_a_folder_of_its_own_and_one_a_killed_run_left_goes()
    {
        Assert.True(temporary != RunTemporary.Persons && RunTemporary.RunOf(Path.GetFileName(temporary)) == Environment.ProcessId,
            $"The unit run's temporary folder is {temporary}, not one of its own beside the person's {RunTemporary.Persons}: what its tests make there stays for good");
        string made = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
        Directory.Delete(made);
        Assert.True(PathNames.IsUnder(made, temporary), $"A test's temporary folder was made at {made}, outside the run's own {temporary}");

        string killed = Path.Combine(Path.GetDirectoryName(temporary)!, RunTemporary.Prefix + int.MaxValue.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(killed, "passenger-unit-left"));
        LetGoOfEnded(own: false);
        Assert.True(!Directory.Exists(killed), $"The folder a killed run left, {killed}, stayed as a run began");
        Assert.True(Directory.Exists(temporary), $"A run's beginning took {temporary}, the folder of a run still going");
    }
}
