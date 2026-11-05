namespace Passenger.Core;

/// <summary>MSBuild's names Passenger writes, each named once: what it gives the builds, servers and evaluations it starts, or
/// keeps from them (in their environment, where a property given is one MSBuild takes as the project's, or as a global
/// property), the properties it asks an evaluation for or writes into a project or publish profile, and the targets it runs.
/// Those an evaluation carries (Host's CarriedProperties) are named there. Framework-free, since the project host compiles
/// it in.</summary>
[IdentifierHome]
public static class MSBuildNames
{
    /// <summary>The salt of MSBuild's handshake with its worker nodes: a build takes up only a node left by one of the same
    /// salt.</summary>
    public const string NodeHandshakeSalt = "MSBUILDNODEHANDSHAKESALT";

    /// <summary>"0" keeps a build out of MSBuild's server, by MSBuild's own switch.</summary>
    public const string UseServer = "MSBUILDUSESERVER";

    /// <summary>"0" keeps a build out of MSBuild's server, by the .NET command line's switch.</summary>
    public const string CliUseServer = "DOTNET_CLI_USE_MSBUILD_SERVER";

    /// <summary>Set by dotnet test for what it runs: each worker node then holds the command's output open as its own.</summary>
    public const string EnsureStdOutForTaskProcesses = "MSBUILDENSURESTDOUTFORTASKPROCESSES";

    // Where MSBuild and its SDKs are, as a build gives them to the programs it runs (dotnet run's, dotnet test's).
    public const string ExePath = "MSBUILD_EXE_PATH";
    public const string ExtensionsPath = "MSBuildExtensionsPath";
    public const string SdksPath = "MSBuildSDKsPath";
    public const string LoadMicrosoftTargetsReadOnly = "MSBuildLoadMicrosoftTargetsReadOnly";

    /// <summary>"true" lets a project that references a framework the SDK marks Windows-only restore and build off Windows
    /// (NETSDK1100).</summary>
    public const string EnableWindowsTargeting = "EnableWindowsTargeting";

    /// <summary>"true" has a restore fetch only the targeting packs of frameworks a project references itself.</summary>
    public const string DisableTransitiveFrameworkReferenceDownloads = "DisableTransitiveFrameworkReferenceDownloads";

    /// <summary>The design-time targets Razor's generator runs in, which the design-time builds are given.</summary>
    public const string RazorDesignTimeTargets = "RazorDesignTimeTargets";

    // The solution's, which a build of it gives every project.
    public const string SolutionDir = "SolutionDir";
    public const string SolutionPath = "SolutionPath";
    public const string SolutionName = "SolutionName";
    public const string SolutionFileName = "SolutionFileName";
    public const string SolutionExt = "SolutionExt";

    // What a project builds for, and with.
    public const string Configuration = "Configuration";
    public const string TargetFramework = "TargetFramework";
    public const string TargetFrameworks = "TargetFrameworks";
    public const string TargetFrameworkMoniker = "TargetFrameworkMoniker";
    public const string TargetPlatformIdentifier = "TargetPlatformIdentifier";
    public const string RuntimeIdentifier = "RuntimeIdentifier";
    public const string RuntimeIdentifiers = "RuntimeIdentifiers";
    public const string RuntimeFrameworkVersion = "RuntimeFrameworkVersion";
    public const string PlatformTarget = "PlatformTarget";
    public const string SelfContained = "SelfContained";
    public const string Language = "Language";
    public const string Nullable = "Nullable";

    /// <summary>The runtime identifier of the SDK's own machine.</summary>
    public const string NETCoreSdkRuntimeIdentifier = "NETCoreSdkRuntimeIdentifier";

    // Where a project is and what its build makes.
    public const string ProjectDir = "ProjectDir";
    public const string AssemblyName = "AssemblyName";
    public const string OutputPath = "OutputPath";
    public const string TargetDir = "TargetDir";
    public const string TargetFileName = "TargetFileName";
    public const string ProjectDepsFileName = "ProjectDepsFileName";
    public const string ProjectRuntimeConfigFileName = "ProjectRuntimeConfigFileName";
    public const string IntermediateOutputPath = "IntermediateOutputPath";
    public const string BaseIntermediateOutputPath = "BaseIntermediateOutputPath";

