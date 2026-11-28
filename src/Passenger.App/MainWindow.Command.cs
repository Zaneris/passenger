using Passenger.Core;

namespace Passenger.App;

/// <summary>The passenger-ide command on macOS (<see cref="PassengerCommand"/>), read off the UI thread as the window opens
/// and comes in front: Add the passenger-ide Command… says what it changes and asks, then links it in ~/.local/bin and puts
/// that folder on the shell's PATH where nothing does; Remove the passenger-ide Command… asks, then removes the link. Only
/// the person does either: an agent's is refused, and no agent's input into the window does either or answers its question
/// (<see cref="RefusedToAgentsInput"/>). Windows' installer and Linux's install.sh make the command there.</summary>
public partial class MainWindow
{
    /// <summary>The command as last read, null before the first read lands.</summary>
    private MacCommand? macCommand;

    /// <summary>The newest read of the command.</summary>
    private Task? readingCommand;

    /// <summary>The reads and changes of the command begun, so a read begun before the last of them lands on nothing.</summary>
    private int commandReads;

    /// <summary>The command as the window last read it, null before the first read lands.</summary>
    public MacCommand? PassengerCommandRead => macCommand;

    /// <summary>Both actions on every system, the menu's on macOS alone: elsewhere they say what makes the command there.</summary>
    private void AddPassengerCommand()
    {
        string[] words = ["command line", "terminal", "shell", "PATH", "launcher", "CLI"];
        MenuPlace[] Tools(int order) => OperatingSystem.IsMacOS() ? [new MenuPlace("Tools", 1, order)] : [];
        actions!.Add(new IdeAction(AppActions.AddCommand, $"Add the {PassengerCommand.Name} Command…", "IDE", _ => IdeErrors.Observe(AddCommandAsync()))
        {
            WhyNot = context => AskedByAgent(context) ? Refusals.CommandIsThePersons
                : Refusals.CommandMadeElsewhere ?? (macCommand is { } read ? NotAdded(read) : Refusals.CommandStillRead),
            Menus = Tools(4),
            Words = words,
            Works = AppActions.AddCommand,
        });
        actions.Add(new IdeAction(AppActions.RemoveCommand, $"Remove the {PassengerCommand.Name} Command…", "IDE", _ => IdeErrors.Observe(RemoveCommandAsync()))
        {
            WhyNot = context => AskedByAgent(context) ? Refusals.CommandIsThePersons
                : Refusals.CommandMadeElsewhere ?? (macCommand is { } read ? NotRemoved(read) : Refusals.CommandStillRead),
            Menus = Tools(5),
            Words = words,
            Works = AppActions.RemoveCommand,
        });
        if (OperatingSystem.IsMacOS())
        {
            Activated += (_, _) => ReadCommand();
            ReadCommand();
        }
    }

    /// <summary>Why the command is not added as it stands: none where adding it changes something.</summary>
    private static string? NotAdded(MacCommand read) =>
        (read.Unlinkable ?? (read.Held == CommandHeld.Others ? Refusals.CommandOthers(read.Link)
            : read is { Held: CommandHeld.This, OnPath: null } ? Refusals.CommandThere(read.Link)
            : null))?.Shown;

    /// <summary>Why the command is not removed as it stands: none where a link of Passenger's is there.</summary>
    private static string? NotRemoved(MacCommand read) => read.Held switch
    {
        CommandHeld.None => Refusals.NoCommand(read.Link).Shown,
        CommandHeld.Others => Refusals.CommandOthers(read.Link).Shown,
        _ => null,
    };

    /// <summary>Reads the command again off the UI thread, unless a read is under way.</summary>
    private void ReadCommand()
    {
        if (readingCommand is { IsCompleted: false })
        {
            return;
        }
        readingCommand = ReadCommandAsync();
        IdeErrors.Observe(readingCommand);
    }

    private async Task ReadCommandAsync()
    {
        int mine = ++commandReads;
        MacCommand read = await BackgroundWork.Run(() => PassengerCommand.Read());
        if (mine == commandReads)
        {
            macCommand = read;
        }
    }

