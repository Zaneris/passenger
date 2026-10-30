namespace Passenger.Core;

/// <summary>Names MSBuild reads that Passenger gives the builds, servers and evaluations it starts, each named once: in their
/// environment (a property given there is one MSBuild takes as the project's) or as a global property. Framework-free, since
/// the project host compiles it in.</summary>
[IdentifierHome]
public static class MSBuildNames
{
    /// <summary>The salt of MSBuild's handshake with its worker nodes: a build takes up only a node left by one of the same
    /// salt.</summary>
    public const string NodeHandshakeSalt = "MSBUILDNODEHANDSHAKESALT";

    /// <summary>"true" lets a project that references a framework the SDK marks Windows-only restore and build off Windows
    /// (NETSDK1100).</summary>
    public const string EnableWindowsTargeting = "EnableWindowsTargeting";
}
