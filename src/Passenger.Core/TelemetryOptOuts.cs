namespace Passenger.Core;

/// <summary>What keeps each .NET and Avalonia tool Passenger runs for a folder's code from sending usage data to its maker,
/// each named once, as its own documentation names it, and each taking <see cref="On"/>: the .NET SDK's (its commands, and
/// MSBuild wherever it runs on .NET), Microsoft.Testing.Platform's test programs', Avalonia's build task's,
/// dotnet-svcutil's, Azure Functions Core Tools' and Aspire's CLI's. A tool's start is given each one the person's
/// environment does not set (<see cref="ChildProcesses.SendingNoTelemetry(System.Diagnostics.ProcessStartInfo)"/>).</summary>
[IdentifierHome]
public static class TelemetryOptOuts
{
    /// <summary>The .NET SDK's: every dotnet command but a program's own run, and the MSBuild in it or in a helper.</summary>
    public const string DotnetCli = "DOTNET_CLI_TELEMETRY_OPTOUT";

    /// <summary>A Microsoft.Testing.Platform test program's, which it also reads from <see cref="DotnetCli"/>.</summary>
    public const string TestingPlatform = "TESTINGPLATFORM_TELEMETRY_OPTOUT";

    /// <summary>Avalonia's build task's, its only switch: no MSBuild property turns it off.</summary>
    public const string Avalonia = "AVALONIA_TELEMETRY_OPTOUT";

    /// <summary>dotnet-svcutil's.</summary>
    public const string Svcutil = "DOTNET_SVCUTIL_TELEMETRY_OPTOUT";

    /// <summary>Azure Functions Core Tools' func.</summary>
    public const string FunctionsCoreTools = "FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT";

    /// <summary>The value each takes to send nothing.</summary>
    public const string On = "1";

    /// <summary>Every one, the .NET SDK's first.</summary>
    public static readonly IReadOnlyList<string> All = [DotnetCli, TestingPlatform, Avalonia, Svcutil, FunctionsCoreTools, AspireVariables.TelemetryOptOut];
}
