using Avalonia.Controls;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The machine's .NET as the window finds it, read off the UI thread as the window opens and comes in front: what
/// it or the folder lacks (<see cref="DotnetWant"/>) said in the banner over the window's middle, on the Welcome screen and
/// a folder alike, with its fix. Install .NET SDK asks, then installs the SDK wanted into the person's own folder
/// (<see cref="DotnetInstall"/>), which Passenger, its terminal and what it starts use at once, the folder's projects then
/// evaluated with it; Add .NET to the Shell's PATH puts it on the PATH of shells started outside Passenger. An agent does
/// neither: it may ask the person.</summary>
public partial class MainWindow
{
    /// <summary>The machine's .NET as last read, null before the first read lands.</summary>
    private DotnetMachine? machine;

    /// <summary>What would put the installation found on the shell's PATH, read with it; null where it is there already.</summary>
    private ShellPath? shellPath;

    /// <summary>The banner's word that the SDK is off the shell's PATH, hidden by the person for this window's life.</summary>
    private bool offPathHidden;

    private Button? installLink;
    private Button? downloadLink;
    private Button? pathLink;
    private Button? hideLink;

    /// <summary>What .NET the machine or the folder open lacks, as the banner says it.</summary>
    public DotnetWant DotnetWant { get; private set; } = DotnetWant.Nothing;

    /// <summary>What the banner over the window says of .NET, null while it says nothing.</summary>
    public string? DotnetSaid => DotnetBanner.Said;

    private void AddDotnet()
    {
        actions!.Add(new IdeAction(AppActions.InstallDotnetSdk, "Install .NET SDK…", "IDE", context => IdeErrors.Observe(InstallDotnetAsync(context)))
        {
            WhyNot = context => context.ByAgent ? Refusals.AgentInstalls
                : DotnetWant.Install is null ? Refusals.NothingToInstall(machine, DotnetWant)
                : null,
            Menus = [new MenuPlace("Tools", 1, 0)],
            Words = ["dotnet", "Download .NET"],
            Works = AppActions.InstallDotnetSdk,
        });
        actions.Add(new IdeAction(AppActions.AddDotnetToPath, "Add .NET to the Shell's PATH…", "IDE", _ => IdeErrors.Observe(AddDotnetToPathAsync()))
        {
            WhyNot = context => context.ByAgent ? Refusals.AgentInstalls
                : machine?.Found is not { } found ? Refusals.NoDotnet
                : shellPath is null ? Refusals.OnPathAlready(found.Root)
                : null,
            Menus = [new MenuPlace("Tools", 1, 1)],
            Words = ["dotnet", RuntimeVariables.Root],
            Works = AppActions.AddDotnetToPath,
        });
        installLink = DotnetBanner.Link(actions[AppActions.InstallDotnetSdk]!.Title, () => Request(AppActions.InstallDotnetSdk));
        downloadLink = DotnetBanner.Link("Download Page", () => Browse(DotnetReleases.PageOf(DotnetWant.Install?.Channel ?? Channel(DotnetWant.Install?.Version))));
        pathLink = DotnetBanner.Link(actions[AppActions.AddDotnetToPath]!.Title, () => Request(AppActions.AddDotnetToPath));
        hideLink = DotnetBanner.Link("Hide", () =>
        {
            offPathHidden = true;
            ShowDotnet();
        });
        Activated += (_, _) => ReadDotnet();
        ReadDotnet();
    }

    /// <summary>A version's channel ("10.0" for 10.0.100), null for none.</summary>
    private static string? Channel(string? version) => version?.Split('.') is { Length: >= 2 } parts ? $"{parts[0]}.{parts[1]}" : null;

    /// <summary>The newest read of the machine's .NET, whose answer stands.</summary>
    private Task<bool>? readingDotnet;

    private int dotnetReads;

    /// <summary>Reads the machine's .NET again off the UI thread, then shows what it lacks, unless a read is under way.</summary>
    private void ReadDotnet()
    {
        if (readingDotnet is { IsCompleted: false })
        {
            return;
        }
        IdeErrors.Observe(ReadDotnetAsync());
    }

    /// <summary>A read begun now, whose answer passes over any begun before it: whether an SDK came where there was none,
    /// which it has taken up (<see cref="DotnetCame"/>).</summary>
    private Task<bool> ReadDotnetAsync() => readingDotnet = ReadingDotnetAsync(++dotnetReads);

