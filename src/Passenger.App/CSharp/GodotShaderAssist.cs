using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>A Godot shader (<c>.gdshader</c>, <c>.gdshaderinc</c>) as Godot's language reads it: completion lists the
/// built-ins the processor function the caret is in reads, with its stage functions and the language's functions, the
/// render modes, stencil modes and hints where those are written, and what the shader and its includes declare; each as
/// the Godot found takes them (<see cref="GodotShaderChecker.TableAsync"/>). The hover card says what a name is, a uniform
/// with what Godot's compile read of it; Go to Declaration goes to a declaration, in an include too, and from an
/// <c>#include</c> to its file; Find Usages of a uniform lists its uses in the shader, the scenes setting it and the C#
/// naming it. Context Help browses a built-in's place in Godot's shading reference (<see cref="HelpAsync"/>). A scene's or
/// resource's own Shader completes, hovers, goes to declarations and helps as a file does, its places mapped through its
/// string's escapes.</summary>
internal sealed class GodotShaderAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation, CSharpAssist csharp) : LanguageAssist
{
    /// <summary>The characters an include's path holds beyond a name's, which narrow its list rather than close it.</summary>
    private const string PathCharacters = "/.:-";

    /// <summary>The words each item says beside the list, by the item.</summary>
    private readonly Dictionary<CompletionItem, string> described = [];

    private static bool IsShader(OpenDocument? document) => document is not null && GodotShaderMarks.IsShader(document.Path);

