using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

public sealed partial class CSharpDocument
{
    internal const string GodotDiagnosticSource = "Passenger.Godot";

    /// <summary>The Godot project's folder the file's last pull read it against; null for a file of none.</summary>
    private string? godotFolder;

    /// <summary>Whether the file is of a Godot project, whose pull reads it against the project's scenes.</summary>
    internal bool OfGodot => service.GodotOf?.Invoke(document.Path) is not null;

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
    /// string naming a member a script lacks, or what the project's files do not define. None for a file of no Godot
    /// project, which asks nothing.</summary>
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
            Data = f.Offer is { Any: true } offer ? offer.ToJson() : null,
        })];
    }

    /// <summary>What the file's Godot findings offer to mend them, on a server's line of the text as it stands (any line for
    /// null): each with where its finding now lies and its words.</summary>
    internal IReadOnlyList<(TextRange Range, string Message, GodotOffer Offer)> GodotOffersOn(int? line)
    {
        List<(TextRange, string, GodotOffer)> offers = [];
        foreach ((Diagnostic diagnostic, TextRange range) in diagnostics.PlacedRanges)
        {
            if (diagnostic.Source == GodotDiagnosticSource && (line is not { } on || diagnostic.Range.Start.Line <= on && on <= diagnostic.Range.End.Line)
                && GodotOffer.Of(diagnostic.Data) is { } offer)
            {
                offers.Add((range, diagnostic.Message, offer));
            }
        }
        return offers;
    }
}