    private async Task<bool> ReadingDotnetAsync(int mine)
    {
        (DotnetMachine read, ShellPath? change) = await BackgroundWork.Run(() =>
        {
            DotnetMachine found = DotnetMachine.Read();
            // One no PATH names is taken by what Passenger starts as one on the PATH would be.
            if (found.Found is { Place: DotnetPlace.Installed } off)
            {
                Dotnet.Use(off.Root);
            }
            return (found, found.Found is { Place: DotnetPlace.Installed } there ? ShellPath.For(there.Root) : null);
        });
        if (mine != dotnetReads)
        {
            // A read begun since saw the disk later: its answer is the one that stands.
            return await readingDotnet!;
        }
        bool came = machine is { NewestSdk: null } && read.NewestSdk is not null;
        machine = read;
        shellPath = change;
        ShowDotnet();
        if (came)
        {
            DotnetCame();
        }
        return came;
    }

    /// <summary>An SDK came where there was none (installed by Passenger or by hand): the folder's projects are evaluated
    /// with it, and the C# language server, which started without it, starts again.</summary>
    private void DotnetCame()
    {
        Dotnet.ForgetSdks();
        session.Evaluate();
        session.CSharp?.Restart();
    }

    /// <summary>The banner says what the machine or the folder lacks, with its links: Install and the download page for a
    /// want, Add to PATH and Hide for an SDK no PATH names; nothing with nothing lacking.</summary>
    private void ShowDotnet()
    {
        UiThread.Check();
        DotnetWant = DotnetWant.Of(machine, session.Models.MissingSdk, session.Models.SdkVersion,
            Projects.Where(p => p.IsEvaluated).Select(p => (p.Name, p.TargetFrameworks)));
        string? said = DotnetWant.Lack == DotnetLack.OffPath && (offPathHidden || shellPath is null) ? null : Refusals.Dotnet(DotnetWant, Folder);
        DotnetBanner.Say(said);
        bool wants = DotnetWant.Install is not null;
        installLink!.IsVisible = wants;
        downloadLink!.IsVisible = wants;
        pathLink!.IsVisible = DotnetWant.Lack == DotnetLack.OffPath && shellPath is not null;
        hideLink!.IsVisible = DotnetWant.Lack == DotnetLack.OffPath;
        // The status bar's word of the folder's want, said before the machine's .NET was read, in the banner's words.
        if (sdkSaid is not null && status.Text == sdkSaid && session.Models.MissingSdk is { } missing && SdkWords(missing) != sdkSaid)
        {
            ShowSdk();
        }
    }

    /// <summary>What a person's action that needs a .NET SDK is refused with while the folder has none to load, build and
    /// run its projects with; null while it has one.</summary>
    private string? WithoutSdk() => DotnetWant.NoSdkAtAll ? Refusals.Dotnet(DotnetWant, Folder) : null;

    /// <summary>What New Solution and New Project are refused with while the machine has no SDK at all; null with one.</summary>
    private string? WithoutAnySdk() => DotnetWant.Lack is DotnetLack.NoDotnet or DotnetLack.RuntimeOnly ? Refusals.Dotnet(DotnetWant, Folder) : null;

