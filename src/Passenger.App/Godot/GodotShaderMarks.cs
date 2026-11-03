using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>Each open shader of a Godot project (a <c>.gdshader</c>, or a <c>.gdshaderinc</c> through the first shader
/// including it) marked with what Godot's own compiler says of it (<see cref="GodotShaderCompiler"/>), Godot's words at
/// their lines: as it opens, as its typing pauses, and as another of its project's files changes (an include written).
/// Where Godot cannot compile it, its banner says why. What the compile read of its uniforms is kept for the hover card.
/// A scene's own Shader is marked with the scene's other marks (<see cref="GodotSceneMarks"/>). A UI-thread object.</summary>
internal sealed class GodotShaderMarks(GodotSceneIndex index, Func<string, string?> godotOf, GodotShaderCompiler compiler)
{
    /// <summary>How long typing pauses before the shader is compiled again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>Who checks a shader, as an agent is told while a check is under way.</summary>
    private const string Checker = "Godot's shader compiler";

    private readonly Dictionary<OpenDocument, (Debounce Pause, EventHandler<DocumentChangedEventArgs> Typed)> documents = [];

    /// <summary>What each open shader's last compile that read them read of its uniforms, by name.</summary>
    private readonly Dictionary<OpenDocument, IReadOnlyDictionary<string, GodotShaderUniform>> uniforms = [];
    private Debounce? again;

    public GodotShaderCompiler Compiler => compiler;

    /// <summary>Whether a file is a shader or a shader include, by its name.</summary>
    public static bool IsShader(string path) =>
        path.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase);

    public void Attach(OpenDocument document)
    {
        UiThread.Check();
        if (!IsShader(document.Path) || documents.ContainsKey(document))
        {
            return;
        }
        Debounce pause = new(Settle, () => MarkAsync(document), () => document.Checking(Checker));
        EventHandler<DocumentChangedEventArgs> typed = (_, _) => pause.Restart();
        documents[document] = (pause, typed);
        compiler.Opened();
        document.Text.Changed += typed;
        IdeErrors.Observe(MarkAsync(document));
    }

    public void Detach(OpenDocument document)
    {
        UiThread.Check();
        uniforms.Remove(document);
        if (documents.Remove(document, out (Debounce Pause, EventHandler<DocumentChangedEventArgs> Typed) gone))
        {
            document.Text.Changed -= gone.Typed;
            gone.Pause.Stop();
            compiler.Closed();
            if (document.Markers.Count > 0)
            {
                document.Markers = [];
            }
        }
    }

    /// <summary>A file of a project came, went or was written: each open shader compiled again once changes pause.</summary>
    public void Changed()
    {
        UiThread.Check();
        if (documents.Count > 0)
        {
            (again ??= new Debounce(Settle, () => Task.WhenAll([.. documents.Keys.Select(MarkAsync)]))).Restart();
        }
    }

    /// <summary>Its changes stopped for good: the window's session ended.</summary>
    public void Stop()
    {
        again?.Stop();
        foreach ((Debounce pause, _) in documents.Values)
        {
            pause.Stop();
        }
    }

    /// <summary>What Godot's last compile of an open shader read of a uniform; null where it read none of that name.</summary>
    public GodotShaderUniform? UniformOf(OpenDocument document, string name) =>
        uniforms.TryGetValue(document, out IReadOnlyDictionary<string, GodotShaderUniform>? read) ? read.GetValueOrDefault(name) : null;

    /// <summary>The shader Godot is given for an open shader file, its text as the editor has it: a shader as itself, an
    /// include as the first shader of its project including it (none where no shader does); with the include's res:// path.</summary>
    public static (GodotShaderUnit Unit, string? Include)? UnitOf(GodotScenes scenes, string res, string text)
    {
        string? Read(string path) => path == res ? text : scenes.Texts.GetValueOrDefault(path) ?? ShaderCodeOf(scenes, path);
        if (!res.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase))
        {
            return (GodotShaderUnit.Of(res, text, Read), null);
        }
        foreach (string shader in scenes.Defined.Shaders)
        {
            if (Read(shader) is { } code && GodotShaderUnit.Of(shader, code, Read) is { } unit && unit.Includes.ContainsKey(res))
            {
                return (unit, res);
            }
        }
        return null;
    }

    /// <summary>A Shader's code a scene or resource writes, by its res:// path (a scene's own by its file and id); null for none.</summary>
    private static string? ShaderCodeOf(GodotScenes scenes, string shader)
    {
        string[] parts = shader.Split("::");
        if (!scenes.Files.TryGetValue(parts[0], out GodotFile? file))
        {
            return null;
        }
        GodotSection? section = parts.Length == 2 ? file.Sections.FirstOrDefault(s => s.Tag == "sub_resource" && s.Text("id") == parts[1]) : file.Resource;
        return section?.Property("code")?.Value.Text;
    }

    private async Task MarkAsync(OpenDocument document)
    {
        using OpenDocument.CheckHold hold = document.Checking(Checker);
        int version = document.Text.Version;
        string text = document.Text.Snapshot.ToString();
        if (godotOf(document.Path) is not { } folder)
        {
            Show(document, version, [], null);
            return;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        if (scenes.ResPathOf(document.Path) is not { } res || await BackgroundWork.Run(() => UnitOf(scenes, res, text)) is not ({ } unit, var include))
        {
            Show(document, version, [], null);
            return;
        }
        if (await compiler.CompileAsync(unit, scenes.Defined.RenderingMethod) is not { } compiled)
        {
            Show(document, version, [], compiler.Problem is { } problem ? $"Godot's compiler does not check this shader: {problem}" : null);
            return;
        }
        IReadOnlyList<GodotShaderMark> marks = await BackgroundWork.Run(() => unit.MarksIn(include, compiled.Messages));
        if (Show(document, version, [.. marks.Select(m => new Marker(TextRange.FromBounds(m.Start, Math.Max(m.Start, m.End)), m.Error ? MarkerKind.Error : MarkerKind.Warning, m.Message))], null))
        {
            // A compile stopped at an error reads no uniform: what the last one read stands until one reads them again.
            if (compiled.Uniforms.Count > 0 || !compiled.Messages.Any(m => m.Error))
            {
                uniforms[document] = compiled.Uniforms.DistinctBy(u => u.Name).ToDictionary(u => u.Name, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>A shader's marks and banner, where it is open still as they were made for; whether they stand for its text.</summary>
    private bool Show(OpenDocument document, int version, IReadOnlyList<Marker> marks, string? notice)
    {
        if (!documents.ContainsKey(document) || document.Text.Version != version)
        {
            return false;
        }
        if (marks.Count > 0 || document.Markers.Count > 0)
        {
            document.Markers = marks;
        }
        if (document.Notice != notice)
        {
            document.Notice = notice;
        }
        return true;
    }
}
