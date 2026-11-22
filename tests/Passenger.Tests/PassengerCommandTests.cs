using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Win32;
using Passenger.App;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>passenger-ide, Passenger's command in a terminal, made where each system makes it and run as a person runs it:
/// on macOS added and removed from the Tools menu over a home of the test's own and an app bundle standing in for the one
/// installed; on Windows its folder put on and taken off the user's Path as the installer's hooks do, over a registry key
/// of the test's own, and the program it carries run. Each run hands a folder, a file and a caret, named from the folder it
/// was started in, to the Passenger running (the test's), whose window on the folder takes them, and the command ends.</summary>
[AboutMacOS]
public sealed class PassengerCommandTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("command-"));

    /// <summary>The project the launches name, its name holding what a command line's shell or cmd.exe would read.</summary>
    private const string Project = "R&D 100%";

    private string Home => Path.Combine(folder, "home");

    private string Link => Path.Combine(Home, ".local", "bin", PassengerCommand.Name);

    public PassengerCommandTests()
    {
        Directory.CreateDirectory(Home);
        ShellPath.HomeStandIn = Home;
    }

    public void Dispose()
    {
        ShellPath.HomeStandIn = null;
        PassengerCommand.BundleStandIn = null;
        TestFolders.Delete(folder);
    }

    /// <summary>On macOS the Tools menu adds the passenger-ide command, which a terminal runs to open a file at a line,
    /// and removes it, leaving another program's command of that name, as one flow of macOS's steps, each in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void On_macOS_the_passenger_ide_command_is_added_run_and_removed_leaving_another_programs()
    {
        Flow.StepApart<PassengerCommandTests>("on macOS another programs passenger ide stays and a link to another Passengers is replaced", t => t.On_macOS_another_programs_passenger_ide_stays_and_a_link_to_another_Passengers_is_replaced());
        Flow.StepApart<PassengerCommandTests>("on macOS the Tools menu adds the command a terminal runs it from a folder to open a file at a line and the menu removes it", t => t.On_macOS_the_Tools_menu_adds_the_command_a_terminal_runs_it_from_a_folder_to_open_a_file_at_a_line_and_the_menu_removes_it());
    }

    /// <summary>On Windows the installer's hooks put the command's folder on the person's Path once and take it off,
    /// and the command hands a folder, file and caret to the running Passenger, as one flow of Windows's steps, each in
    /// an instance of its own.</summary>
    [AvaloniaFact]
    public void On_Windows_the_passenger_ide_command_is_on_the_Path_and_hands_its_launch_to_the_running_Passenger()
    {
        Flow.StepApart<PassengerCommandTests>("on Windows the installers hooks put the commands folder on the users Path once keeping the rest and its kind and take it off", t => t.On_Windows_the_installers_hooks_put_the_commands_folder_on_the_users_Path_once_keeping_the_rest_and_its_kind_and_take_it_off());
        Flow.StepApart<PassengerCommandTests>("on Windows the command alone in its folder hands a relative folder file and caret to the running Passenger and ends at once", t => t.On_Windows_the_command_alone_in_its_folder_hands_a_relative_folder_file_and_caret_to_the_running_Passenger_and_ends_at_once());
    }

    private void On_macOS_the_Tools_menu_adds_the_command_a_terminal_runs_it_from_a_folder_to_open_a_file_at_a_line_and_the_menu_removes_it()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("On Windows the installer makes the command, and on Linux install.sh");
            return;
        }
        PassengerCommand.BundleStandIn = Bundle();
        string project = Directory.CreateDirectory(Path.Combine(folder, Project)).FullName;
        string script = TestFolders.Write(project, "Player.cs", "one\ntwo\nthree\nfour\n");
        MainWindow window = Ui.OpenFolder(project, languageServer: false);
        Ui.WaitFor(() => window.PassengerCommandRead is not null, "the window to read where the command stands");
        string startup = window.PassengerCommandRead!.OnPath?.File
            ?? throw new Xunit.Sdk.XunitException($"Passenger found {Path.GetDirectoryName(Link)} on the PATH of the test's own home, which nothing puts there");
        if (startup.EndsWith(".fish", StringComparison.Ordinal))
        {
            Assert.Skip("The login shell is fish, whose lines /bin/sh cannot read");
            return;
        }
        Assert.Equal(Refusals.CommandIsThePersons, window.Actions.WhyNot(AppActions.AddCommand, window.Actions.Context().ForAgent()));
        Assert.Equal(Refusals.NoCommand(Link), window.Actions.WhyNot(AppActions.RemoveCommand, window.Actions.Context()));

        Chosen(window, $"Add the {PassengerCommand.Name} Command…");
        ConfirmDialog adding = Asked(window);
        Assert.True(Said(adding).Contains($"to the end of {PathNames.Shown(startup, null)}", StringComparison.Ordinal),
            $"Add the command did not say it changes {startup} before it asked: {Said(adding)}");
        Ui.ClickButton(adding, "Add Command");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.AddCommand) is null, () => $"Add the command to end ({window.Status})");

        string launcher = Path.Combine(PassengerCommand.BundleStandIn, "Contents", "Resources", "bin", PassengerCommand.Name);
        Assert.True(new FileInfo(Link).LinkTarget is { } target && PathNames.Same(target, PathNames.Canonical(launcher)),
            $"~/.local/bin/{PassengerCommand.Name} was to lead to the app's launcher at {launcher}; it is {new FileInfo(Link).LinkTarget ?? "no link"} ({window.Status})");
        Assert.Contains("export PATH=\"$HOME/.local/bin:$PATH\"\n", File.ReadAllText(startup), StringComparison.Ordinal);
        Assert.True(window.Actions.WhyNot(AppActions.AddCommand, window.Actions.Context()) == Refusals.CommandThere(Link),
            $"With the command added Add the command was to say it is there; it says {window.Actions.WhyNot(AppActions.AddCommand, window.Actions.Context()) ?? "nothing"}, the window reading {window.PassengerCommandRead}");

        // As a shell started since runs it, after its startup file, in the folder above the project it names from there.
        Ended(Started("/bin/sh", ["-c", ". \"$1\" && exec passenger-ide \"$2\" --file Player.cs --caret 3:2", "sh", startup, Project], folder));
        Ui.WaitFor(() => Caret(window) == (PathNames.Canonical(script), new TextPosition(2, 1)),
            () => $"the command's file in the window on its folder at 3:2; it shows {window.EditorArea.Active?.Path ?? "nothing"} at {Caret(window).At}");

        Chosen(window, $"Remove the {PassengerCommand.Name} Command…");
        Ui.ClickButton(Asked(window), "Remove Command");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.RemoveCommand) is null, () => $"Remove the command to end ({window.Status})");
        Assert.True(!File.Exists(Link) && new FileInfo(Link).LinkTarget is null, $"Remove left ~/.local/bin/{PassengerCommand.Name} leading to {new FileInfo(Link).LinkTarget}");
        Assert.True(File.ReadAllText(startup).Contains("$HOME/.local/bin", StringComparison.Ordinal),
            $"Remove took ~/.local/bin off the PATH in {startup}, where other programs' commands may be");
        Assert.Equal(Refusals.NoCommand(Link), window.Actions.WhyNot(AppActions.RemoveCommand, window.Actions.Context()));
    }

    /// <summary>Another program of the command's name, or a link to one, stays as it was and the command is not added; a link
    /// to another Passenger's launcher (an app moved since, or another copy) is Passenger's to replace.</summary>
    private void On_macOS_another_programs_passenger_ide_stays_and_a_link_to_another_Passengers_is_replaced()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("On Windows the installer makes the command, and on Linux install.sh");
            return;
        }
        PassengerCommand.BundleStandIn = Bundle();
        Directory.CreateDirectory(Path.GetDirectoryName(Link)!);
        File.WriteAllText(Link, "#!/bin/sh\necho 'Phusion Passenger(R) 6.0.27'\n");
        MainWindow window = Ui.OpenFolder(Directory.CreateDirectory(Path.Combine(folder, "open")).FullName, languageServer: false);
        Ui.WaitFor(() => window.PassengerCommandRead is not null, "the window to read where the command stands");

        Assert.Equal(Refusals.CommandOthers(Link), window.Actions.WhyNot(AppActions.AddCommand, window.Actions.Context()));
        Assert.Equal(Refusals.CommandOthers(Link), window.Actions.WhyNot(AppActions.RemoveCommand, window.Actions.Context()));
        Assert.Throws<IOException>(() => PassengerCommand.Add());
        Assert.Equal("#!/bin/sh\necho 'Phusion Passenger(R) 6.0.27'\n", File.ReadAllText(Link));

        File.Delete(Link);
        File.CreateSymbolicLink(Link, Path.Combine(folder, "Moved", "Passenger.app", "Contents", "Resources", "bin", PassengerCommand.Name));
        MacCommand added = PassengerCommand.Add();
        Assert.True(added.Held == CommandHeld.This, $"A link to a Passenger moved away was left as {new FileInfo(Link).LinkTarget}, not replaced by this one's ({added.Held})");
    }

    /// <summary>The hooks' change of the user's Path, over a key of the test's own: the folder put at its end once however
    /// often the hook runs (an install, then each update), every other entry kept as written, an empty one too, and the
    /// value's kind kept; the uninstall's taking it off leaves the value as it was. A Path made for it alone goes with it.</summary>
    private void On_Windows_the_installers_hooks_put_the_commands_folder_on_the_users_Path_once_keeping_the_rest_and_its_kind_and_take_it_off()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Only Windows' installer puts the command's folder on a user's Path");
            return;
        }
        Hooks();
    }

    [SupportedOSPlatform("windows")]
    private static void Hooks()
    {
        string tests = $@"Software\Passenger-tests\{Guid.NewGuid():N}";
        string key = $@"{tests}\Environment";
        string standing = UserEnvironment.Key;
        UserEnvironment.Key = key;
        try
        {
            const string Had = @"C:\Tools;;%USERPROFILE%\bin;";
            using (RegistryKey made = Registry.CurrentUser.CreateSubKey(key))
            {
                made.SetValue(UserEnvironment.PathVariable, Had, RegistryValueKind.String);
            }
            Assert.True(PassengerCommand.PutOnPath(), "The install's hook did not put the command's folder on the Path");
            Assert.False(PassengerCommand.PutOnPath(), "An update's hook put the command's folder on the Path a second time");
            Assert.Equal(($"{Had}{PassengerCommand.Carried};", RegistryValueKind.String), Registered(key));
            Assert.True(PassengerCommand.TakeOffPath(), "The uninstall's hook found no command's folder on the Path to take off");
            Assert.Equal((Had, RegistryValueKind.String), Registered(key));

            using (RegistryKey made = Registry.CurrentUser.CreateSubKey(key))
            {
                made.DeleteValue(UserEnvironment.PathVariable);
            }
            PassengerCommand.PutOnPath();
            Assert.Equal((PassengerCommand.Carried, RegistryValueKind.ExpandString), Registered(key));
            PassengerCommand.TakeOffPath();
            Assert.Equal((null, RegistryValueKind.Unknown), Registered(key));
        }
        finally
        {
            UserEnvironment.Key = standing;
            Registry.CurrentUser.DeleteSubKeyTree(tests, throwOnMissingSubKey: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string? Value, RegistryValueKind Kind) Registered(string key)
    {
        using RegistryKey? environment = Registry.CurrentUser.OpenSubKey(key);
        return environment?.GetValue(UserEnvironment.PathVariable, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            ? (value, environment.GetValueKind(UserEnvironment.PathVariable))
            : (null, RegistryValueKind.Unknown);
    }

    /// <summary>The program the installer's Path names, alone in its folder, run from the folder above the project with the
    /// project's name holding a space, an ampersand and a percent sign as written: it ends at once, and the running Passenger
    /// opens the file at the line.</summary>
    private void On_Windows_the_command_alone_in_its_folder_hands_a_relative_folder_file_and_caret_to_the_running_Passenger_and_ends_at_once()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The command's program is Windows'; macOS and Linux run a script");
            return;
        }
        string command = Path.Combine(PassengerCommand.Carried, PassengerCommand.Name + ".exe");
        string[] carried = [.. Directory.EnumerateFileSystemEntries(PassengerCommand.Carried).Select(Path.GetFileName).OfType<string>()];
        Assert.True(carried is [PassengerCommand.Name + ".exe"], $"The folder the Path names was to hold the command alone; it holds {string.Join(", ", carried)}");
        string project = Directory.CreateDirectory(Path.Combine(folder, Project)).FullName;
        string script = TestFolders.Write(project, "Player.cs", "one\ntwo\nthree\nfour\n");
        MainWindow window = Ui.OpenFolder(project, languageServer: false);
        Assert.Equal(Refusals.CommandMadeElsewhere, window.Actions.WhyNot(AppActions.AddCommand, window.Actions.Context()));

        Ended(Started(command, [Project, "--file", "Player.cs", "--caret", "3:2"], folder));

        Ui.WaitFor(() => Caret(window) == (PathNames.Canonical(script), new TextPosition(2, 1)),
            () => $"the command's file in the window on its folder at 3:2; it shows {window.EditorArea.Active?.Path ?? "nothing"} at {Caret(window).At}");
    }

    /// <summary>An app bundle standing in for the one installed: the launcher the build carries in its Resources/bin, and
    /// Passenger's program and assembly in its Contents/MacOS, links to the test's own.</summary>
    private string Bundle()
    {
        string bundle = Path.Combine(folder, "Passenger.app");
        string bin = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources", "bin")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "bin", PassengerCommand.Name), Path.Combine(bin, PassengerCommand.Name));
        string programs = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS")).FullName;
        File.CreateSymbolicLink(Path.Combine(programs, "Passenger"), Product.Program);
        File.CreateSymbolicLink(Path.Combine(programs, "Passenger.dll"), Path.Combine(AppContext.BaseDirectory, "Passenger.dll"));
        return bundle;
    }

    /// <summary>A program started as a terminal starts one, in a folder, with the test's home and state, its input kept open
    /// so a launch no Passenger took would not end.</summary>
    private Process Started(string program, IReadOnlyList<string> arguments, string at)
    {
        ProcessStartInfo start = new(program) { WorkingDirectory = at, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[StateStore.Variable] = StateStore.DefaultRoot;
        if (!OperatingSystem.IsWindows())
        {
            start.Environment["HOME"] = Home;
            start.Environment["PATH"] = "/usr/bin:/bin";
        }
        return Process.Start(start)!;
    }

    /// <summary>Waits for a launch to end with exit code 0, the window's loop run meanwhile.</summary>
    private static void Ended(Process launch)
    {
        using (launch)
        {
            Task<string> error = launch.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                bool ended = Ui.Pumped(() => !DotnetProcesses.Runs(launch), TimeSpan.FromSeconds(60));
                Assert.True(ended && launch.ExitCode == 0, ended ? $"The command ended with exit code {launch.ExitCode}: {Ui.AwaitPumped(error, "the command's errors")}"
                    : "The command was still running after 60 s: it started a Passenger of its own rather than handing the launch to the one running");
            }
            finally
            {
                if (DotnetProcesses.Runs(launch))
                {
                    OwnProcess.Of(launch).Kill();
                }
            }
        }
    }

    /// <summary>A menu bar item of Tools, chosen as the person chooses it.</summary>
    private static void Chosen(MainWindow window, string title)
    {
        NativeMenuItem item = Ui.Submenu(window.MenuBar, "Tools").Items.OfType<NativeMenuItem>().SingleOrDefault(i => i.Header == title)
            ?? throw new Xunit.Sdk.XunitException($"Tools has no {title}");
        Assert.True(item.IsEnabled, $"{title} is greyed: {window.Actions.WhyNot(title == $"Add the {PassengerCommand.Name} Command…" ? AppActions.AddCommand : AppActions.RemoveCommand, window.Actions.Context())}");
        item.Command!.Execute(null);
        Ui.Settle();
    }

    private static ConfirmDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "the command's question");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
    }

    private static string Said(ConfirmDialog dialog) =>
        string.Join(" | ", Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<TextBlock>().Select(t => t.Text));

    /// <summary>The caret's place in the window's file shown, and that file.</summary>
    private static (string? Path, TextPosition At) Caret(MainWindow window)
    {
        if (window.EditorArea.Active is not { } shown)
        {
            return (null, default);
        }
        int caret = window.EditorArea.CodeEditor.Selections.Primary.Caret;
        TextSnapshot text = shown.Text.Snapshot;
        int line = text.GetLineFromOffset(caret);
        return (shown.Path, new TextPosition(line, caret - text.GetLineStart(line)));
    }
}