    /// <summary>Install .NET SDK: Microsoft's metadata asked which SDK mends the want, the person asked, the SDK installed
    /// with its stages said, then taken up at once; it says what it came to.</summary>
    private async Task InstallDotnetAsync(ActionContext context)
    {
        UiThread.Check();
        if (DotnetWant.Install is not { } wanted || work.Begin(AppActions.InstallDotnetSdk, "Asking Microsoft which .NET SDK to install") is not { } installing)
        {
            return;
        }
        using (installing)
        {
            string into = DotnetInstall.Folder;
            SdkDownload sdk;
            try
            {
                wanted = await BackgroundWork.Run(() => WithRollForward(wanted, DotnetWant.Missing?.GlobalJson));
                sdk = await BackgroundWork.Run(() => DotnetReleases.ChooseAsync(wanted, DotnetReleases.ThisSystem(), installing.Stopping));
            }
            catch (DotnetInstallFailed failed)
            {
                Told(installing, false, $"No .NET SDK was installed: {failed.Message}");
                return;
            }
            catch (OperationCanceledException) when (installing.IsStopped)
            {
                Told(installing, false, "No .NET SDK was installed: stopped");
                return;
            }
            ShellPath? onPath = await BackgroundWork.Run(() => ShellPath.For(into));
            CheckBox? addToPath = onPath is null ? null : new CheckBox { Name = "AddDotnetToPath", IsChecked = true, Content = $"Also add it to your shell's PATH: Passenger {onPath.Shown}" };
            installing.Say($"Asking to install the .NET SDK {sdk.Version}");
            string runtime = sdk.Runtime.Length > 0 ? $" (with the .NET runtime {sdk.Runtime})" : string.Empty;
            string replacing = machine?.Found is { } found && !PathNames.Same(found.Root, into) ? $" in place of the one in {PathNames.Shown(found.Root, null)}" : string.Empty;
            if (!await ConfirmAsync($"Install the .NET SDK {sdk.Version}?",
                $"Passenger downloads the .NET SDK {sdk.Version}{runtime} from Microsoft ({sdk.Url.Host}), checks it against the SHA-512 Microsoft publishes, and installs it "
                + $"into {PathNames.Shown(into, null)}, for you alone: no administrator rights are needed. Passenger, its terminal and the programs it starts use it at once{replacing}.",
                "Install", addToPath))
            {
                Told(installing, false, "No .NET SDK was installed");
                return;
            }
            bool path = addToPath?.IsChecked == true;
            DotnetInstallation installed;
            try
            {
                installed = await BackgroundWork.Run(() => DotnetInstall.InstallAsync(sdk, into, stage => UiThread.Post(() => installing.Say(stage)), installing.Stopping));
            }
            catch (DotnetInstallFailed failed)
            {
                Told(installing, false, failed.Message);
                return;
            }
            catch (OperationCanceledException) when (installing.IsStopped)
            {
                Told(installing, false, $"The .NET SDK {sdk.Version} was not installed: stopped, nothing of it left");
                return;
            }
            Dotnet.Use(installed.Root);
            string pathSaid = path && onPath is not null ? await MakePathAsync(onPath) : string.Empty;
            if (!await ReadDotnetAsync())
            {
                DotnetCame();
            }
            Told(installing, true, $"The .NET SDK {sdk.Version} is installed in {PathNames.Shown(installed.Root, null)}: Passenger uses it now{pathSaid}");
        }
    }

    /// <summary>The SDK wanted with the rollForward of the global.json that asks for it, read off the UI thread.</summary>
    private static SdkWanted WithRollForward(SdkWanted wanted, string? globalJson)
    {
        if (wanted.Version is null || globalJson is null)
        {
            return wanted;
        }
        try
        {
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(File.ReadAllText(globalJson), LenientJson.Document);
            return wanted with { RollForward = JsonMembers.Text(JsonMembers.Object(read.RootElement, "sdk"), "rollForward") };
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return wanted;
        }
    }

    /// <summary>Add .NET to the Shell's PATH: the change said and asked, then made; it says what it came to.</summary>
    private async Task AddDotnetToPathAsync()
    {
        UiThread.Check();
        if (shellPath is not { } change || work.Begin(AppActions.AddDotnetToPath, "Asking to add .NET to your shell's PATH", stoppable: false) is not { } adding)
        {
            return;
        }
        using (adding)
        {
            if (!await ConfirmAsync("Add .NET to your shell's PATH?", $"Passenger {change.Shown}.", "Add to PATH", null))
            {
                Told(adding, false, "Your shell's PATH was left as it was");
                return;
            }
            string said = await MakePathAsync(change);
            await ReadDotnetAsync();
            Told(adding, said.Length > 0 && shellPath is null, said.Length > 0 ? $"{PathNames.Shown(change.Root, null)} is on your shell's PATH{said}" : "Your shell's PATH was left as it was");
        }
    }

    /// <summary>Makes a change of the shell's PATH off the UI thread: what it did, after a "; " (empty when it failed, which
    /// the status bar says).</summary>
    private async Task<string> MakePathAsync(ShellPath change)
    {
        string? failed = await BackgroundWork.Run(() =>
        {
            try
            {
                change.Make();
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return error.Message;
            }
        });
        if (failed is not null)
        {
            status.Say($"Your shell's PATH could not be changed: {failed}", ProblemSeverity.Error, null);
            return string.Empty;
        }
        return change.File is { } file ? $"; {PathNames.Shown(file, null)} puts it on the PATH of shells started from now on" : "; programs started from now on find it on your Path";
    }
}