    /// <summary>The command as a change left it, which stands over every read begun before.</summary>
    private void Took(MacCommand read)
    {
        commandReads++;
        macCommand = read;
    }

    /// <summary>Add the passenger-ide Command: read again, said and asked, then made; it says what it came to.</summary>
    private async Task AddCommandAsync()
    {
        UiThread.Check();
        if (RefusedToAgentsInput(Refusals.CommandIsThePersons.Shown)
            || work.Begin(AppActions.AddCommand, $"Asking to add the {PassengerCommand.Name} command", stoppable: false) is not { } adding)
        {
            return;
        }
        using (adding)
        {
            MacCommand read = await BackgroundWork.Run(() => PassengerCommand.Read(shellRead: true));
            Took(read);
            if (NotAdded(read) is { } why)
            {
                Told(adding, false, why);
                return;
            }
            string link = PathNames.Shown(read.Link, null);
            string folder = PathNames.Shown(PassengerCommand.Folder, null);
            string replacing = read.Held == CommandHeld.Passengers ? ", in place of the link there to another Passenger's" : string.Empty;
            string path = read.OnPath is { } change ? $" It then {change.Shown}: a terminal open now finds the command once it reads that file again."
                : $" {folder} is on your shell's PATH already.";
            string text = $"Passenger links {link} to the {PassengerCommand.Name} script it carries{replacing}, so in a terminal {PassengerCommand.Name} opens a folder, a solution "
                + $"or a file in Passenger, the Passenger running taking it where one is.{path} No administrator is asked; Tools, Remove the {PassengerCommand.Name} Command… takes it away.";
            if (!await ConfirmAsync($"Add the {PassengerCommand.Name} command?", text, "Add Command", null, Refusals.CommandIsThePersons.Shown))
            {
                Told(adding, false, $"The {PassengerCommand.Name} command was not added");
                return;
            }
            (MacCommand? made, string? failed) = await BackgroundWork.Run(() => Changed(PassengerCommand.Add));
            if (made is null)
            {
                Told(adding, false, $"The {PassengerCommand.Name} command was not added: {failed}");
                return;
            }
            Took(made);
            string onPath = read.OnPath?.File is { } file ? $"; {PathNames.Shown(file, null)} puts {folder} on the PATH of shells started from now on" : string.Empty;
            Told(adding, true, $"{link} leads to this Passenger{onPath}");
        }
    }

    /// <summary>Remove the passenger-ide Command: read again, asked, then the link removed; it says what it came to.</summary>
    private async Task RemoveCommandAsync()
    {
        UiThread.Check();
        if (RefusedToAgentsInput(Refusals.CommandIsThePersons.Shown)
            || work.Begin(AppActions.RemoveCommand, $"Asking to remove the {PassengerCommand.Name} command", stoppable: false) is not { } removing)
        {
            return;
        }
        using (removing)
        {
            MacCommand read = await BackgroundWork.Run(() => PassengerCommand.Read());
            Took(read);
            if (NotRemoved(read) is { } why)
            {
                Told(removing, false, why);
                return;
            }
            string link = PathNames.Shown(read.Link, null);
            string text = $"Passenger removes {link}, which leads to {(read.Held == CommandHeld.This ? "this Passenger" : "another Passenger")}. "
                + $"{PathNames.Shown(PassengerCommand.Folder, null)} stays on your shell's PATH, where other programs' commands may be.";
            if (!await ConfirmAsync($"Remove the {PassengerCommand.Name} command?", text, "Remove Command", null, Refusals.CommandIsThePersons.Shown))
            {
                Told(removing, false, $"The {PassengerCommand.Name} command was left as it was");
                return;
            }
            (MacCommand? left, string? failed) = await BackgroundWork.Run(() => Changed(PassengerCommand.Remove));
            if (left is null)
            {
                Told(removing, false, $"The {PassengerCommand.Name} command was not removed: {failed}");
                return;
            }
            Took(left);
            Told(removing, true, $"{link} is removed");
        }
    }

    /// <summary>A change of the command, off the UI thread: the command after it, or why it failed.</summary>
    private static (MacCommand? After, string? Failed) Changed(Func<MacCommand> change)
    {
        try
        {
            return (change(), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, error.Message);
        }
    }
}
