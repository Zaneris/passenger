using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A packaged Windows app a plan runs (a WinUI app of WindowsPackageType MSIX): the recipe of its package's layout
/// its build writes beside its program, from which it is registered before it starts.</summary>
public sealed record WindowsPackageLaunch(string Recipe);

/// <summary>A packaged app registered from its build: its package's full and family names, the application started, and
/// the layout it was registered from.</summary>
public sealed record WindowsPackage(string FullName, string FamilyName, string Application, string Layout)
{
    /// <summary>What Windows starts the application by.</summary>
    public string UserModelId => $"{FamilyName}!{Application}";
}

/// <summary>A packaged Windows app run from its build, as a deploy runs one: its layout made from its build's recipe
/// (<see cref="WindowsPackageLayouts"/>) and registered as the person's development package, which Passenger leaves
/// registered as a deploy does (Settings › Apps removes it), then started by Windows through its application's id with the
/// run's variables (<see cref="Starter"/>). Windows, not Passenger, starts its process, so a starter of Passenger's own asks
/// for it, holds it to end with the starter, and lasts as long as it does: Stop and Passenger's end end it through the
/// starter.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsPackages
{
    /// <summary>What a starter is told to start, as JSON: the package, the application's id, the variables, and the file it
    /// writes the app's process id to.</summary>
    [IdentifierHome]
    public const string StartVariable = "PASSENGER_START_PACKAGE";

    /// <summary>The registry value Developer Mode sets, without which Windows registers no package from loose files.</summary>
    private const string DeveloperMode = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";

    /// <summary>How long registering may take: a first one copies the Windows App SDK's files.</summary>
    private static readonly TimeSpan RegisterLimit = TimeSpan.FromMinutes(5);

    /// <summary>Why this machine registers no package from a build, null where it does.</summary>
    public static string? WhyNotRegistered() =>
        Microsoft.Win32.Registry.GetValue(DeveloperMode, "AllowDevelopmentWithoutDevLicense", 0) is 1 ? null
        : "Windows registers a packaged app from its build only with Developer Mode on: turn it on in Settings › System › For developers";

    /// <summary>The build's package laid out and registered for the person: its files copied into its layout where they
    /// changed, then the layout's manifest registered (any running instance of it ended first), each step said, and for a run
    /// nobody sees (<paramref name="unseen"/>) that its windows show anyway. The package, else why not.</summary>
    public static async Task<(WindowsPackage? Package, string? Problem)> RegisterAsync(string name, WindowsPackageLaunch launch, bool unseen, Action<string> say,
        CancellationToken cancel)
    {
        if (WhyNotRegistered() is { } off)
        {
            return (null, $"{name} cannot run: {off}");
        }
        WindowsPackageLayout? layout = File.Exists(launch.Recipe) ? WindowsPackageLayouts.Recipe(await File.ReadAllTextAsync(launch.Recipe, cancel)) : null;
        if (layout is null)
        {
            return (null, $"{name} cannot run: its build wrote no package layout ({Path.GetFileName(launch.Recipe)})");
        }
        WindowsPackageIdentity? identity = File.Exists(layout.Manifest) ? WindowsPackageLayouts.Identity(await File.ReadAllTextAsync(layout.Manifest, cancel)) : null;
        if (identity is not { Applications: [{ } application, ..] })
        {
            return (null, $"{name} cannot run: its package manifest ({layout.Manifest}) names no application");
        }
        int copied = await BackgroundWork.Run(() => LayOut(layout), cancel);
        say(copied == 0 ? $"{identity.Name}'s package layout is as built" : $"Laid out {Plural.Of(copied, "file")} of {identity.Name}'s package in {layout.Folder}");
        string manifest = Path.Combine(layout.Folder, "AppxManifest.xml");
        // A dependency the user lacks refuses it first; another layout's development registration goes; failures say one line.
        string script = $"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {"{"}
            {string.Concat(identity.Dependencies.Select(d => Lacking(d, identity.Processor)))}
            $old = Get-AppxPackage -Name {Quoted(identity.Name)} | Where-Object {"{"} $_.Publisher -eq {Quoted(identity.Publisher)} {"}"} | Select-Object -First 1
            if ($old -and "$($old.InstallLocation)".TrimEnd('\') -ne {Quoted(Path.TrimEndingDirectorySeparator(layout.Folder))}) {"{"}
                if (-not $old.IsDevelopmentMode) {"{"} "installed|$($old.PackageFullName)"; exit 0 {"}"}
                Remove-AppxPackage -Package $old.PackageFullName
            {"}"}
            Add-AppxPackage -Register {Quoted(manifest)} -ForceApplicationShutdown -ForceUpdateFromAnyVersion
            $p = Get-AppxPackage -Name {Quoted(identity.Name)} | Where-Object {"{"} $_.Publisher -eq {Quoted(identity.Publisher)} {"}"} | Select-Object -First 1
            "$($p.PackageFullName)|$($p.PackageFamilyName)"
            {"}"}
            catch {"{"} "failed|$($_.Exception.Message -replace '\s+', ' ')"; exit 0 {"}"}
            """;
        if (Programs.Command("powershell") is not { } powerShell)
        {
            return (null, $"{name} cannot run: Windows PowerShell, which registers its package, is not on the PATH");
        }
        ChildResult result = await ChildProcesses.RunAsync(PowerShell(powerShell, script), limit: RegisterLimit, cancel: cancel);
        string answer = ChildResult.FirstLineOf(result.Output);
        if (result.Succeeded && answer.Split('|', 2) is ["installed", { } installed])
        {
            return (null, $"{name} cannot run: {installed} is installed on this machine other than from a build, which registering the build would take the place of: remove it in Settings › Apps first");
        }
        if (result.Succeeded && answer.Split('|') is ["lacking", { } package, { } version])
        {
            return (null, $"{name} cannot run: its package depends on {package} {version} or newer, which is not installed for this user: "
                + "install the Windows App Runtime of that version from Microsoft's Windows App SDK downloads");
        }
        if (result.Succeeded && answer.Split('|', 2) is ["failed", { } refused])
        {
            return (null, $"{name} cannot run: Windows did not register its package: {refused.Trim()}");
        }
        if (!result.Succeeded || answer.Split('|') is not [{ Length: > 0 } full, { Length: > 0 } family])
        {
            string why = answer.Length > 0 ? answer : $"Windows PowerShell ended with exit code {result.ExitCode}";
            return (null, $"{name} cannot run: Windows did not register its package ({why})");
        }
        say($"Registered {identity.Name} as a development package for this user, which stays registered as a deploy leaves it: Settings › Apps removes it");
        if (unseen)
        {
            say($"Windows shows {name}'s windows on the desktop, where it starts a package's, though nobody sees this Passenger");
        }
        return (new WindowsPackage(full, family, application, layout.Folder), null);
    }

    /// <summary>PowerShell's check that a package it depends on is installed for this user at its least version, of the
    /// package's processor or none, answering "lacking|name|version" and stopping where it is not.</summary>
    private static string Lacking(WindowsPackageDependency dependency, string? processor) => $"""
        if (-not (Get-AppxPackage -Name {Quoted(dependency.Name)} | Where-Object {"{"} $_.Publisher -eq {Quoted(dependency.Publisher)} -and [version]$_.Version -ge [version]{Quoted(dependency.MinVersion.Length > 0 ? dependency.MinVersion : "0.0.0.0")} -and ({(processor is { Length: > 0 } own ? $"$_.Architecture -eq {Quoted(own)} -or " : string.Empty)}$_.Architecture -eq 'Neutral') {"}"})) {"{"} "lacking|{dependency.Name}|{dependency.MinVersion}"; exit 0 {"}"}

        """;

    /// <summary>Copies each of the layout's files from where the build has it where its copy differs, and beside each program
    /// or assembly its symbols where the build has them, which a package leaves out and a debugger reads beside what it
    /// loads; how many it copied.</summary>
    private static int LayOut(WindowsPackageLayout layout)
    {
        int copied = 0;
        IEnumerable<(string From, string To)> symbols = layout.Files
            .Where(f => f.From.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.From.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.ChangeExtension(f.From, ".pdb"), Path.ChangeExtension(f.To, ".pdb")))
            .Where(f => !layout.Files.Any(listed => listed.To.Equals(f.Item2, StringComparison.OrdinalIgnoreCase)));
        foreach ((string from, string to) in layout.Files.Concat(symbols))
        {
            string into = Path.GetFullPath(Path.Combine(layout.Folder, to.Replace('/', '\\')));
            if (!PathNames.IsUnder(into, layout.Folder) || !File.Exists(from) || FileStamp.Of(from) is not { } built || FileStamp.Of(into) is { } kept && kept.Length == built.Length && kept.Written == built.Written)
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            File.Copy(from, into, overwrite: true);
            File.SetLastWriteTimeUtc(into, built.Written);
            copied++;
        }
        return copied;
    }

    /// <summary>Windows PowerShell running a script given whole, never read from a file, as its encoded command.</summary>
    private static ProcessStartInfo PowerShell(string program, string script) => ChildProcesses.Command(program,
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);

    /// <summary>A PowerShell string of the text, quoted as a single-quoted string is.</summary>
    private static string Quoted(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>What starts a registered package's application with these arguments and variables (Passenger's own program, as
    /// <see cref="Start"/> runs it), which lasts as long as the app does and ends it as it ends; <paramref name="started"/>
    /// names a file it writes the app's process id to once it runs. The variables go to the app alone: the starter, a .NET
    /// program itself, takes none of them.</summary>
    public static ProcessStartInfo Starter(WindowsPackage package, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> variables, string folder, string? started) =>
        ChildProcesses.Command(Product.Program, [], folder, StarterVariables(package, arguments, variables, started));

    /// <summary>The one variable a starter (<see cref="Starter"/>) is given: what it is to start, as JSON.</summary>
    public static Dictionary<string, string> StarterVariables(WindowsPackage package, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> variables,
        string? started) => new()
    {
        [StartVariable] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["package"] = package.FullName,
            ["application"] = package.UserModelId,
            ["arguments"] = WindowsCommandLine.Of(arguments),
            ["variables"] = variables,
            ["started"] = started,
        }),
    };

    /// <summary>What Main asks before anything starts: where <see cref="StartVariable"/> names a package, Windows starts its
    /// application with the variables given (its package's debug settings carry them, cleared once it runs), its process is
    /// held to end with this one, its id written where asked, and its exit code is this one's; else null, and Passenger
    /// starts as itself.</summary>
    public static int? Start()
    {
        if (Environment.GetEnvironmentVariable(StartVariable) is not { Length: > 0 } told)
        {
            return null;
        }
        OwnEnvironment.Set(StartVariable, null);
        try
        {
            using JsonDocument asked = JsonDocument.Parse(told);
            string package = Text(asked.RootElement, "package") ?? throw new FormatException("no package named");
            string application = Text(asked.RootElement, "application") ?? throw new FormatException("no application named");
            Dictionary<string, string> variables = JsonMembers.Properties(asked.RootElement, "variables")
                .Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
            int id = Activate(package, application, Text(asked.RootElement, "arguments") ?? string.Empty, variables, Text(asked.RootElement, "started"));
            using Process app = Process.GetProcessById(id);
            ChildProcesses.HoldFamily(app);
            Console.Out.WriteLine($"{application} started as process {id}");
            app.WaitForExit();
            return app.ExitCode;
        }
        catch (Exception error) when (error is COMException or FormatException or JsonException or ArgumentException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"Windows could not start the package's app: {error.Message}");
            return 1;
        }
    }

    /// <summary>A file of its own a starter writes its app's process id to (<see cref="Starter"/>), not made yet.</summary>
    public static string StartedFile() =>
        Path.Combine(UserTemporary.Own(UserTemporary.Of(State.StateStore.DefaultRoot, "packages")), $"{Guid.NewGuid():N}.started");

    /// <summary>The process id a starter wrote to <paramref name="file"/> once its app runs, the file deleted once read; null
    /// where the starter has <paramref name="gone"/> first, or Windows took longer than <see cref="StartLimit"/> to start it.</summary>
    public static async Task<int?> StartedAsync(string file, Func<bool> gone)
    {
        Deadline until = Deadline.In(StartLimit);
        while (true)
        {
            if (await BackgroundWork.Run(() => File.Exists(file) ? File.ReadAllText(file) : null) is { } written)
            {
                File.Delete(file);
                return int.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id) ? id : null;
            }
            if (gone() || until.Passed)
            {
                return null;
            }
            await Task.Delay(StartedPoll);
        }
    }

    /// <summary>How long Windows is given to start a package's app, longer on a slower machine.</summary>
    private static TimeSpan StartLimit => Waits.Of(TimeSpan.FromMinutes(1));

    /// <summary>How often a start looks for its starter's word.</summary>
    private static readonly TimeSpan StartedPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>What Windows starts Passenger's own program with as a package's debugger, before its <c>-p</c> and <c>-tid</c>.</summary>
    private const string ResumeArgument = "--resume-package-start";

    /// <summary>The file a debugger's command line names where a start wants no process id told.</summary>
    private const string NoFile = "-";

    /// <summary>What Main asks before anything starts: where Windows ran Passenger as a package's debugger
    /// (<see cref="ResumeArgument"/>, the file to tell its process id, then <c>-p</c> the app's process and <c>-tid</c> its
    /// first thread, held), the id written, the thread let go and 0; else null, and Passenger starts as itself. A held
    /// start's runtime waits for its debugger itself.</summary>
    public static int? Resume(string[] args)
    {
        if (args is not [ResumeArgument, { } file, "-p", { } process, "-tid", { } thread])
        {
            return null;
        }
        if (!uint.TryParse(thread, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint id)
            || !uint.TryParse(process, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return 1;
        }
        if (file != NoFile)
        {
            AtomicFile.Write(file, Encoding.UTF8.GetBytes(process));
        }
        nint held = Kernel32.OpenThread(ThreadSuspendResume, false, id);
        if (held == 0)
        {
            return 1;
        }
        try
        {
            return Kernel32.ResumeThread(held) == uint.MaxValue ? 1 : 0;
        }
        finally
        {
            Kernel32.CloseHandle(held);
        }
    }

    /// <summary>THREAD_SUSPEND_RESUME.</summary>
    private const uint ThreadSuspendResume = 0x0002;

    private static string? Text(JsonElement element, string name) => JsonMembers.Value(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>Starts a package's application through Windows' activation manager, its debug settings giving it the variables
    /// for as long as it takes to start; the process's id. Windows takes variables only with a debugger to start the app
    /// held for, which Passenger's own program is (<see cref="Resume"/>): it writes the app's process id to
    /// <paramref name="started"/> where given, as the activation may wait for a start held further, then lets it go.</summary>
    private static unsafe int Activate(string package, string application, string arguments, IReadOnlyDictionary<string, string> variables, string? started)
    {
        IPackageDebugSettings settings = Create<IPackageDebugSettings>(PackageDebugSettingsClass);
        string block = string.Concat(variables.Select(v => $"{v.Key}={v.Value}\0")) + "\0";
        Marshal.ThrowExceptionForHR(settings.EnableDebugging(package, WindowsCommandLine.Of([Product.Program, ResumeArgument, started ?? NoFile]), block));
        try
        {
            IApplicationActivationManager manager = Create<IApplicationActivationManager>(ActivationManagerClass);
            uint id;
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(application, arguments, 0, &id));
            return checked((int)id);
        }
        finally
        {
            Marshal.ThrowExceptionForHR(settings.DisableDebugging(package));
        }
    }

    private static readonly Guid ActivationManagerClass = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid PackageDebugSettingsClass = new("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D");

    private static unsafe T Create<T>(Guid type) where T : class
    {
        Guid face = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(type, 0, InProcessOrLocal, face, out nint made));
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)made) ?? throw new InvalidOperationException($"Windows made no {typeof(T).Name}");
        }
        finally
        {
            Marshal.Release(made);
        }
    }

    /// <summary>CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER.</summary>
    private const uint InProcessOrLocal = 0x1 | 0x4;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid type, nint outer, uint context, in Guid face, out nint made);
}

/// <summary>Windows' IApplicationActivationManager, which starts an app by its application user model id.</summary>
[GeneratedComInterface]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
internal unsafe partial interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string application, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, uint* process);
}

/// <summary>Windows' IPackageDebugSettings, of which only the debug settings a start gives an app are used: its variables.</summary>
[GeneratedComInterface]
[Guid("F27C3930-8029-4AD1-94E3-3DBA417810C1")]
internal partial interface IPackageDebugSettings
{
    [PreserveSig]
    int EnableDebugging([MarshalAs(UnmanagedType.LPWStr)] string package, [MarshalAs(UnmanagedType.LPWStr)] string? debugger, [MarshalAs(UnmanagedType.LPWStr)] string environment);

    [PreserveSig]
    int DisableDebugging([MarshalAs(UnmanagedType.LPWStr)] string package);
}
