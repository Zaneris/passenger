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

    /// <summary>What the scenes say beside the code as the last pull read them, for the text's version it read: the marks
    /// leading into the scenes and the hints after GetNode paths (<see cref="GodotBeside"/>); null for a file of none.</summary>
    private (int Version, IReadOnlyList<Editors.SceneMark> Marks, IReadOnlyList<Editor.InlayHint> Hints)? godotBeside;

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
        godotBeside = null;
        int version = document.Text.Version;
        TextSnapshot text = document.Text.Snapshot;
        if (godotFolder is null || await service.GodotAsync(document.Path, cancel) is not { } read)
        {
            return [];
        }
        IReadOnlyList<GodotChecks.Finding> findings = await BackgroundWork.Run(
            () => GodotChecks.Findings(read.Scenes, read.Asked, read.Found, path => File.Exists(path) || Directory.Exists(path)), cancel);
        godotBeside = await BackgroundWork.Run(() => Beside(version, text, read), cancel);
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

    /// <summary>The scenes' marks and hints beside a text's code, placed in it: a class's mark on its name's line, a
    /// handler's on its, a GetNode path's hint after its closing quote.</summary>
    private static (int, IReadOnlyList<Editors.SceneMark>, IReadOnlyList<Editor.InlayHint>) Beside(int version, TextSnapshot text, GodotRead read)
    {
        ServerLines lines = ServerLines.Of(text);
        List<Editors.SceneMark> marks = [];
        foreach (GodotBeside.Mark mark in GodotBeside.Classes(read.Scenes, read.Found).Concat(GodotBeside.Handlers(read.Scenes, read.Asked, read.Found)))
        {
            int line = lines.PositionOf(lines.OffsetOf(new LspPosition(mark.At.Line, mark.At.Character))).Line;
            if (marks.FindIndex(m => m.Line == line) is var known and >= 0)
            {
                marks[known] = marks[known] with { Leads = [.. marks[known].Leads, .. mark.Leads] };
            }
            else
            {
                marks.Add(new Editors.SceneMark(line, mark.Leads));
            }
        }
        List<Editor.InlayHint> hints = [.. GodotBeside.Hints(read.Scenes, read.Found).Select(h => new Editor.InlayHint(
            Math.Min(text.Length, lines.OffsetOf(new LspPosition(h.Quote.Line, h.Quote.Character)) + 1), $"→ {h.Words}", PadLeft: true, Kind: Editor.InlayHintKind.Value))];
        return (version, [.. marks.OrderBy(m => m.Line)], hints);
    }

    /// <summary>The scenes' marks the last pull read, for the text as it stands (none for a file of no Godot project).</summary>
    private void ShowGodotMarks()
    {
        document.SceneMarks = godotBeside is { } beside && beside.Version == document.Text.Version ? beside.Marks : [];
    }

    /// <summary>The scenes' hints the last pull read for a version of the text, beside the server's; none for another.</summary>
    private IReadOnlyList<Editor.InlayHint> GodotHints(int version) => godotBeside is { } beside && beside.Version == version ? beside.Hints : [];

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
