namespace Passenger.Core;

/// <summary>The environment variables of .NET's runtime, its host and WPF that Passenger sets or reads in the programs it
/// starts, each named once. Framework-free, since it is linked into what Passenger puts inside another program, which
/// never loads Passenger's own assemblies.</summary>
[IdentifierHome]
public static class RuntimeVariables
{
    /// <summary>The runtime's list of startup hooks, each an assembly it calls before the program's Main; Passenger's own
    /// go first through StartupHooks.First.</summary>
    public const string StartupHooks = "DOTNET_STARTUP_HOOKS";

    /// <summary>"1" holds the runtime at its start, before the program's first instruction, until a diagnostic client
    /// (a debugger attaching, a profiler) lets it go; the variable stays in the program's environment for every program it
    /// starts, so HeldStart's let-go clears it there.</summary>
    public const string DefaultDiagnosticPortSuspend = "DOTNET_DefaultDiagnosticPortSuspend";

    /// <summary>A diagnostic port the runtime connects to as it starts: its address, then connect or listen, then
    /// suspend or nosuspend.</summary>
    public const string DiagnosticPorts = "DOTNET_DiagnosticPorts";

    /// <summary>"0" turns every diagnostic the runtime offers off: its debugger, its diagnostic port and its profiling.</summary>
    public const string EnableDiagnostics = "DOTNET_EnableDiagnostics";

    /// <summary>"0" turns the runtime's debugger off alone.</summary>
    public const string EnableDiagnosticsDebugger = "DOTNET_EnableDiagnostics_Debugger";

    /// <summary>"0" turns the runtime's diagnostic port off alone.</summary>
    public const string EnableDiagnosticsIpc = "DOTNET_EnableDiagnostics_IPC";

    /// <summary>"debug" lets a debugger change the program's unoptimized code as it runs (Hot Reload), read by the runtime
    /// as each module loads.</summary>
    public const string ModifiableAssemblies = "DOTNET_MODIFIABLE_ASSEMBLIES";

    /// <summary>"0" has the runtime collect garbage with the workstation collector, "1" with the server one.</summary>
    public const string GcServer = "DOTNET_gcServer";

    /// <summary>"1" lets the host roll a program forward onto a prerelease runtime when no release one will do.</summary>
    public const string RollForwardToPrerelease = "DOTNET_ROLL_FORWARD_TO_PRERELEASE";

    /// <summary>The stack each thread the runtime starts is given, in bytes written in hex, the thread pool's too.</summary>
    public const string ThreadStackSize = "DOTNET_Thread_DefaultStackSize";

    /// <summary>How hard the runtime's GC compacts a heap left fragmented, from 0 (its default) to 9.</summary>
    public const string ConserveMemory = "DOTNET_GCConserveMemory";

    /// <summary>The .NET command line a build's tools and the SDK's own programs run under, as the command line gives it to
    /// MSBuild.</summary>
    public const string HostPath = "DOTNET_HOST_PATH";

    /// <summary>The folder of the .NET installation a program's apphost takes its runtime from, before the system's own.</summary>
    public const string Root = "DOTNET_ROOT";

    /// <summary>"1" has WPF record where in its XAML each object was made (VisualDiagnostics.GetXamlSourceInfo) and
    /// raise BindingDiagnostics.BindingFailed; read as WPF first loads XAML, so it stays in the environment.</summary>
    public const string XamlDiagnosticsSourceInfo = "ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO";
}
