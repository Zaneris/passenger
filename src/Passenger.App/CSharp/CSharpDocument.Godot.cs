using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.App.CSharp;

public sealed partial class CSharpDocument
{
    internal const string GodotDiagnosticSource = "Passenger.Godot";

    /// <summary>The Godot project's folder the file's last pull read it against; null for a file of none.</summary>
    private string? godotFolder;

    /// <summary>A Godot scene or resource changed: pulled again when the file is of a Godot project.</summary>
    internal void GodotChanged()
    {
        if (godotFolder is not null)
        {
            Pull(TimeSpan.Zero);
        }
    }

    /// <summary>The projects were evaluated: pulled again when the file is of another Godot project than its last pull read
    /// (its project's evaluation landing after the file was opened).</summary>
    internal void GodotProjectChanged()
    {
        if (Lsp is not null && service.GodotOf?.Invoke(document.Path) != godotFolder)
        {
            Pull(TimeSpan.Zero);
        }
    }

    /// <summary>The file read against the scenes its scripts run in (<see cref="GodotChecks.Findings"/>): a GetNode path no node
    /// answers, or whose node is not of the type asked for, each an error naming the scene and line; a res:// path no file
    /// answers; a scene's connection to a method its script lacks, or one taking other parameters than its signal gives; a
    /// string naming a member a script lacks. None for a file of no Godot project, which asks nothing.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> GodotFindingsAsync(CancellationToken cancel)
    {
        godotFolder = Lsp is null ? null : service.GodotOf?.Invoke(document.Path);
        if (godotFolder is null || await service.GodotAsync(document.Path, cancel) is not { } read)
        {
            return [];
        }
        IReadOnlyList<GodotChecks.Finding> findings = await BackgroundWork.Run(
            () => GodotChecks.Findings(read.Scenes, read.Asked, read.Found, path => File.Exists(path) || Directory.Exists(path)), cancel);
        service.GodotCodeRead?.Invoke();
        return [.. findings.Select(f => new Diagnostic
        {
            Range = new LspRange(new LspPosition(f.At.Line, f.At.Character), new LspPosition(f.End.Line, f.End.Character)),
            Severity = f.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            Source = GodotDiagnosticSource,
            Message = f.Message,
        })];
    }
}
