using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.GodotShader;

namespace Passenger.App.Godot;

/// <summary>Each open shader of a Godot project (a <c>.gdshader</c>, or a <c>.gdshaderinc</c> read alone, as Godot's shader
/// editor reads one) marked with what Godot's front end says of it (<see cref="GodotShaderChecker"/>), every error and
/// warning in Godot's words at its line: as it opens, as its typing pauses, and as another of its project's files changes
/// (an include written). What the check read of its uniforms is kept for the hover card. A scene's own Shader is marked
/// with the scene's other marks (<see cref="GodotSceneMarks"/>). A UI-thread object.</summary>
internal sealed class GodotShaderMarks(GodotSceneIndex index, Func<string, string?> godotOf, GodotShaderChecker checker)
    : OpenFileChecks<GodotShaderMarks.Shader>(Settle)
{
    /// <summary>How long typing pauses before the shader is compiled again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    /// <summary>Who checks a shader, as an agent is told while a check is under way.</summary>
    private const string Checker = "Godot's shader front end";

    public GodotShaderChecker ShaderChecker => checker;

    protected override (string Checker, Shader Kept)? Follow(OpenDocument document) => GodotFileKinds.IsShader(document.Path) ? (Checker, new Shader()) : null;

    /// <summary>What the last check of an open shader read of a uniform; null where it read none of that name.</summary>
    public GodotShaderUniform? UniformOf(OpenDocument document, string name) => KeptOf(document)?.Uniforms?.GetValueOrDefault(name);

    /// <summary>The shader an open shader file is read in for its language, its text as the editor has it: a shader as
    /// itself, an include as the first shader of its project including it (none where no shader does), so its type is
    /// known; with the include's res:// path.</summary>
    public static (GodotShaderUnit Unit, string? Include)? UnitOf(GodotScenes scenes, string res, string text)
    {
        string? Read(string path) => path == res ? text : scenes.Texts.GetValueOrDefault(path) ?? ShaderCodeOf(scenes, path);
        if (!GodotFileKinds.IsShaderInclude(res))
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

    protected override async Task CheckAsync(OpenDocument document, Shader kept)
    {
        int version = document.Text.Version;
        string text = document.Text.Snapshot.ToString();
        if (godotOf(document.Path) is not { } folder)
        {
            Show(document, version, []);
            return;
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        if (scenes.ResPathOf(document.Path) is not { } res)
        {
            Show(document, version, []);
            return;
        }
        GodotShaderUnit unit = await BackgroundWork.Run(() => GodotShaderUnit.Of(res, text, path => path == res ? text : scenes.Texts.GetValueOrDefault(path) ?? ShaderCodeOf(scenes, path)));
        GodotShaderChecked result = await checker.CheckAsync(unit, scenes);
        IReadOnlyList<GodotShaderMark> marks = await BackgroundWork.Run(() => unit.MarksIn(null, result.Messages));
        // A check that read no uniform for an error leaves what the last one read standing until one reads them again.
        if (Show(document, version, [.. marks.Select(m => new Marker(TextRange.FromBounds(m.Start, Math.Max(m.Start, m.End)), KindOf(m), m.Message))])
            && (result.Uniforms.Count > 0 || !result.Messages.Any(m => m.Error || m.Note)))
        {
            kept.Uniforms = result.Uniforms.Select(u => GodotShaderUniform.Of(u.Key, u.Value)).ToDictionary(u => u.Name, StringComparer.Ordinal);
        }
    }

    /// <summary>The kind of mark a compile's mark is.</summary>
    internal static MarkerKind KindOf(GodotShaderMark mark) => mark.Note ? MarkerKind.Info : mark.Error ? MarkerKind.Error : MarkerKind.Warning;

    /// <summary>What is kept of an open shader: what its last compile that read them read of its uniforms, by name.</summary>
    internal sealed class Shader
    {
        public IReadOnlyDictionary<string, GodotShaderUniform>? Uniforms { get; set; }
    }
}
