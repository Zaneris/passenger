using Passenger.App.Run;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>Run Anything, and Choose and Run or Debug: popups over what the open folder runs, each running what is chosen
/// through the window's actions, so Run's and Debug's refusals hold there as they do on the toolbar. Run Anything offers
/// the folder's run configurations (its projects', launch profiles, its own, and its packages' scripts, which are
/// configurations too), then the command lines run from it before, then what is typed as a command line: those run in a
/// Terminal tab of the open folder, whose shell takes the person's own ways and the folder's own dotnet first, so any of
/// dotnet's commands, or another tool's, runs there as typed.</summary>
public partial class MainWindow
{
    /// <summary>How many command lines Run Anything offers again.</summary>
    private const int RecentCommandsKept = 10;

    /// <summary>The command lines Run Anything ran in this window, the latest first.</summary>
    private readonly List<string> recentCommands = [];

    private void AddRunAnything()
    {
        Add(A.RunAnything, "Run Anything…", "Run", ShowRunAnything, NeedsFolder, ("Run", 0, 4), words: ["Command", "Terminal", "Run configuration"]);
        Add(A.ChooseRunConfiguration, "Choose and Run…", "Run", context => ShowConfigurationChoice(context, debug: false), NeedsFolder, ("Run", 0, 4),
            words: ["Run configuration", "Choose Run Configuration"]);
        Add(A.ChooseDebugConfiguration, "Choose and Debug…", "Run", context => ShowConfigurationChoice(context, debug: true), NeedsFolder, ("Run", 0, 4),
            words: ["Debug configuration", "Choose Debug Configuration"]);
    }

    /// <summary>Run Anything: a configuration chosen runs (Shift+Enter debugs it), a command line runs in a Terminal tab.</summary>
    private void ShowRunAnything(ActionContext context)
    {
        RunOffer[] offered = [.. ConfigurationChoices("Run configuration"), .. recentCommands.Select(c => new RunOffer(c, RunWords.RecentInTerminal, Command: c))];
        RunOffersPopup popup = new(TitleOf(A.RunAnything), $"Run a configuration or a command line; {OtherwiseKey} debugs", offered, commands: true,
            Refusals.NothingToRunYet);
        popups.Show(popup, chosen => Take(chosen, popup.Otherwise, context));
    }

    /// <summary>Choose and Run, or Debug: a configuration chosen runs or is debugged (Shift+Enter the other way), and Edit
    /// Configurations is offered last.</summary>
    private void ShowConfigurationChoice(ActionContext context, bool debug)
    {
        string id = debug ? A.ChooseDebugConfiguration : A.ChooseRunConfiguration;
        RunOffer[] offered = [.. ConfigurationChoices(string.Empty), new RunOffer(actions![A.EditConfigurations]!.TitleWhere(context), string.Empty, Action: A.EditConfigurations)];
        RunOffersPopup popup = new(TitleOf(id), debug ? $"Debug a configuration; {OtherwiseKey} runs it" : $"Run a configuration; {OtherwiseKey} debugs it", offered,
            commands: false, Refusals.NoRunConfigurationMatches);
        popups.Show(popup, chosen => Take(chosen, popup.Otherwise != debug, context));
    }

    /// <summary>The key that takes a choice the other way (<see cref="RunOffersPopup.Otherwise"/>), in words.</summary>
    private static string OtherwiseKey => KeyWords.Of(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Shift);

    /// <summary>The configurations as the toolbar's menu lists them, each saying <paramref name="detail"/> beside it, or
    /// greyed with why where it cannot launch here.</summary>
    private IEnumerable<RunOffer> ConfigurationChoices(string detail) =>
        Configurations.Where(c => !c.IsScript || !c.Generated).Concat(Configurations.Where(c => c is { IsScript: true, Generated: true }))
            .Select(c => new RunOffer(c.Name, detail, Configuration: c, Refused: WhyNotHere(c)));

    /// <summary>What was chosen, run: a configuration, chosen as the toolbar's is, then run or debugged by its action; a command
    /// line in a Terminal tab of its own; an action by itself.</summary>
    private void Take(RunOffer? chosen, bool debug, ActionContext context)
    {
        if (chosen?.Configuration is { } configuration)
        {
            SelectConfiguration(configuration.Name);
            Request(debug ? A.Debug : A.RunProgram, context);
        }
        else if (chosen?.Command is { } command)
        {
            IdeErrors.Observe(RunCommandAsync(command));
        }
        else if (chosen?.Action is { } action)
        {
            Request(action, context);
        }
    }

    /// <summary>A command line run in a new Terminal tab of the open folder, as if typed there, and kept to be offered again.</summary>
    private async Task RunCommandAsync(string command)
    {
        if (Folder is not { } folder)
        {
            return;
        }
        recentCommands.Remove(command);
        recentCommands.Insert(0, command);
        if (recentCommands.Count > RecentCommandsKept)
        {
            recentCommands.RemoveAt(recentCommands.Count - 1);
        }
        Terminal.TerminalSession shell = await OpenTerminalAt(folder);
        if (shell.IsRunning)
        {
            shell.Write(command + "\r");
        }
    }
}
