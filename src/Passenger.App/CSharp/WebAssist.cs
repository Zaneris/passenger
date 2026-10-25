using System.Text;
using System.Text.RegularExpressions;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>The same popups and commands over a JavaScript, TypeScript, HTML, CSS or JSON file, answered by its
/// language server (<see cref="WebLanguages"/>): completion after the server's own trigger characters, names that run
/// on through the language's own characters, and, where the JSON server has none, Go to Declaration from a package.json
/// dependency to its installed package and Rename of a JSON property, a package.json script's runs with it.</summary>
internal sealed partial class WebAssist(AssistContext context, PopupPlacement popups, CodeCompletion completion, ParameterInfo parameters, CodeActions actions) : LanguageAssist
{
    private Passenger.Editor.CodeEditor Editor => context.Editor;

    public override bool Typed(string text)
    {
        if (context.WebCurrent is not { } web)
        {
            return false;
        }
        AfterTypedInWeb(web, text);
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.WebCurrent is not { } web)
        {
            return false;
        }
        if (web.Lsp is { } lsp)
        {
            await completion.ServerListAsync(lsp, trigger, incomplete, web);
        }
        return true;
    }

    public override async Task<bool> HoverAsync(InfoCard card, int offset, CancellationToken cancel)
    {
        if (context.WebCurrent is not { } web)
        {
            return false;
        }
        if (web.Lsp is { } lsp)
        {
            await QuickInfo.ServerHoverAsync(lsp, card, offset, cancel);
        }
        return true;
    }

    /// <summary>Go to Declaration where the server has none (JSON's); the server's own (<see cref="CSharpAssist"/>) otherwise.</summary>
    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.WebCurrent is not { Lsp: not null } web || web.Provides("definitionProvider"))
        {
            return false;
        }
        await WebGoTo(web, offset);
        return true;
    }

    public override Task<bool> UsagesAsync(int offset)
    {
        if (context.WebCurrent is not { Lsp: not null } web || web.Provides("referencesProvider"))
        {
            return Passed;
        }
        context.Say($"Find Usages finds nothing in {context.Editors.Active?.Name}: its language server has no usages to find");
        return Answered;
    }

    /// <summary>Rename where the server has none (JSON's); the server's own (<see cref="CSharpAssist"/>) otherwise.</summary>
    public override bool Rename(int caret)
    {
        if (context.WebCurrent is not { Lsp: not null } own || own.Provides("renameProvider") || context.Editors.Active is not { } json)
        {
            return false;
        }
        ShowWebRename(own, json);
        return true;
    }

    /// <summary>Prettier's "Run on 'Reformat Code' action", for any file it formats: one its .prettierignore names, or of a
    /// type it does not format, goes to its language server.</summary>
    public override async Task<CodeActions.Reformatted?> FormatAsync(OpenDocument document, TextRange? within)
    {
        if (!PrettierReformats(document) || context.Web?.Invoke() is not { } web)
        {
            return null;
        }
        (Prettified outcome, string said) = await PrettifyAsync(web, document, "Reformat Code");
        return outcome is Prettified.Ignored or Prettified.Unsupported ? null : new CodeActions.Reformatted(outcome != Prettified.Failed, outcome == Prettified.Formatted, said);
    }

    /// <summary>What opens completion in a web file: one of its server's trigger characters (a member's '.', CSS's ':', a
    /// tag's '&lt;', a JSON string's quote), or a name's first letter; and in a script, a call's bracket its signature.</summary>
    private void AfterTypedInWeb(WebDocument web, string text)
    {
        char last = text[^1];
        int caret = Editor.Selections.Primary.Caret;
        if (text.Length == 1 && web.CompletionTriggers.Contains(text))
        {
            IdeErrors.Observe(completion.Start(text));
        }
        else if ((char.IsLetter(last) || last is '_' or '$') && WebWordStart(web, caret) == caret - 1)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        // A script's call shows its signature after its bracket or comma; any file's closing bracket or semicolon hides it.
        if (web.Kind is WebServerKind.TypeScript or WebServerKind.Html || last is ')' or ';')
        {
            parameters.AfterTyped(last);
        }
    }

    /// <summary>A character of a web language's name beyond an identifier's: CSS's and HTML's '-' (background-color,
    /// --accent, data-id), JavaScript's '$', a Dockerfile's flags' '-' (--from), YAML's keys' and names' '-' and '.'
    /// (x-common, my-api), and in JSON the quotes a property's name is written in.</summary>
    public static bool IsWebWordChar(WebDocument web, char c) => web.Kind switch
    {
        WebServerKind.Css or WebServerKind.Html or WebServerKind.Dockerfile => c == '-',
        WebServerKind.TypeScript => c == '$',
        WebServerKind.Yaml => c is '-' or '.',
        _ => c is '"' or '-' or '.' or '@' or '/',
    };

    private int WebWordStart(WebDocument web, int caret) => WebWordStart(web, Editor.Document.Snapshot, caret);

    public static int WebWordStart(WebDocument web, TextSnapshot text, int caret)
    {
        int start = caret;
        while (start > 0 && (AssistContext.IsIdentifierChar(text[start - 1]) || (IsWebWordChar(web, text[start - 1]) && text[start - 1] != '"')))
        {
            start--;
        }
        return start;
    }

    /// <summary>The name around an offset in a web file's text, through its language's own characters.</summary>
    public static TextRange WebWordAt(WebDocument web, TextSnapshot text, int offset)
    {
        int start = WebWordStart(web, text, Math.Min(offset, text.Length));
        int end = Math.Min(offset, text.Length);
        while (end < text.Length && (AssistContext.IsIdentifierChar(text[end]) || (IsWebWordChar(web, text[end]) && text[end] != '"')))
        {
            end++;
        }
        return TextRange.FromBounds(start, end);
    }

    /// <summary>Go to Declaration where the server has none (JSON's): from a package.json dependency to the package's
    /// own package.json in node_modules, and from a script's <c>npm run</c> to the script it runs.</summary>
    private async Task WebGoTo(WebDocument web, int offset)
    {
        if (web.Lsp is not { } lsp || context.Editors.Active is not { } document)
        {
            return;
        }
        try
        {
            ((string Path, TextPosition Position)? place, string said) = await JsonDeclarationAsync(lsp, document, offset);
            if (context.Editors.Active != document || place is not { } found)
            {
                context.Say(context.Editors.Active == document ? said : Refusals.NoDeclaration);
                return;
            }
            context.Navigate(found);
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Go to Declaration", failed);
        }
    }

    /// <summary>Where Go to Declaration goes in JSON, whose server has none: a package.json dependency's installed package,
    /// a script's <c>npm run</c> to the script; else why not.</summary>
    internal static async Task<((string Path, TextPosition Position)? Place, string Said)> JsonDeclarationAsync(LspDocument lsp, OpenDocument document, int offset)
    {
        TextSnapshot text = document.Text.Snapshot;
        IReadOnlyList<DocumentSymbol> outline = await lsp.OutlineAsync();
        if (!IsPackageJson(document))
        {
            return (null, Refusals.NoDeclaration);
        }
        LspPosition at = lsp.PositionOf(offset);
        List<DocumentSymbol> path = DocumentSymbol.PathTo(outline, at);
        if (path is [{ Name: "dependencies" or "devDependencies" or "peerDependencies" or "optionalDependencies" }, { } dependency] && dependency.SelectionRange.Holds(at))
        {
            return Installed(document.Path, dependency.Name) is { } installed
                ? ((installed, new TextPosition(0, 0)), $"{dependency.Name}'s package.json")
                : (null, $"{dependency.Name} is not installed: there is no node_modules/{dependency.Name}/package.json over {document.Name} (npm install puts it there)");
        }
        if (path is [{ Name: "scripts" } scripts, _] && RunAt(text, offset) is { } script
            && scripts.Children?.FirstOrDefault(s => s.Name == script) is { } declared)
        {
            TextPosition name = ServerPlaces.PositionOf(text, declared.SelectionRange.Start);
            return ((document.Path, new TextPosition(name.Line, name.Column + 1)), $"The script {script}");
        }
        return (null, Refusals.NoDeclaration);
    }

    /// <summary>Rename where the server has none (JSON's): the property name at the caret, quotes kept, and in a
    /// package.json's scripts every <c>npm run</c> of a script renamed with it, as one undoable edit.</summary>
    private void ShowWebRename(WebDocument web, OpenDocument document)
    {
        if (web.Lsp is not { } lsp)
        {
            return;
        }
        int caret = Editor.Selections.Primary.Caret;
        // The text the outline is asked of, whose places the edit is made in and carried through what is typed since.
        ChangeTracker asked = new(document.Text);
        IdeErrors.Observe(popups.Carried("Rename", async () =>
        {
            bool shown = false;
            try
            {
                IReadOnlyList<DocumentSymbol> outline = await lsp.OutlineAsync();
                if (context.Editors.Active != document || web.Lsp != lsp)
                {
                    return;
                }
                ServerLines lines = ServerLines.Of(asked.Snapshot);
                if (JsonPropertyAt(outline, lines, caret) is not { } at)
                {
                    context.Say("Nothing to rename here: put the caret on a property's name");
                    return;
                }
                DocumentSymbol property = at.Path[^1];
                RenameBox box = new(property.Name);
                shown = true;
                popups.ShowModal(box, asked.MapOffset(at.Key.Start), name =>
                {
                    if (name is null || name == property.Name || context.Editors.Active != document)
                    {
                        return Task.CompletedTask;
                    }
                    IReadOnlyList<TextEdit> edits = JsonRenameEdits(at.Path, at.Key, lines, name, IsPackageJson(document));
                    if (asked.Map(edits) is not { } placed)
                    {
                        context.Say("Rename was not applied: the property was edited meanwhile");
                        return Task.CompletedTask;
                    }
                    Editor.ApplyEdits(placed);
                    int runs = placed.Count - 1;
                    context.Say(runs == 0 ? "Renamed" : $"Renamed, and in {Plural.Of(runs, "script")} that run{(runs == 1 ? "s" : "")} it");
                    return Task.CompletedTask;
                });
                // Let go as the box closes, however it closes; what was typed until then is still carried.
                popups.WhenClosed(asked.Dispose);
            }
            finally
            {
                if (!shown)
                {
                    asked.Dispose();
                }
            }
        }, closeModal: false));
    }

    /// <summary>The JSON property whose name is at an offset of the text the outline is of: its path from the top, and its
    /// name's range, quotes and all; null where the offset is on no property's name.</summary>
    internal static (List<DocumentSymbol> Path, TextRange Key)? JsonPropertyAt(IReadOnlyList<DocumentSymbol> outline, ServerLines lines, int offset)
    {
        LspPosition at = lines.PositionOf(Math.Min(offset, lines.Text.Length));
        List<DocumentSymbol> path = DocumentSymbol.PathTo(outline, at);
        if (path.Count == 0 || !path[^1].SelectionRange.Holds(at))
        {
            return null;
        }
        DocumentSymbol property = path[^1];
        return (path, TextRange.FromBounds(lines.OffsetOf(property.SelectionRange.Start), lines.OffsetOf(property.SelectionRange.End)));
    }

    /// <summary>Rename's edits for a JSON property, in order: its name, quoted, and in a package.json's scripts every
    /// <c>npm run</c> of a script renamed with it.</summary>
    internal static IReadOnlyList<TextEdit> JsonRenameEdits(List<DocumentSymbol> path, TextRange key, ServerLines lines, string name, bool packageJson)
    {
        string written = JsonStringContent(name);
        List<TextEdit> edits = [new TextEdit(key.Start, key.Length, $"\"{written}\"")];
        if (packageJson && path is [{ Name: "scripts" } scripts, _])
        {
            TextRange within = TextRange.FromBounds(lines.OffsetOf(scripts.Range.Start), lines.OffsetOf(scripts.Range.End));
            string text = lines.Text.GetText(within);
            foreach (Match run in Runs(path[^1].Name).Matches(text))
            {
                Group script = run.Groups["script"];
                edits.Add(new TextEdit(within.Start + script.Index, script.Length, written));
            }
        }
        return [.. edits.OrderBy(e => e.Start)];
    }

    /// <summary>Text as a JSON string holds it, read as typed: only what JSON must escape is (a quote, a backslash, a control
    /// character), where a serializer's encoder escapes every letter outside ASCII and <c>+</c>, <c>&lt;</c> and the like.</summary>
    private static string JsonStringContent(string text)
    {
        StringBuilder written = new(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '"' or '\\':
                    written.Append('\\').Append(c);
                    break;
                case '\n':
                    written.Append("\\n");
                    break;
                case '\r':
                    written.Append("\\r");
                    break;
                case '\t':
                    written.Append("\\t");
                    break;
                case < ' ':
                    written.Append($"\\u{(int)c:x4}");
                    break;
                default:
                    written.Append(c);
                    break;
            }
        }
        return written.ToString();
    }

    /// <summary>The registry page of the image a Dockerfile's FROM names where an offset is on it; null elsewhere.</summary>
    internal static Uri? FromImageAt(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Clamp(offset, 0, text.Length));
        int column = Math.Clamp(offset, 0, text.Length) - text.GetLineStart(line);
        return FromLine().Match(text.GetLineText(line)) is { Success: true } from
            && column >= from.Groups["image"].Index && column <= from.Groups["image"].Index + from.Groups["image"].Length
            ? ComposeDeclarations.RegistryPage(from.Groups["image"].Value)
            : null;
    }

    [GeneratedRegex(@"^\s*FROM\s+(?:--\S+\s+)*(?<image>[^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromLine();

    internal static bool IsPackageJson(OpenDocument document) => Path.GetFileName(document.Path).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The installed package's package.json, found as Node.js resolves a package: node_modules beside the
    /// package.json, then in each folder above it.</summary>
    private static string? Installed(string packageJson, string name)
    {
        for (string? folder = Path.GetDirectoryName(packageJson); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string candidate = Path.Combine(folder, "node_modules", name, "package.json");
            if (File.Exists(candidate))
            {
                return Passenger.Core.PathNames.Canonical(candidate);
            }
        }
        return null;
    }

    /// <summary>The script a script's command runs (<c>npm run build</c>) where the offset is on its name; null elsewhere.</summary>
    private static string? RunAt(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        int start = text.GetLineStart(line);
        string written = text.GetLineText(line);
        foreach (Match run in AnyRun().Matches(written))
        {
            Group script = run.Groups["script"];
            if (offset >= start + script.Index && offset <= start + script.Index + script.Length)
            {
                return script.Value;
            }
        }
        return null;
    }

    /// <summary>A script run by its name from another: npm's, Yarn's and pnpm's run.</summary>
    private static Regex Runs(string script) => new($@"\b(?:npm|yarn|pnpm)\s+run(?:-script)?\s+(?<script>{Regex.Escape(script)})(?=$|[\s""&;|])");

    [GeneratedRegex(@"\b(?:npm|yarn|pnpm)\s+run(?:-script)?\s+(?<script>[\w:.\-]+)")]
    private static partial Regex AnyRun();
}