    /// <summary>Where Microsoft.Extensions.ApiDescription.Server writes a build's OpenAPI documents.</summary>
    public const string OpenApiDocumentsDirectory = "OpenApiDocumentsDirectory";

    /// <summary>The Design assembly a project names for dotnet ef in place of EF Core's.</summary>
    public const string DesignAssembly = "DesignAssembly";

    /// <summary>The program dotnet run starts.</summary>
    public const string RunCommand = "RunCommand";

    /// <summary>The version of Godot.NET.Sdk a Godot project builds with, as the SDK sets it.</summary>
    public const string GodotSdkVersion = "PackageVersion_Godot_NET_Sdk";

    /// <summary>The arguments dotnet run gives it (StartArguments' too).</summary>
    public const string RunArguments = "RunArguments";

    /// <summary>Where dotnet run runs it (StartWorkingDirectory's when the project sets only that).</summary>
    public const string RunWorkingDirectory = "RunWorkingDirectory";

    /// <summary>The run settings file a test project names.</summary>
    public const string RunSettingsFilePath = "RunSettingsFilePath";

    // A project's container (Docker's and the .NET SDK's), beside those CarriedProperties holds.
    public const string DockerfileBuildArguments = "DockerfileBuildArguments";
    public const string DockerfileRunArguments = "DockerfileRunArguments";
    public const string DockerfileTag = "DockerfileTag";
    public const string ContainerPort = "ContainerPort";
    public const string ContainerImageTag = "ContainerImageTag";

    /// <summary>The registry an SDK container's image is pushed to; none for the engine on this machine.</summary>
    public const string ContainerRegistry = "ContainerRegistry";

    /// <summary>An SDK container's several tags, the one tag's place where it has more than one.</summary>
    public const string ContainerImageTags = "ContainerImageTags";

    /// <summary>The runtime an SDK container is made for, and the several of an image of more than one platform.</summary>
    public const string ContainerRuntimeIdentifier = "ContainerRuntimeIdentifier";
    public const string ContainerRuntimeIdentifiers = "ContainerRuntimeIdentifiers";

    // A Docker Compose project's (.dcproj).
    public const string DockerComposeBaseFilePath = "DockerComposeBaseFilePath";
    public const string AdditionalComposeFilePaths = "AdditionalComposeFilePaths";
    public const string DockerServiceName = "DockerServiceName";
    public const string DockerServiceUrl = "DockerServiceUrl";
    public const string DockerLaunchAction = "DockerLaunchAction";

    // A publish and its profile's (.pubxml).
    public const string PublishProfile = "PublishProfile";
    public const string LastUsedBuildConfiguration = "LastUsedBuildConfiguration";
    public const string PublishSelfContained = "PublishSelfContained";
    public const string PublishSingleFile = "PublishSingleFile";
    public const string PublishReadyToRun = "PublishReadyToRun";
    public const string PublishTrimmed = "PublishTrimmed";
    public const string PublishAot = "PublishAot";
    public const string PublishDir = "PublishDir";
    public const string PublishUrl = "PublishUrl";
    public const string WebPublishMethod = "WebPublishMethod";
    public const string PublishProtocol = "PublishProtocol";
    public const string PublishProvider = "PublishProvider";

    /// <summary>"true" tells a project's evaluation it is a publish's, as dotnet publish says.</summary>
    public const string IsPublishing = "_IsPublishing";

    /// <summary>The kind of target a profile publishes to, as Visual Studio writes it ("Folder").</summary>
    public const string PublishTargetId = "_TargetId";

    /// <summary>The target that publishes a project as a container image, into the engine.</summary>
    public const string PublishContainer = "PublishContainer";

    /// <summary>The target that works out the SDK container's base image, after which ContainerBaseImage holds it.</summary>
    public const string ComputeContainerBaseImage = "ComputeContainerBaseImage";

    /// <summary>The targets that make a web app's static web assets made by its build again, its scoped CSS's bundle among
    /// them, with no compile.</summary>
    public const string GenerateComputedBuildStaticWebAssets = "GenerateComputedBuildStaticWebAssets";
    public const string ResolveReferencedProjectsStaticWebAssets = "ResolveReferencedProjectsStaticWebAssets";
}
