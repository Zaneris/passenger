using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>Each open text scene or resource of a Godot project marked where Godot would not load what it says
/// (<see cref="GodotChecks.SceneFindings"/>), its connections read against the code by Passenger's extension through one of
/// the scene's C# scripts: as it opens, and again a moment after what it is read against changes (its project's scenes as
/// typed or written, the code a pull read, the server loaded). A UI-thread object.</summary>
internal sealed class GodotSceneMarks(GodotSceneIndex index, Func<string, string?> godotOf, Func<CSharp.CSharpService?> csharp)
{
    /// <summary>How long a change waits for the next before the open scenes are read again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>Who checks a scene, as an agent is told while a check is under way.</summary>
    private const string Checker = "Passenger's reading of its scene";

    private readonly HashSet<OpenDocument> documents = [];
    private Debounce? again;

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (GodotFile.IsText(document.Path) && documents.Add(document))
        {
            IdeErrors.Observe(MarkAsync(document));
        }
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        if (documents.Remove(document) && document.Markers.Count > 0)
        {
            document.Markers = [];
        }
    }

    /// <summary>What the open scenes are read against changed: each is read again once changes pause.</summary>
    public void Changed()
    {
        UiThread.Check();
        if (documents.Count > 0)
        {
            (again ??= new Debounce(Settle, () => Task.WhenAll([.. documents.Select(MarkAsync)]))).Restart();
        }
    }

    /// <summary>Its changes stopped for good: the window's session ended.</summary>
    public void Stop() => again?.Stop();

    private async Task MarkAsync(OpenDocument document)
    {
        using OpenDocument.CheckHold hold = document.Checking(Checker);
        int version = document.Text.Version;
        if (godotOf(document.Path) is not { } folder)
        {
            Show(document, version, []);
            return;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        TextSnapshot text = document.Text.Snapshot;
        if (scenes.ResPathOf(document.Path) is not { } res || !scenes.Files.TryGetValue(res, out GodotFile? file) || file.Text != text.ToString())
        {
            // Typed since the scenes were read: the index tells of it once the typing pauses.
            return;
        }
        string? code = await BackgroundWork.Run(() => scenes.Tree(res)?.Below().Select(n => n.Script).Where(GodotChecks.IsCSharp).Select(s => scenes.FileOf(s!))
            .OfType<string>().FirstOrDefault(File.Exists));
        GodotCodeRequest asked = await BackgroundWork.Run(() => GodotChecks.SceneAsks(code ?? document.Path, scenes, res));
        GodotCodeFound found = code is not null && csharp() is { } service ? await service.GodotSceneCodeAsync(asked, CancellationToken.None) ?? GodotCodeFound.None : GodotCodeFound.None;
        IReadOnlyList<GodotChecks.SceneFinding> findings = await BackgroundWork.Run(() => GodotChecks.SceneFindings(scenes, res, asked, found, path => File.Exists(path) || Directory.Exists(path)));
        Show(document, version, [.. findings.Select(f => new Marker(TextRange.FromBounds(f.Start, Math.Max(f.Start, f.End)), f.Error ? MarkerKind.Error : MarkerKind.Warning, f.Message))]);
    }

    /// <summary>A scene's marks, where it is open still as they were read for.</summary>
    private void Show(OpenDocument document, int version, IReadOnlyList<Marker> marks)
    {
        if (documents.Contains(document) && document.Text.Version == version && (marks.Count > 0 || document.Markers.Count > 0))
        {
            document.Markers = marks;
        }
    }
}
