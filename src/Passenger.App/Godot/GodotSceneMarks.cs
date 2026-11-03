using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>Each open text scene or resource of a Godot project marked where Godot would not load what it says
/// (<see cref="GodotChecks.SceneFindings"/>), its connections read against the code by Passenger's extension through one of
/// the scene's C# scripts, or where it has none, against the engine's classes as the Godot found's GodotSharp declares them
/// (<see cref="GodotChecks.EngineAnswers"/>): as it opens, again a moment after what it is read against changes (its
/// project's scenes as typed or written, the code a pull read, the server loaded), and at once as a C# file of its project
/// is written on disk, read once the server holds as the disk has them the scene's scripts heard changed
/// (<see cref="CodeAsync"/>); and each Shader it writes itself marked with what Godot's own compiler says of its code
/// (<see cref="ShaderMarksAsync"/>). A UI-thread object.</summary>
internal sealed class GodotSceneMarks(GodotSceneIndex index, Func<string, string?> godotOf, Func<CSharp.CSharpService?> csharp, CommandHistory commands)
{
    /// <summary>The Godot the person's settings name (their command line for it), whose GodotSharp tells the engine's classes.</summary>
    public Func<string?> Godot { get; init; } = () => null;

    /// <summary>Godot's shader compiler, which a scene's own Shaders are compiled by; none leaves them unmarked.</summary>
    public GodotShaderCompiler? Shaders { get; init; }

    /// <summary>How long a change waits for the next before the open scenes are read again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>Who checks a scene, as an agent is told while a check is under way.</summary>
    private const string Checker = "Passenger's reading of its scene";

    private readonly HashSet<OpenDocument> documents = [];

    /// <summary>Each open scene's own typing, which holds its check from the first key until it is read again, so an
    /// agent's diagnostics waits for the marks of the text as it stands.</summary>
    private readonly Dictionary<OpenDocument, (Debounce Pause, EventHandler<DocumentChangedEventArgs> Typed)> typing = [];

    /// <summary>What each open scene's marks offer to mend them, by their words.</summary>
    private readonly Dictionary<OpenDocument, IReadOnlyDictionary<string, GodotOffer>> offers = [];
    private Debounce? again;

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (GodotFile.IsText(document.Path) && documents.Add(document))
        {
            Debounce pause = new(Settle, () => MarkAsync(document), () => document.Checking(Checker));
            EventHandler<DocumentChangedEventArgs> typed = (_, _) => pause.Restart();
            typing[document] = (pause, typed);
            document.Text.Changed += typed;
            IdeErrors.Observe(MarkAsync(document));
        }
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        offers.Remove(document);
        if (typing.Remove(document, out (Debounce Pause, EventHandler<DocumentChangedEventArgs> Typed) typed))
        {
            document.Text.Changed -= typed.Typed;
            typed.Pause.Stop();
        }
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

    /// <summary>A C# file of a project was written on disk: each open scene read again now, once the server has read it.</summary>
    public void CodeWritten()
    {
        UiThread.Check();
        if (documents.Count > 0)
        {
            IdeErrors.Observe(Task.WhenAll([.. documents.Select(MarkAsync)]));
        }
    }

    /// <summary>Its changes stopped for good: the window's session ended.</summary>
    public void Stop()
    {
        again?.Stop();
        foreach ((Debounce pause, _) in typing.Values)
        {
            pause.Stop();
        }
    }

