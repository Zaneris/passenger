using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.App;

/// <summary>Context Help: the page of Godot's class reference for the Godot class or member at the caret, browsed, of the
/// version of the Godot found (<see cref="GodotDocs"/>): from C#, the engine's class and member by GodotSharp's own names
/// (asked of Passenger's extension, <see cref="GodotNamesFound.Engine"/>); from GDScript, by what Godot's server's card for
/// it names (<see cref="GodotDocs.OfScriptCard"/>); from a shader, the place of its built-in, mode, hint or function in
/// Godot's shading reference (<see cref="CSharp.GodotShaderAssist.HelpAsync"/>). What has no page there is said.</summary>
public partial class MainWindow
{
    private void AddContextHelp() =>
        actions!.Add(new IdeAction(AppActions.ContextHelp, "Context Help", "Help", _ => IdeErrors.Observe(ContextHelpAsync()))
        {
            WhyNot = _ => Editors.Active is not { } file ? Refusals.NoFile
                : HelpServer(file) is null && !CSharp.GodotShaderAssist.InShader(file, Editors.CodeEditor.Selections.Primary.Caret) ? Refusals.NoContextHelp(file.Name) : null,
            Menus = [new MenuPlace("Help", 0, 1)],
        });

    /// <summary>The server that names what is at a file's caret: the C# server's document, else GDScript's; null for neither.</summary>
    private (LspDocument Lsp, bool Script)? HelpServer(OpenDocument file) =>
        session.CSharp?.For(file)?.Lsp is { } csharp ? (csharp, false)
        : session.Web?.For(file) is { Kind: Web.WebServerKind.GDScript, Lsp: { } gdscript } ? (gdscript, true)
        : null;

    private async Task ContextHelpAsync()
    {
        if (Editors.Active is { } shader && CSharp.GodotShaderAssist.InShader(shader, Editors.CodeEditor.Selections.Primary.Caret))
        {
            await ShaderHelpAsync(shader);
            return;
        }
        if (Editors.Active is not { } file || HelpServer(file) is not ({ } lsp, bool script))
        {
            return;
        }
        using Working? work = actions!.Work.Begin(AppActions.ContextHelp, "Finding the caret's class or member in Godot's class reference");
        int offset = Editors.CodeEditor.Selections.Primary.Caret;
        string? godot = settings.GodotExecutable;
        try
        {
            GodotDocItem? item = script ? (await lsp.HoverAsync(offset)) is { } card ? GodotDocs.OfScriptCard(Shapes.Text(card.Contents)) : null
                : (await lsp.ExtensionAsync<GodotNamesFound>(Handlers.GodotNames, new GodotNamesAsked(lsp.PlaceOf(offset), false))).Engine is { } engine
                    ? new GodotDocItem(engine.Class, engine.Kind switch
                    {
                        GodotNameKind.Method => GodotDocs.Method,
                        GodotNameKind.Signal => GodotDocs.Signal,
                        GodotNameKind.Property => GodotDocs.Property,
                        _ => null,
                    }, engine.Member) : null;
            if (item is null)
            {
                Told(false, Refusals.NoGodotDocumentation);
                return;
            }
            string? version = (await BackgroundWork.Run(() => GodotLaunch.Found(godot)))?.Version;
            Uri page = GodotDocs.Page(GodotDocs.VersionOf(version), item);
            Browse(page);
            Told(true, $"Opened {item.Class}{(item.Member is { } member ? $".{member}" : string.Empty)} in Godot's class reference: {page}");
        }
        catch (LanguageServerFailed failed)
        {
            Told(false, failed.Message);
        }

        void Told(bool succeeded, string said)
        {
            Say(said);
            ActionWork.Tell(succeeded, said);
        }
    }

    /// <summary>Context Help in a shader: the place in Godot's shading reference of what is at the caret, browsed.</summary>
    private async Task ShaderHelpAsync(OpenDocument shader)
    {
        using Working? work = actions!.Work.Begin(AppActions.ContextHelp, "Finding the caret's built-in in Godot's shading reference");
        int offset = Editors.CodeEditor.Selections.Primary.Caret;
        string? godot = settings.GodotExecutable;
        string version = GodotDocs.VersionOf((await BackgroundWork.Run(() => GodotLaunch.Found(godot)))?.Version);
        if (await assist.ShaderHelpAsync(shader, offset, version) is not ({ } page, { } name))
        {
            Say(Refusals.NoShaderDocumentation);
            ActionWork.Tell(false, Refusals.NoShaderDocumentation);
            return;
        }
        Browse(page);
        string said = $"Opened {name} in Godot's shading reference: {page}";
        Say(said);
        ActionWork.Tell(true, said);
    }
}
