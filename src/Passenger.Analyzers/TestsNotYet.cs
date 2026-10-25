using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Passenger.Analyzers;

/// <summary>The test classes PSG0004 and PSG0006 do not look at yet (#79), by the name of the outermost type the code is
/// in: each name goes as its class's waits and thread-pool work move to Ui.Await, an await or Waiting.Apart.</summary>
internal static class TestsNotYet
{
    private const string Assembly = "Passenger.Tests";

    private static readonly ImmutableHashSet<string> Types = ImmutableHashSet.Create(StringComparer.Ordinal,
        "AspireApp",
        "AspireContainersTests",
        "AspireExternalDataTests",
        "AspireOrchestrationTests",
        "AspireStandInTests",
        "AspireTests",
        "AssetPreviewTests",
        "AvaloniaApp",
        "BindingErrorsTests",
        "BlazorApp",
        "BranchesTests",
        "BuildRunTests",
        "CodeAssistTests",
        "ConflictsTests",
        "ContainerDebuggingTests",
        "ContainerPrograms",
        "ContainerRunDebuggingTests",
        "ContainerRunProfilingTests",
        "ContainerRunsTests",
        "ContainerRunStopsTests",
        "ContainerServicesTests",
        "ContainersTests",
        "ContainerTelemetryTests",
        "DebuggerTests",
        "DebugSession",
        "DebugSessionsTests",
        "DiffWordsTests",
        "DiscordFolders",
        "Docker",
        "DockerFilesTests",
        "EditingBehaviourTests",
        "EditingSessionTests",
        "EngineChecksTests",
        "EslintPrettierTests",
        "ExplorerFilesTests",
        "FileTests",
        "FindInFilesTests",
        "IdeErrorsTests",
        "LiveRequestsTests",
        "LocalHistoryTests",
        "LongFileAssistTests",
        "LongListsTests",
        "MemorySnapshotsTests",
        "NativeStacks",
        "NewSolutionTests",
        "PackagesTests",
        "PathsTests",
        "ProfilerTests",
        "ProjectEvaluationTests",
        "PublishTests",
        "RazorDebuggingTests",
        "Restore",
        "RestoredFolders",
        "SelfDebuggingTests",
        "SharedProject",
        "SolutionFoldersTests",
        "SolutionViewTests",
        "StandInAppHost",
        "StandInHttp",
        "TemplateFamiliesTests",
        "TerminalTests",
        "TestFolders",
        "TestGit",
        "TestingPlatformDebuggingTests",
        "UnitTestsTests",
        "UpdatesTests",
        "VcsTests",
        "WebLanguagesTests",
        "WelcomeTests",
        "WpfApp",
        "WpfPreviewTests",
        "XamlTests");

    /// <summary>Whether the symbol is in one of those classes.</summary>
    public static bool Contains(ISymbol? symbol)
    {
        if (symbol?.ContainingAssembly?.Name != Assembly)
        {
            return false;
        }
        INamedTypeSymbol? outermost = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        while (outermost?.ContainingType is { } outer)
        {
            outermost = outer;
        }
        return outermost is not null && Types.Contains(outermost.Name);
    }
}