    /// <summary>A name's first letter, or a mode list's, a hint list's or an include path's mark, opens the list.</summary>
    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } shown)
        {
            return false;
        }
        int caret = context.Caret;
        SceneCode? scene = IsShader(shown) ? null : SceneCodeAt(shown, caret);
        if (!IsShader(shown) && scene is null)
        {
            return false;
        }
        char last = text[^1];
        GodotShaderCode code = GodotShaderCode.Read(scene?.Code ?? shown.Text.Snapshot.ToString());
        GodotShaderSpot spot = code.SpotAt(scene?.At ?? caret);
        if (scene is not null && spot == GodotShaderSpot.Include)
        {
            return true;
        }
        bool starts = AssistContext.IsIdentifierChar(last) && !char.IsDigit(last) && context.WordStart(caret) == caret - 1;
        bool marks = last is ',' or ':' && spot is GodotShaderSpot.RenderModes or GodotShaderSpot.StencilModes or GodotShaderSpot.Hints
            || last is '"' or '/' && spot == GodotShaderSpot.Include;
        if ((starts && spot is not (GodotShaderSpot.None or GodotShaderSpot.Member)) || marks)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown)
        {
            return false;
        }
        int caret = context.Caret;
        SceneCode? scene = IsShader(shown) ? null : SceneCodeAt(shown, caret);
        if (!IsShader(shown) && scene is null)
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        Shader shader = scene is null ? await ShaderAsync(shown, asked.ToString()) : await SceneShaderAsync(shown, scene);
        int at = scene?.At ?? caret;
        GodotShaderSpot spot = shader.Code.SpotAt(at);
        // A scene's string would need an include's quotes escaped, so its includes are written by hand.
        if (spot is GodotShaderSpot.None or GodotShaderSpot.Member || scene is not null && spot == GodotShaderSpot.Include)
        {
            completion.Close();
            return true;
        }
        GodotShaderTable table = await TableAsync(shader);
        int start = context.WordStart(caret);
        string tail = string.Empty;
        List<(CompletionItem Item, string Said)> items;
        if (spot == GodotShaderSpot.Include && shader.Code.Includes.FirstOrDefault(i => i.Start <= caret && caret <= i.End) is { } include)
        {
            start = include.Start;
            tail = caret < include.End ? asked.GetText(caret, include.End - caret) : string.Empty;
            items = await BackgroundWork.Run(() => IncludeItems(shader));
        }
        else
        {
            items = Items(table, shader, spot, at);
        }
        if (cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        if (items.Count == 0)
        {
            completion.Close();
            return true;
        }
        described.Clear();
        foreach ((CompletionItem item, string said) in items)
        {
            described[item] = said;
        }
        completion.Open(new CodeCompletion.Completion([.. items.Select(i => i.Item)], start, [], false)
        {
            Own = this,
            WordCharacters = spot == GodotShaderSpot.Include ? PathCharacters : string.Empty,
            Tail = tail,
        });
        return true;
    }

    /// <summary>What completion offers at a place of the shader, each with the words beside the list.</summary>
    private static List<(CompletionItem Item, string Said)> Items(GodotShaderTable table, Shader shader, GodotShaderSpot spot, int caret)
    {
        List<(CompletionItem, string)> items = [];
        string? type = shader.Type;
        GodotShaderType? kind = table.TypeOf(type);
        void Add(string label, CompletionItemKind itemKind, string detail, string said, int rank) =>
            items.Add((new CompletionItem { Label = label, Kind = itemKind, Detail = detail, FilterText = label, SortText = $"{rank}{label}" }, said));
        switch (spot)
        {
            case GodotShaderSpot.ShaderType:
                foreach (GodotShaderType each in table.Types)
                {
                    Add(each.Name, CompletionItemKind.Keyword, "shader type", $"shader_type {each.Name};", 0);
                }
                return items;
            case GodotShaderSpot.RenderModes:
                foreach (string mode in kind?.Modes.SelectMany(m => m.Written) ?? [])
                {
                    Add(mode, CompletionItemKind.EnumMember, "render mode", $"A render mode of {type} shaders", 0);
                }
                return items;
            case GodotShaderSpot.StencilModes:
                foreach (string mode in kind?.StencilModes.SelectMany(m => m.Written) ?? [])
                {
                    Add(mode, CompletionItemKind.EnumMember, "stencil mode", $"A stencil mode of {type} shaders", 0);
                }
                return items;
            case GodotShaderSpot.Hints:
                foreach (string hint in type is null ? table.Types.SelectMany(t => table.HintsOf(t.Name)).Distinct() : table.HintsOf(type))
                {
                    Add(hint, CompletionItemKind.Keyword, "hint", $"A uniform hint{(type is null ? string.Empty : $" of {type} shaders")}", 0);
                }
                return items;
        }
        string? function = shader.Code.FunctionAt(caret)?.Name;
        HashSet<string> named = [];
        foreach (GodotShaderDeclaration declared in shader.Code.SeenAt(caret).Concat(shader.Included).Where(d => d.Start > caret || d.End < caret))
        {
            if (named.Add(declared.Name) && (spot == GodotShaderSpot.Body || declared.Kind is not (GodotShaderDeclared.Parameter or GodotShaderDeclared.Local)))
            {
                Add(declared.Name, ItemKind(declared.Kind), declared.Type ?? Words(declared.Kind), declared.Written, 0);
            }
        }
        if (type is not null)
        {
            foreach ((GodotShaderBuiltIn builtIn, string scope) in table.BuiltInsIn(type, spot == GodotShaderSpot.Body ? function : null))
            {
                if (named.Add(builtIn.Name))
                {
                    Add(builtIn.Name, builtIn.Constant ? CompletionItemKind.Constant : CompletionItemKind.Variable, builtIn.Type, Said(builtIn, scope, type), 1);
                }
            }
            if (spot == GodotShaderSpot.Body && function is not null)
            {
                foreach (GodotShaderCall call in table.StageFunctionsIn(type, function).Where(c => named.Add(c.Name)))
                {
                    Add(call.Name, CompletionItemKind.Function, call.Returns, $"{call.Signature}\nA function {type}'s {function}() may call", 1);
                }
            }
        }
        if (spot == GodotShaderSpot.Body)
        {
            foreach (IGrouping<string, GodotShaderCall> overloads in table.Functions.GroupBy(f => f.Name).Where(g => named.Add(g.Key)))
            {
                Add(overloads.Key, CompletionItemKind.Function, overloads.First().Returns, string.Join('\n', overloads.Select(o => o.Signature).Distinct().Take(6)), 2);
            }
        }
        foreach (string word in GodotShaderCode.BuiltInTypes.Concat(GodotShaderCode.Keywords).Where(named.Add))
        {
            Add(word, CompletionItemKind.Keyword, GodotShaderCode.BuiltInTypes.Contains(word) ? "type" : "keyword", word, 3);
        }
        return items;
    }

    /// <summary>The shader includes of the shader's project, by res:// path, but the file itself.</summary>
    private List<(CompletionItem Item, string Said)> IncludeItems(Shader shader)
    {
        if (shader.Folder is not { } folder || context.GodotScenes?.Invoke() is not { } index)
        {
            return [];
        }
        return [.. index.FilesUnder(folder).Where(f => f.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase) && !PathNames.Same(f, shader.Path))
            .Select(f => GodotResPaths.Of(folder, f)).OfType<string>().Order(StringComparer.Ordinal)
            .Select(res => (new CompletionItem { Label = res, Kind = CompletionItemKind.File, Detail = "shader include", FilterText = res, SortText = res }, $"#include \"{res}\""))];
    }

    private static CompletionItemKind ItemKind(GodotShaderDeclared kind) => kind switch
    {
        GodotShaderDeclared.Function => CompletionItemKind.Function,
        GodotShaderDeclared.Struct => CompletionItemKind.Struct,
        GodotShaderDeclared.Constant or GodotShaderDeclared.Define => CompletionItemKind.Constant,
        GodotShaderDeclared.Uniform => CompletionItemKind.Field,
        _ => CompletionItemKind.Variable,
    };

    /// <summary>What a declaration is, as words.</summary>
    private static string Words(GodotShaderDeclared kind) => kind switch
    {
        GodotShaderDeclared.Define => "define",
        GodotShaderDeclared.Struct => "struct",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>What a built-in is, as the card says it.</summary>
    private static string Said(GodotShaderBuiltIn builtIn, string scope, string type)
    {
        string where = scope switch
        {
            GodotShaderTable.Global => $"A built-in of every processor function of {type} shaders",
            GodotShaderTable.Constants => $"A constant of {type} shaders",
            _ => $"A built-in of {type} shaders' {scope}()",
        };
        return $"{(builtIn.Constant ? "const " : string.Empty)}{builtIn.Type} {builtIn.Name}\n{where}{(builtIn.Constant || scope == GodotShaderTable.Constants ? ", read only" : ", read and written")}";
    }

    /// <summary>The name, or the include's path, in place of what was typed from <paramref name="start"/> and the rest of
    /// the name (or path) after the caret.</summary>
    public override void Commit(CompletionItem item, int start)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int caret = context.Caret;
        int end = caret;
        if (IsShader(context.Editors.Active) && GodotShaderCode.Read(text.ToString()).Includes.FirstOrDefault(i => i.Start <= caret && caret <= i.End) is { } include)
        {
            end = include.End;
        }
        else
        {
            while (end < text.Length && AssistContext.IsIdentifierChar(text[end]))
            {
                end++;
            }
        }
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), item.Label)], SelectionSet.Single(start + item.Label.Length));
    }

    public override string? Describe(CompletionItem item) => described.GetValueOrDefault(item);

    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        SceneCode? scene = IsShader(document) ? null : SceneCodeAt(document, offset);
        if (!IsShader(document) && scene is null)
        {
            return false;
        }
        Shader shader = scene is null ? await ShaderAsync(document, document.Text.Snapshot.ToString()) : await SceneShaderAsync(document, scene);
        GodotShaderTable table = await TableAsync(shader, cancel);
        cancel.ThrowIfCancellationRequested();
        if (GodotShaderNames.At(table, shader.Code, shader.Type, scene?.At ?? offset, shader.Others) is not { } name)
        {
            return false;
        }
        switch (name.Kind)
        {
            case GodotShaderNameKind.BuiltIn when name.BuiltIn is { } builtIn:
                string[] said = Said(builtIn, name.Scope ?? string.Empty, shader.Type ?? "these").Split('\n');
                card.AddCode(said[0]);
                card.AddText(said[1]);
                break;
            case GodotShaderNameKind.StageFunction or GodotShaderNameKind.Function:
                card.AddCode(string.Join('\n', name.Calls.Select(c => c.Signature).Distinct().Take(8)));
                card.AddText(name.Kind == GodotShaderNameKind.StageFunction ? $"A function {shader.Type} shaders' {name.Scope}() may call" : "A built-in function of Godot's shading language");
                break;
            case GodotShaderNameKind.RenderMode:
                card.AddCode($"render_mode {name.Name};");
                card.AddText($"A render mode of {shader.Type} shaders");
                break;
            case GodotShaderNameKind.StencilMode:
                card.AddCode($"stencil_mode {name.Name};");
                card.AddText($"A stencil mode of {shader.Type} shaders");
                break;
            case GodotShaderNameKind.Hint:
                card.AddText($"A uniform hint of Godot's shading language");
                break;
            case GodotShaderNameKind.Declared when name.Declaration is { } declaration:
                card.AddCode(declaration.Written);
                string where = name.Include ?? shader.Res ?? Path.GetFileName(shader.Path);
                if (declaration.Kind == GodotShaderDeclared.Uniform)
                {
                    GodotShaderUniform? uniform = scene is null ? shader.Shaders?.UniformOf(document, declaration.Name)
                        : shader.Res is { } res ? shader.Shaders?.ShaderChecker.UniformOf(res, declaration.Name) : null;
                    card.AddText(uniform is { } read
                        ? $"A uniform of {where}, which Godot reads as {read.Described}" : $"A uniform of {where}");
                }
                else if (name.Include is { } include)
                {
                    card.AddText($"Declared in {include}");
                }
                break;
        }
        return true;
    }

    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Editors.Active is not { } shown || !InShader(shown, offset))
        {
            return false;
        }
        Declared? declared = await DeclaredAsync(shown, offset);
        if (declared is null)
        {
            context.Say(Refusals.NoDeclaration);
        }
        else if (context.Editors.Active == shown)
        {
            navigation.Go(declared, offset);
        }
        return true;
    }

    /// <summary>Where a name of the shader is declared (the shader's own, or an include's), or the file an <c>#include</c>
    /// names; null for a built-in, which only Godot declares.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        SceneCode? scene = IsShader(document) ? null : SceneCodeAt(document, offset);
        if (!IsShader(document) && scene is null)
        {
            return null;
        }
        Shader shader = scene is null ? await ShaderAsync(document, document.Text.Snapshot.ToString()) : await SceneShaderAsync(document, scene);
        int at = scene?.At ?? offset;
        if (shader.Code.Includes.FirstOrDefault(i => i.Start <= at && at <= i.End) is { } include)
        {
            string res = GodotResPaths.Resolved(include.Path, shader.Res ?? string.Empty);
            return shader.Scenes?.FileOf(res) is { } file && File.Exists(file)
                ? new Declared([new LspLocation(DocumentUris.FromPath(file), new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)))], string.Empty)
                : new Declared([], $"No file {res} is in the Godot project");
        }
        GodotShaderTable table = await TableAsync(shader);
        if (GodotShaderNames.At(table, shader.Code, shader.Type, at, shader.Others) is not { Kind: GodotShaderNameKind.Declared, Declaration: { } declaration } name)
        {
            return null;
        }
        string? path = name.Include is { } from ? shader.Scenes?.FileOf(from) : document.Path;
        string? text = name.Include is { } inc ? shader.Others.GetValueOrDefault(inc) : document.Text.Snapshot.ToString();
        if (path is null || text is null)
        {
            return null;
        }
        int start = name.Include is null && scene is not null ? scene.Places[declaration.Start] : declaration.Start;
        TextSnapshot snapshot = TextSnapshot.From(text);
        int line = snapshot.GetLineFromOffset(start);
        return new Declared(Declared.Served([(path, new TextPosition(line, start - snapshot.GetLineStart(line)))], ServerPlaces.Texts(context.Editors)), Refusals.NoDeclaration)
        {
            Names = [$"{Words(declaration.Kind)} {declaration.Name}{(name.Include is { } shown ? $" in {shown}" : string.Empty)}"],
        };
    }

    public override async Task<bool> UsagesAsync(int offset, bool inPopup)
    {
        if (context.Editors.Active is not { } shown || !IsShader(shown))
        {
            return false;
        }
        if (await UsesAsync(shown, offset) is not ({ } name, { } places))
        {
            context.Say("No usages found");
            return true;
        }
        context.ShowUsages(name, places, inPopup ? offset : null);
        return true;
    }

    /// <summary>The uses of what the shader, or an include, declares at an offset, the window's and an agent's alike: a
    /// uniform's every place the shader and its includes write it, every scene's material setting it and every string of
    /// the C# naming it (<see cref="CSharpAssist.KindUsesAsync"/>); any other declaration's in the shader and its includes.
    /// Null for a built-in or no name.</summary>
    public async Task<(string Name, IReadOnlyList<LspLocation> Places)?> UsesAsync(OpenDocument document, int offset)
    {
        if (!IsShader(document))
        {
            return null;
        }
        Shader shader = await ShaderAsync(document, document.Text.Snapshot.ToString());
        GodotShaderTable table = await TableAsync(shader);
        if (GodotShaderNames.At(table, shader.Code, shader.Type, offset, shader.Others) is not { Kind: GodotShaderNameKind.Declared, Declaration: { } declaration } name)
        {
            return null;
        }
        List<LspLocation> places = [];
        if (declaration.Kind == GodotShaderDeclared.Uniform && declaration.Scope is null && shader is { Scenes: { } scenes, Folder: { } folder, Res: { } res })
        {
            GodotDefinitions defined = scenes.Defined;
            string owner = name.Include is null ? res : shader.Unit?.Res ?? res;
            (IReadOnlyList<GodotDefined> uniforms, bool whole) = await BackgroundWork.Run(() => defined.Uniforms(owner));
            IReadOnlyList<GodotPlace> written = [.. uniforms.Where(u => u.Name == declaration.Name).SelectMany(u => u.Places)];
            GodotRead read = new(folder, scenes, new GodotCodeRequest(document.Path, [], [], [], []), GodotCodeFound.None);
            places.AddRange(await csharp.KindUsesAsync(new CSharpAssist.KindNamed(read, GodotStringKind.ShaderParameter, declaration.Name,
                new GodotChecks.Candidates(uniforms, $"among the uniforms of {GodotDefinitions.Shown(owner)}", whole), written)));
        }
        else
        {
            places.AddRange(Uses(document.Path, document.Text.Snapshot.ToString(), name.Name, declaration));
            foreach ((string include, string text) in shader.Others)
            {
                if (shader.Scenes?.FileOf(include) is { } file)
                {
                    places.AddRange(Uses(file, text, name.Name, null));
                }
            }
        }
        return (name.Name, places);
    }

    /// <summary>What a shader's code declares at its top, in order, for an agent's outline: each with its line (from one).</summary>
    public static IReadOnlyList<(string Written, string Kind, int Line)> Outline(string text)
    {
        TextSnapshot snapshot = TextSnapshot.From(text);
        return [.. GodotShaderCode.Read(text).Declarations.Where(d => d.Kind is not (GodotShaderDeclared.Parameter or GodotShaderDeclared.Local))
            .OrderBy(d => d.Start).Select(d => (d.Written, Words(d.Kind), snapshot.GetLineFromOffset(d.Start) + 1))];
    }

    /// <summary>Where a text writes a name as a name, as places for the Find window; for a parameter or local only within
    /// where it is seen.</summary>
    private static IEnumerable<LspLocation> Uses(string path, string text, string name, GodotShaderDeclaration? declaration)
    {
        ServerLines lines = ServerLines.Of(TextSnapshot.From(text));
        return GodotShaderCode.Read(text).Uses(name)
            .Where(u => declaration is not { Kind: GodotShaderDeclared.Parameter or GodotShaderDeclared.Local } local || u.Start == local.Start || local.SeenFrom <= u.Start && u.End <= local.SeenTo)
            .Select(u => new LspLocation(DocumentUris.FromPath(path), lines.RangeOf(new TextRange(u.Start, u.End - u.Start))));
    }

    /// <summary>Godot's shading reference at the place of the built-in, mode, hint or function at an offset of a shader,
    /// for a version of Godot's reference; null for a name with no place there (the shader's own).</summary>
    public async Task<(Uri Page, string Name)?> HelpAsync(OpenDocument document, int offset, string version)
    {
        SceneCode? scene = IsShader(document) ? null : SceneCodeAt(document, offset);
        if (!IsShader(document) && scene is null)
        {
            return null;
        }
        Shader shader = scene is null ? await ShaderAsync(document, document.Text.Snapshot.ToString()) : await SceneShaderAsync(document, scene);
        GodotShaderTable table = await TableAsync(shader);
        if (GodotShaderNames.At(table, shader.Code, shader.Type, scene?.At ?? offset, shader.Others) is not { } name)
        {
            return null;
        }
        string page = GodotDocs.ShaderPageOf(shader.Type ?? string.Empty);
        (string Page, string? Section)? place = name.Kind switch
        {
            GodotShaderNameKind.BuiltIn => (page, name.Scope is GodotShaderTable.Global or GodotShaderTable.Constants ? "global-built-ins" : BuiltInsSection(table, shader.Type, name)),
            GodotShaderNameKind.StageFunction => (page, shader.Type == "particles" ? "process-functions" : "sdf-functions"),
            GodotShaderNameKind.RenderMode => (page, "render-modes"),
            GodotShaderNameKind.StencilMode => (page, "stencil-modes"),
            GodotShaderNameKind.Hint => ("shading_language", "uniform-hints"),
            GodotShaderNameKind.Function => ("shader_functions", $"shader-func-{name.Name.ToLowerInvariant()}"),
            _ => null,
        };
        return place is ({ } at, var section) ? (GodotDocs.ShaderPage(version, at, section), name.Name) : null;
    }

    /// <summary>The section of a type's page listing a processor function's built-ins: a particle shader's that both its
    /// functions offer are listed together.</summary>
    private static string BuiltInsSection(GodotShaderTable table, string? type, GodotShaderName name) =>
        type == "particles" && table.TypeOf(type) is { } particles && particles.Mains.All(f => f.BuiltIns.Any(b => b.Name == name.Name))
            ? "start-and-process-built-ins" : $"{name.Scope}-built-ins";

    /// <summary>Whether an offset of a file lies in shader code: a shader's, or a scene's or resource's own Shader's.</summary>
    public static bool InShader(OpenDocument document, int offset) => IsShader(document) || SceneCodeAt(document, offset) is not null;

    /// <summary>The built-ins the Godot found takes.</summary>
    private static Task<GodotShaderTable> TableAsync(Shader shader, CancellationToken cancel = default) =>
        shader.Shaders is { } marks ? marks.ShaderChecker.TableAsync().WaitAsync(cancel) : Task.FromResult(GodotShaderTable.Carried);

    /// <summary>The Shader a scene or resource writes itself whose code an offset of the file lies in: its code as Godot reads
    /// the string, the file's offset of each of its characters (<see cref="GodotStringPlaces"/>), the sub_resource's id
    /// (none for a Shader resource's own), and where the offset lies in the code.</summary>
    private sealed record SceneCode(string Code, int[] Places, string? Id, int At);

    /// <summary>A scene's or resource's Shader code an offset lies in; null elsewhere. Read from the file only where the
    /// offset is in a string a <c>code = "</c> line opens.</summary>
    private static SceneCode? SceneCodeAt(OpenDocument document, int offset)
    {
        if (!GodotFile.IsText(document.Path) || !InCodeString(document.Text.Snapshot, offset))
        {
            return null;
        }
        GodotFile file = GodotFile.Parse(document.Text.Snapshot.ToString());
        bool resource = file.Header?.Text("type") == "Shader";
        foreach (GodotSection section in file.Sections.Where(s => s.Tag == "sub_resource" && s.Text("type") == "Shader" || resource && s.Tag == "resource"))
        {
            if (section.Property("code")?.Value is { Text: { } code } value && value.TextStart <= offset && offset <= value.TextEnd)
            {
                int[] places = GodotStringPlaces.Of(file.Text, value.TextStart, value.TextEnd);
                int at = Math.Max(0, Sorted.FirstAfter<int>(places, offset) - 1);
                return new SceneCode(code, places, section.Tag == "sub_resource" ? section.Text("id") : null, at);
            }
        }
        return null;
    }

    /// <summary>Whether an offset lies in a string that a line written <c>code = "</c> opens: the nearest quote before it, not
    /// escaped, is that line's.</summary>
    private static bool InCodeString(TextSnapshot text, int offset)
    {
        for (int at = Math.Min(offset, text.Length) - 1; at >= 0; at--)
        {
            if (text[at] != '"')
            {
                continue;
            }
            int slashes = 0;
            while (at - slashes - 1 >= 0 && text[at - slashes - 1] == '\\')
            {
                slashes++;
            }
            if (slashes % 2 == 1)
            {
                continue;
            }
            int line = text.GetLineFromOffset(at);
            return text.GetText(text.GetLineStart(line), at - text.GetLineStart(line)).Trim() == "code =";
        }
        return false;
    }

    /// <summary>A scene's or resource's own Shader as read now, as <see cref="ShaderAsync"/> reads a shader file: its res://
    /// path the scene's with the sub_resource's id, as its marks are compiled.</summary>
    private async Task<Shader> SceneShaderAsync(OpenDocument document, SceneCode scene)
    {
        GodotShaderCode code = GodotShaderCode.Read(scene.Code);
        GodotShaderMarks? marks = context.GodotShaders?.Invoke();
        if (context.GodotOf(document.Path) is not ({ } index, { } folder))
        {
            return new Shader(document.Path, code, code.Type, null, null, null, null, marks);
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        string? res = scenes.ResPathOf(document.Path) is { } file ? scene.Id is { } id ? $"{file}::{id}" : file : null;
        GodotShaderUnit? unit = res is null ? null : await BackgroundWork.Run(() => GodotShaderUnit.Of(res, scene.Code, path => scenes.Texts.GetValueOrDefault(path)));
        return new Shader(document.Path, code, code.Type, scenes, folder, res, unit, marks);
    }

    /// <summary>An open shader as read now: its code, its type (an include's, the shader including it's), its project's
    /// scenes, folder and res:// path, the unit Godot is given for it, and the window's shader marks.</summary>
    private async Task<Shader> ShaderAsync(OpenDocument document, string text)
    {
        GodotShaderCode code = GodotShaderCode.Read(text);
        GodotShaderMarks? marks = context.GodotShaders?.Invoke();
        if (context.GodotOf(document.Path) is not ({ } index, { } folder))
        {
            return new Shader(document.Path, code, code.Type, null, null, null, null, marks);
        }
        GodotScenes scenes = await index.ScenesAsync(folder);
        string? res = scenes.ResPathOf(document.Path);
        (GodotShaderUnit Unit, string? Include)? unit = res is null ? null : await BackgroundWork.Run(() => GodotShaderMarks.UnitOf(scenes, res, text));
        string? type = code.Type ?? (unit is ({ } whole, _) ? GodotShaderCode.Read(whole.Code).Type : null);
        return new Shader(document.Path, code, type, scenes, folder, res, unit?.Unit, marks);
    }

    /// <summary>An open shader as read now (<see cref="ShaderAsync"/>), with the other texts of what Godot compiles for it.</summary>
    private sealed record Shader(string Path, GodotShaderCode Code, string? Type, GodotScenes? Scenes, string? Folder, string? Res, GodotShaderUnit? Unit, GodotShaderMarks? Shaders)
    {
        /// <summary>The texts Godot is given beside this one, by res:// path: the shader's includes, and for an include the
        /// shader including it too.</summary>
        public IReadOnlyDictionary<string, string> Others => Unit is null ? new Dictionary<string, string>()
            : Unit.Includes.Where(i => i.Key != Res).Concat(Unit.Res != Res ? [new KeyValuePair<string, string>(Unit.Res, Unit.Code)] : [])
                .ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

        /// <summary>What those other texts declare at their tops.</summary>
        public IEnumerable<GodotShaderDeclaration> Included => Others.Values.SelectMany(text => GodotShaderCode.Read(text).Declarations
            .Where(d => d.Kind is not (GodotShaderDeclared.Parameter or GodotShaderDeclared.Local)));
    }
}
