using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>Windows' hand-off of an update made ready to Velopack's Update.exe as Passenger ends: a batch file of its own,
/// outside the install, started apart from Passenger, applies it with Update.exe's words, and applies it again, up to
/// <see cref="Tries"/> times in all, while Update.exe fails, as it does when something still holds the install's current\
/// folder through its own retries (the version replaced, run as Velopack's obsolete hook, holds it for a moment as it
/// ends); then, for Restart to Update, has Update.exe start the version now installed, as its own restart would. What each
/// try came to is in Velopack's log, which the next start reads (<see cref="VelopackLog"/>).</summary>
public static class UpdateHandOff
{
    /// <summary>The program applying the update: Update.exe, or a batch file standing in for it in a test.</summary>
    [IdentifierHome]
    public const string ProgramVariable = "PASSENGER_UPDATE_PROGRAM";

    /// <summary>How many times in all the update is applied while Update.exe fails.</summary>
    [IdentifierHome]
    public const string TriesVariable = "PASSENGER_UPDATE_TRIES";

    /// <summary>1 where the version installed is started once the tries end (Restart to Update).</summary>
    [IdentifierHome]
    public const string RestartVariable = "PASSENGER_UPDATE_RESTART";

    /// <summary>How many times in all an update is applied: each try extracts the package again before it renames current\,
    /// about twenty seconds, which spaces the tries' renames, so a few span a minute or so.</summary>
    public const int Tries = 3;

    /// <summary>The batch file, its words Update.exe's, given as they are through cmd.exe (<see cref="WindowsCommandLine.ThroughCmd"/>):
    /// a batch file standing in for Update.exe is called, so the tries go on after it.</summary>
    private const string Batch = $$"""
        @echo off
        setlocal
        set tries=1
        for %%P in ("%{{ProgramVariable}}%") do set kind=%%~xP
        :apply
        if /i not "%kind%"==".exe" goto called
        "%{{ProgramVariable}}%" %*
        goto tried
        :called
        call "%{{ProgramVariable}}%" %*
        :tried
        if not errorlevel 1 goto applied
        if %tries% geq %{{TriesVariable}}% goto applied
        set /a tries+=1
        goto apply
        :applied
        if not "%{{RestartVariable}}%"=="1" goto done
        if /i not "%kind%"==".exe" goto start
        "%{{ProgramVariable}}%" start
        goto done
        :start
        call "%{{ProgramVariable}}%" start
        :done

        """;

    /// <summary>Starts the hand-off to <paramref name="program"/> with Update.exe's words for the apply, in Update.exe's own
    /// folder, apart from Passenger, which it outlives; the batch file written first to the user's temporary folder for
    /// Passenger's state. Throws as a start that fails does.</summary>
    public static void Start(string program, IReadOnlyList<string> apply, bool restart)
    {
        string folder = UserTemporary.Own(UserTemporary.Of(State.StateStore.DefaultRoot, "update"));
        string batch = Path.Combine(folder, "apply-update.cmd");
        AtomicFile.Write(batch, Encoding.ASCII.GetBytes(Batch.ReplaceLineEndings("\r\n")));
        ProcessStartInfo start = ChildProcesses.Command(batch, apply, Path.GetDirectoryName(program), new Dictionary<string, string>
        {
            [ProgramVariable] = program,
            [TriesVariable] = Tries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [RestartVariable] = restart ? "1" : "0",
        });
        ChildProcesses.StartApart(start);
    }
}