    private async Task MarkAsync(OpenDocument document)
    {
        using OpenDocument.CheckHold hold = document.Checking(Checker);
        int version = document.Text.Version;
        if (godotOf(document.Path) is not { } folder)
        {
            if (Show(document, version, []))
            {
                offers.Remove(document);
            }
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
        string? setting = Godot();
        GodotCodeFound found = code is not null && csharp() is { } service ? await CodeAsync(service, scenes, asked) ?? GodotCodeFound.None
            : await BackgroundWork.Run(() => Run.GodotLaunch.Found(setting)?.Sharp is { } sharp && Path.Combine(sharp, "Api", "Release", "GodotSharp.dll") is var api && File.Exists(api)
                ? GodotChecks.EngineAnswers(asked, GodotEngineClasses.Of(api)) : GodotCodeFound.None);
        IReadOnlyList<GodotChecks.SceneFinding> findings = await BackgroundWork.Run(() => GodotChecks.SceneFindings(scenes, res, asked, found, path => File.Exists(path) || Directory.Exists(path)));
        IReadOnlyList<Marker> shaders = await ShaderMarksAsync(scenes, res, file);
        if (Show(document, version, [.. findings.Select(f => new Marker(TextRange.FromBounds(f.Start, Math.Max(f.Start, f.End)), f.Error ? MarkerKind.Error : MarkerKind.Warning, f.Message)), .. shaders]))
        {
            offers[document] = findings.Where(f => f.Offer is { Any: true }).DistinctBy(f => f.Message).ToDictionary(f => f.Message, f => f.Offer!, StringComparer.Ordinal);
        }
    }

    /// <summary>What the extension answers of the code for a scene's connections, asked once the server holds as the disk
    /// has them the scene's C# scripts heard changed on disk (a fix's write of one not open), as a command waits for them
    /// (<see cref="Patience"/>), within its limit.</summary>
    private async Task<GodotCodeFound?> CodeAsync(CSharp.CSharpService service, GodotScenes scenes, GodotCodeRequest asked)
    {
        HashSet<string> scripts = new(asked.Scripts.Select(scenes.FileOf).OfType<string>(), PathNames.Comparer);
        (GodotCodeFound? found, _) = await Patience.RunAsync(commands, () => service.Session, async patience =>
        {
            // Behind's words are only told while time is left: once it is spent, the code is asked of as the server holds it.
            if (service.Session is { } session && session.Server.WatchedFiles.Changing().Paths.Where(scripts.Contains).ToList() is { Count: > 0 } unread
                && patience.Behind(unread, paths => session.HeldTextsAsync(paths), Checker, string.Empty).Length > 0)
            {
                return ((GodotCodeFound?)null, Behind: true);
            }
            return (await service.GodotSceneCodeAsync(asked, CancellationToken.None), Behind: false);
        }, read => read.Behind, _ => { }, CancellationToken.None);
        return found;
    }

    /// <summary>The marks Godot's compiler gives each Shader a scene or resource writes itself (a sub_resource's <c>code</c>,
    /// or a Shader resource's own), each place in its code's value mapped to where the file writes it, escapes and all.</summary>
    private async Task<IReadOnlyList<Marker>> ShaderMarksAsync(GodotScenes scenes, string res, GodotFile file)
    {
        if (Shaders is not { } compiler)
        {
            return [];
        }
        List<Marker> marks = [];
        bool resource = file.Header?.Text("type") == "Shader";
        foreach (GodotSection section in file.Sections.Where(s => s.Tag == "sub_resource" && s.Text("type") == "Shader" || resource && s.Tag == "resource"))
        {
            if (section.Property("code")?.Value is not { Text: { } code } value)
            {
                continue;
            }
            string shader = section.Tag == "sub_resource" ? $"{res}::{section.Text("id")}" : res;
            GodotShaderUnit unit = await BackgroundWork.Run(() => GodotShaderUnit.Of(shader, code, path => scenes.Texts.GetValueOrDefault(path)));
            if (await compiler.CompileAsync(unit) is not { } compiled)
            {
                return marks;
            }
            marks.AddRange(await BackgroundWork.Run(() =>
            {
                int[] places = GodotStringPlaces.Of(file.Text, value.TextStart, value.TextEnd);
                return unit.MarksIn(null, compiled.Messages).Select(m =>
                {
                    (int start, int end) = GodotStringPlaces.RangeOf(places, m.Start, m.End);
                    return new Marker(TextRange.FromBounds(start, end), m.Error ? MarkerKind.Error : MarkerKind.Warning, m.Message);
                }).ToList();
            }));
        }
        return marks;
    }

    /// <summary>What an open scene's marks on an offset's line offer to mend them (any line for null), each with where its
    /// mark now lies and its words.</summary>
    public IReadOnlyList<(TextRange Range, string Message, GodotOffer Offer)> OffersOn(OpenDocument document, int? offset)
    {
        UiThread.Check();
        if (!offers.TryGetValue(document, out IReadOnlyDictionary<string, GodotOffer>? kept))
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        int? line = offset is { } at ? text.GetLineFromOffset(Math.Clamp(at, 0, text.Length)) : null;
        return [.. document.Markers.Where(m => kept.ContainsKey(m.Message) && (line is not { } on || m.Range.Start <= text.Length && text.GetLineFromOffset(m.Range.Start) == on))
            .Select(m => (m.Range, m.Message, kept[m.Message]))];
    }

    /// <summary>A scene's marks, where it is open still as they were read for; whether they stand for its text.</summary>
    private bool Show(OpenDocument document, int version, IReadOnlyList<Marker> marks)
    {
        if (!documents.Contains(document) || document.Text.Version != version)
        {
            return false;
        }
        if (marks.Count > 0 || document.Markers.Count > 0)
        {
            document.Markers = marks;
        }
        return true;
    }
}
