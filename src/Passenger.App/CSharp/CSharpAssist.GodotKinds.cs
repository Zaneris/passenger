using System.Text.RegularExpressions;
using Passenger.App.Godot;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A Godot project's strings that name what its own files define (<see cref="GodotStringKind"/>: input actions,
/// groups, animations, autoloads, shader parameters, translations, AnimationTree parameters, global shader uniforms), each
/// completed from where Godot defines it (<see cref="GodotChecks.Named"/>), led by Go to Declaration to its definition,
/// listed by Find Usages with every place the project's files and code name it (<see cref="KindUsesAsync"/>), and renamed in
/// one command of Undo in those files and every string of the code naming it (<see cref="RenameKindAsync"/>); a GetNode
/// path's <c>/root/Name</c> is an autoload's, and a layer's number its name in project.godot's [layer_names]. A theme item's
/// name completes from Godot's default theme and the project's themes for the class it is looked up in; and where the
/// person's Godot editor is open on the project, its names join those the files give (<see cref="GodotIdeLink"/>).</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>A line up to the caret that ends inside a string given to one of Godot's calls taking such a name or a member's,
    /// or set to an animation player's animation, so a quote typed there opens the list.</summary>
    private static readonly Regex KindString = new(
        @"(\b(IsAction\w*|GetAction\w*|ActionPress|ActionRelease|GetAxis|GetVector|HasAction|AddAction|EraseAction|Action\w*Event\w*|Action\w*Deadzone|EventIsAction"
        + @"|IsInGroup|AddToGroup|RemoveFromGroup|\w*InGroup|CallGroup\w*|NotifyGroup\w*|SetGroup\w*|HasGroup"
        + @"|Play\w*|Queue|HasAnimation|GetAnimation|AnimationSetNext|AnimationGetNext|\w*BlendTime|SetAutoplay|Set(Current|Assigned)?Animation|Capture"
        + @"|\w*ShaderParameter\w*|Tr|TrN|Translate|TranslatePlural|(Get|Has|Add|Remove)Theme\w+|(Set|Get|Has|Clear)(Color|Constant|Font|FontSize|Icon|Stylebox)"
        + @"|Call|CallDeferred|Callv|Connect|Disconnect|IsConnected|EmitSignal|HasMethod|HasSignal|Get|Set|SetDeferred|ToSignal|Callable)\s*\(([^""()]|""[^""]*"")*"
        + @"|\b(CurrentAnimation|AssignedAnimation|Animation|Autoplay)\s*=\s*)""[^""]*$",
        RegexOptions.CultureInvariant);

    /// <summary>A line up to an offset that ends among the arguments of a call setting or reading a layer by its number.</summary>
    private static readonly Regex LayerArgument = new(@"\b(Set|Get)\w*(Layer|Mask)\w*Value\s*\([^()]*$", RegexOptions.CultureInvariant);

    /// <summary>The characters an animation's name holds beyond a word's (a library's own after its '/'), which narrow its
    /// list rather than close it.</summary>
    private const string KindCharacters = "/.-";

    /// <summary>Whether the file shown is a Godot project's C#.</summary>
    private bool InGodotFile() => context.Editors.Active is { } shown && context.CSharp?.GodotOf?.Invoke(shown.Path) is not null;

    /// <summary>Whether the caret is where a string of a kind is typed, so a quote typed there opens its list.</summary>
    private bool InGodotKind(int caret)
    {
        TextSnapshot text = context.Editor.Document.Snapshot;
        int line = text.GetLineFromOffset(Math.Min(caret, text.Length));
        return KindString.IsMatch(text.GetText(text.GetLineStart(line), Math.Min(caret, text.Length) - text.GetLineStart(line)));
    }

    /// <summary>The string of a kind holding an offset, by the server's places; null for none.</summary>
    private static GodotKindRead? KindAt(LspDocument lsp, GodotRead read, int offset, int quoted) =>
        read.Found.Kinds.FirstOrDefault(k => OffsetOf(lsp, k.At) <= offset && offset <= OffsetOf(lsp, k.End) || OffsetOf(lsp, k.At) == quoted - 1);

    /// <summary>What the open Godot editor is asked for a string of a kind: input actions, shader parameters, a theme item
    /// looked up on the class's own node by its class (the editor answers its node's); null for any other.</summary>
    private static GodotIdeKind? EditorKindOf(GodotKindRead kind) => kind.Kind switch
    {
        GodotStringKind.Action => GodotIdeKind.InputActions,
        GodotStringKind.ShaderParameter => GodotIdeKind.ShaderParams,
        GodotStringKind.ThemeColor when kind.Node == "." => GodotIdeKind.ThemeColors,
        GodotStringKind.ThemeConstant when kind.Node == "." => GodotIdeKind.ThemeConstants,
        GodotStringKind.ThemeFont when kind.Node == "." => GodotIdeKind.ThemeFonts,
        GodotStringKind.ThemeFontSize when kind.Node == "." => GodotIdeKind.ThemeFontSizes,
        GodotStringKind.ThemeStyle when kind.Node == "." => GodotIdeKind.ThemeStyles,
        _ => null,
    };

    /// <summary>The names the person's Godot editor open on the project answers for a kind of string in a script; none
    /// where none is open, or for no kind.</summary>
    private async Task<IReadOnlyList<string>> EditorNamesAsync(GodotRead read, string path, GodotIdeKind? kind, CancellationToken cancel) =>
        kind is { } asked && context.CSharp?.GodotIde is { } editor && read.Scenes.ResPathOf(path) is { } script
            ? await editor.AskAsync(read.Folder, script, asked, cancel) : [];

    /// <summary>What completion offers in a string of a kind: every name it may name (Godot's own input actions after the
    /// project's, then those only the open Godot editor holds), each with what it is; null where the list would be empty.</summary>
    private static CodeCompletion.OwnItems? KindCompletion(GodotRead read, GodotKindRead kind, int start, string tail, IReadOnlyList<string> editor)
    {
        GodotChecks.Candidates named = GodotChecks.Named(read.Scenes, kind.Kind, GodotChecks.ScriptsOf(read.Found, kind.Class), kind.Node, kind.Export, kind.Member, editor);
        IReadOnlyList<CompletionItem> items = [.. named.Names.Select(n => new CompletionItem
        {
            Label = n.Name,
            Kind = CompletionItemKind.Constant,
            Detail = n.Places.FirstOrDefault(p => p.Defines) is { } place ? $"{n.Detail} · {place.Shown}" : n.Detail,
            FilterText = n.Name,
            SortText = (n.Places.Count == 0 ? "~" : " ") + n.Name,
        })];
        return items.Count == 0 ? null : new CodeCompletion.OwnItems(start, items, tail) { WordCharacters = KindCharacters };
    }

    /// <summary>Whether an offset is among the arguments of a call taking a layer's number, so it may be on one.</summary>
    private static bool InLayerArgument(TextSnapshot text, int offset)
    {
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        int start = text.GetLineStart(line);
        string row = text.GetLineText(line);
        int at = Math.Clamp(offset - start, 0, row.Length);
        // The number the offset is on, its digits after it too.
        while (at < row.Length && char.IsAsciiLetterOrDigit(row[at]))
        {
            at++;
        }
        return LayerArgument.IsMatch(row[..at]);
    }

    /// <summary>What the hover card says of a string of a kind: what it names and each place the project defines it, or
    /// that Godot defines it itself, or where Godot looks for it; of a layer, its name.</summary>
    private static string KindHover(GodotRead read, GodotKindRead kind)
    {
        GodotChecks.Candidates named = GodotChecks.Named(read.Scenes, kind.Kind, GodotChecks.ScriptsOf(read.Found, kind.Class), kind.Node, kind.Export, kind.Member);
        string what = GodotChecks.What(kind.Kind);
        if (kind.Kind == GodotStringKind.Layer && kind.Member is { } layers && named.Names.FirstOrDefault(n => n.Name == kind.Name) is { } layer)
        {
            string shown = $"{GodotDefinitions.LayersShown(layers)} layer {kind.Name}";
            return layer.Places is [{ } key, ..] ? $"{shown}: {layer.Detail}, named in {key.Shown}" : $"{shown}, which project.godot's [layer_names] leaves unnamed";
        }
        List<GodotPlace> places = [.. named.Names.Where(n => n.Name == kind.Name).SelectMany(n => n.Places.Where(p => p.Defines))];
        string described = GodotChecks.IsTheme(kind.Kind) && GodotChecks.ThemeDocumented(read.Scenes, kind.Kind, GodotChecks.ScriptsOf(read.Found, kind.Class), kind.Node, kind.Export,
            kind.Member, kind.Name) is ({ } documenting, { } words) ? $"\n{words} ({documenting}'s, in Godot's class reference)" : string.Empty;
        return places.Count > 0 ? string.Join('\n', [$"The {what} {kind.Name}, defined in:", .. places.Select(p => p.Shown)]) + described
            : GodotChecks.IsTheme(kind.Kind) && named.Names.FirstOrDefault(n => n.Name == kind.Name) is { } item ? $"{kind.Name}: {item.Detail}{described}"
            : named.Names.Any(n => n.Name == kind.Name) ? $"{kind.Name} is Godot's own {what}"
            : named.Others.Contains(kind.Name) ? $"{kind.Name} is a {what} of a class deriving from {kind.Member}, or one the project's themes or scenes give"
            : $"No {what} '{kind.Name}' is {named.Where}";
    }

    /// <summary>What completion offers in a GetNode path from its "/": each autoload by its path under /root (those only the
    /// open Godot editor holds too), and the nodes of an autoload's scene below it.</summary>
    private static CodeCompletion.OwnItems? AutoloadCompletion(GodotRead read, int start, string tail, IReadOnlyList<string> editor)
    {
        GodotDefinitions defined = read.Scenes.Defined;
        List<CompletionItem> items = [];
        foreach (string path in editor.Where(p => GodotChecks.AutoloadIn(p) is { } named && defined.Autoloads.All(a => a.Name != named.Name)).Distinct(StringComparer.Ordinal))
        {
            items.Add(new CompletionItem { Label = path, Kind = CompletionItemKind.Module, Detail = "autoload the open Godot editor holds", FilterText = path, SortText = " " + path });
        }
        foreach (GodotDefined autoload in defined.Autoloads)
        {
            string path = "/root/" + autoload.Name;
            string? loaded = read.Scenes.AutoloadFile(autoload.Name);
            items.Add(new CompletionItem { Label = path, Kind = CompletionItemKind.Module, Detail = $"autoload · {loaded}", FilterText = path, SortText = " " + path });
            if (loaded is not null && GodotProject.IsScene(loaded) && read.Scenes.Tree(loaded) is { } tree)
            {
                items.AddRange(GodotScenes.PathsFrom(tree).Where(p => !p.Path.StartsWith('%')).Select(p => new CompletionItem
                {
                    Label = $"{path}/{p.Path}",
                    Kind = CompletionItemKind.Field,
                    Detail = $"{GodotChecks.Shown(p.Node)} · {loaded.Split('/')[^1]}",
                    FilterText = $"{path}/{p.Path}",
                    SortText = $" {path}/{p.Path}",
                }));
            }
        }
        return items.Count == 0 ? null : new CodeCompletion.OwnItems(start, items, tail) { WordCharacters = PathCharacters };
    }

    /// <summary>A name of a kind at an offset of a Godot project's C#: a string of a kind, or an autoload's name in a GetNode
    /// path; with the scenes read, its kind, the name, what it may name, and where its files write it.</summary>
    internal sealed record KindNamed(GodotRead Read, GodotStringKind Kind, string Name, GodotChecks.Candidates Candidates, IReadOnlyList<GodotPlace> Places)
    {
        /// <summary>For a layer, the layers it is among (<c>2d_physics</c>).</summary>
        public string? Layers { get; init; }

        /// <summary>For a theme item, whether Godot's default theme gives it to a type the call looks in.</summary>
        public bool InDefaultTheme { get; init; }

        /// <summary>For a theme item, the class whose page of Godot's class reference describes it; null for none.</summary>
        public string? Documented { get; init; }
    }

    /// <summary>The place in Godot's class reference of the theme item a string at an offset names, for Context Help; null
    /// for an offset on none, or one the reference describes for no class the call looks in.</summary>
    internal async Task<GodotDocItem?> GodotThemeDocAsync(OpenDocument document, int offset) =>
        await GodotKindAtAsync(document, offset) is { Documented: { } documented } named && GodotChecks.IsTheme(named.Kind)
            ? new GodotDocItem(documented, GodotChecks.ThemeDocKind(named.Kind), named.Name) : null;

    /// <summary>The name of a kind at an offset (<see cref="KindNamed"/>); null where the offset is in no such string.</summary>
    internal async Task<KindNamed?> GodotKindAtAsync(OpenDocument document, int offset)
    {
        if (context.CSharp is not { } csharp || csharp.For(document)?.Lsp is not { } lsp || csharp.GodotOf?.Invoke(document.Path) is null)
        {
            return null;
        }
        bool inString = InString(lsp.Text.Snapshot, offset);
        if (!inString && !InLayerArgument(lsp.Text.Snapshot, offset) || await csharp.GodotAsync(document.Path, CancellationToken.None) is not { } read)
        {
            return null;
        }
        (int start, _) = StringAround(lsp.Text.Snapshot, offset);
        if (KindAt(lsp, read, offset, inString ? start : -1) is { Exact: true } kind && (inString || kind.Kind == GodotStringKind.Layer))
        {
            IReadOnlyList<string> scripts = GodotChecks.ScriptsOf(read.Found, kind.Class);
            GodotChecks.Candidates named = GodotChecks.Named(read.Scenes, kind.Kind, scripts, kind.Node, kind.Export, kind.Member);
            IReadOnlyList<GodotPlace> places = [.. named.Names.Where(n => n.Name == kind.Name).SelectMany(n => n.Places)];
            bool theme = GodotChecks.IsTheme(kind.Kind);
            return new KindNamed(read, kind.Kind, kind.Name, named, theme ? [.. places, .. GodotChecks.ThemeOverridesOf(read.Scenes, kind.Kind, kind.Name, places)] : places)
            {
                Layers = kind.Kind == GodotStringKind.Layer ? kind.Member : null,
                InDefaultTheme = theme && GodotChecks.InDefaultTheme(read.Scenes, kind.Kind, scripts, kind.Node, kind.Export, kind.Member, kind.Name),
                Documented = theme ? GodotChecks.ThemeDocumented(read.Scenes, kind.Kind, scripts, kind.Node, kind.Export, kind.Member, kind.Name)?.Class : null,
            };
        }
        if (CallAt(lsp, read, offset, start) is { Exact: true } call && GodotChecks.AutoloadIn(call.Path) is ({ } autoload, int from, int to)
            && offset - OffsetOf(lsp, call.At) is var at && from <= at && at <= to)
        {
            GodotChecks.Candidates named = GodotChecks.Named(read.Scenes, GodotStringKind.Autoload, [], null, null, null);
            return new KindNamed(read, GodotStringKind.Autoload, autoload, named, [.. named.Names.Where(n => n.Name == autoload).SelectMany(n => n.Places)]);
        }
        return null;
    }

    /// <summary>Where Go to Declaration goes from a name of a kind: each place its files define it (an action's key in
    /// project.godot, each node's group, a library's animation, a shader's uniform, a translation's key in each file);
    /// null where the offset is on none.</summary>
    private async Task<Declared?> GodotKindDeclaredAsync(OpenDocument document, int offset)
    {
        if (await GodotKindAtAsync(document, offset) is not { } named)
        {
            return null;
        }
        List<LspLocation> places = [];
        List<string?> names = [];
        foreach (GodotPlace place in named.Places.Where(p => p.Defines))
        {
            if (named.Read.Scenes.FileOf(place.Res) is { } file && named.Read.Scenes.Defined.TextOf(place.Res) is { } text)
            {
                places.Add(new LspLocation(DocumentUris.FromPath(file), ServerLines.Of(TextSnapshot.From(text)).RangeOf(new TextRange(place.Start, place.End - place.Start))));
                names.Add($"{GodotChecks.What(named.Kind)} {named.Name} in {place.Shown}");
            }
        }
        string none = named.Layers is { } layers && named.Candidates.Names.Any(n => n.Name == named.Name)
            ? $"{GodotDefinitions.LayersShown(layers)} layer {named.Name} has no name in project.godot's [layer_names]"
            : GodotChecks.IsTheme(named.Kind) && named.Candidates.Names.FirstOrDefault(n => n.Name == named.Name) is { } item
                ? $"{named.Name} is {item.Detail}, which no file of the project defines"
            : $"No {GodotChecks.What(named.Kind)} '{named.Name}' is {named.Candidates.Where}";
        return new Declared(places, none) { Names = names, Title = "Definitions" };
    }

    /// <summary>Rename's box for a name of a kind at the caret; once a name is typed, <see cref="RenameKindAsync"/>. False
    /// where the caret is on no such name.</summary>
    private async Task<bool> ShowKindRename(OpenDocument document, int caret)
    {
        int version = document.Text.Version;
        if (await GodotKindAtAsync(document, caret) is not { } named || context.Editors.Active != document || document.Text.Version != version)
        {
            return false;
        }
        RenameBox box = new(named.Name);
        popups.ShowModal(box, caret, async name =>
        {
            if (name is null || name == named.Name)
            {
                return;
            }
            // Read again for each attempt, as a file written a moment before is one the index has yet to hear of.
            RefactorOutcome made = await context.PatientlyAsync(AppActions.Rename, async patience => await GodotKindAtAsync(document, caret) is { } now && now.Name == named.Name
                ? await RenameKindAsync(now, name, patience) : RefactorOutcome.Refused($"{named.Name} was not renamed: {document.Name} changed as it was read"));
            context.Say(made.Problem ?? made.Said);
        });
        return true;
    }

    /// <summary>Why a name of a kind may not take a new name, else null: a name Godot's files would not hold as written, one
    /// a name beside it has already, Godot's own input action, an animation's library left, a place written with escapes, or a
    /// raw string that can't say it.</summary>
    internal static string? KindRenameProblem(KindNamed named, string name)
    {
        string what = GodotChecks.What(named.Kind);
        string old = named.Name;
        if (named.Kind == GodotStringKind.Layer)
        {
            return $"A layer is named by its number, which Rename keeps: its name is project.godot's [layer_names]' to change";
        }
        if (named.Kind == GodotStringKind.TreeParameter)
        {
            return $"{old} is made by its AnimationTree's nodes and conditions, which Rename does not change";
        }
        if (GodotChecks.IsTheme(named.Kind) && (named.InDefaultTheme || !named.Places.Any(p => p.Defines)))
        {
            return named.InDefaultTheme ? $"{old} is a {what} of Godot's default theme, whose names Rename does not change"
                : $"No theme of the project defines the {what} {old}, so Rename has nothing to change";
        }
        if (name.Length == 0 || name.Trim() != name || name.Any(char.IsControl))
        {
            return $"'{name}' is no {what}'s name";
        }
        if (named.Places.FirstOrDefault(p => !p.Plain && !p.Unsure) is { } escaped)
        {
            return $"{old} is written with escapes at {escaped.Shown}, which Passenger does not rename";
        }
        if (named.Places.FirstOrDefault(p => !p.Unsure && p.Written(name) is null) is { } raw)
        {
            return $"{old} is written in a raw string at {raw.Shown}, which can't say {name}";
        }
        if (named.Kind == GodotStringKind.Action && !named.Places.Any(p => p.Defines))
        {
            return $"{old} is Godot's own input action, which project.godot does not define, so it keeps its name";
        }
        string refused = named.Kind switch
        {
            GodotStringKind.Action or GodotStringKind.Autoload => " =\"[];\\" + GodotScenes.Unnamable,
            GodotStringKind.Animation => ":,[\"\\" + (old.Contains('/') ? string.Empty : "/"),
            GodotStringKind.Translation => named.Places.Any(p => p.Res.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ? "\"\\,;\t" : "\"\\",
            >= GodotStringKind.ThemeColor and <= GodotStringKind.ThemeStyle => "\"\\/ =[]:",
            _ => "\"\\",
        };
        if (name.FirstOrDefault(c => refused.Contains(c)) is var character and not '\0')
        {
            return $"A{(what[0] is 'a' or 'i' ? "n" : string.Empty)} {what}'s name here never holds '{character}', so {old} keeps its name";
        }
        if (named.Kind is GodotStringKind.ShaderParameter or GodotStringKind.ShaderGlobal && !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
        {
            return $"A uniform's name is a word of letters, digits and '_', so {old} keeps its name";
        }
        if (named.Kind == GodotStringKind.Animation && old.LastIndexOf('/') is var slash and >= 0
            && (!name.StartsWith(old[..(slash + 1)], StringComparison.Ordinal) || name[(slash + 1)..].Length == 0 || name[(slash + 1)..].Contains('/')))
        {
            return $"{old} is the animation {old[(slash + 1)..]} of the library {old[..slash]}, whose name a rename keeps: give it as {old[..(slash + 1)]}…";
        }
        if (named.Candidates.Names.Any(n => n.Name == name && (n.Places.Count > 0 || GodotChecks.IsTheme(named.Kind))))
        {
            return $"There is {Article(what)} {name} already";
        }
        return null;

        static string Article(string noun) => (noun[0] is 'a' or 'i' ? "an " : "a ") + noun;
    }

    /// <summary>A name of a kind renamed, as one command of global undo: every place Godot's files write it
    /// (<see cref="KindNamed.Places"/>), and every string of the code's naming it that reaches those places (an animation
    /// player's, a material's: those told to another are left), its files changed as they were read; a place that only may name
    /// it left as it is, said (<see cref="GodotPlace.Unsure"/>); refused as <see cref="KindRenameProblem"/> says, or for a file
    /// other than as it was read, which <paramref name="patience"/> asks again for. The window's and an agent's alike.</summary>
    internal async Task<RefactorOutcome> RenameKindAsync(KindNamed named, string name, Patience? patience = null)
    {
        if (KindRenameProblem(named, name) is { } problem)
        {
            return RefactorOutcome.Refused(problem);
        }
        GodotScenes scenes = named.Read.Scenes;
        string old = named.Name;
        // An animation of a library is written in it by its own name, after the library's.
        int own = named.Kind == GodotStringKind.Animation ? old.LastIndexOf('/') + 1 : 0;
        Dictionary<string, List<TextEdit>> edits = new(PathNames.Comparer);
        Dictionary<string, string> made = new(PathNames.Comparer);
        foreach (GodotPlace place in named.Places.Where(p => !p.Unsure))
        {
            if (scenes.FileOf(place.Res) is not { } file || scenes.Defined.TextOf(place.Res) is not { } text)
            {
                continue;
            }
            if (place.End > text.Length || text[place.Start..place.End] != old[own..])
            {
                return RefactorOutcome.Refused(Overtaken(file, old, patience));
            }
            made[file] = text;
            Add(file, new TextEdit(place.Start, place.End - place.Start, place.Written(name[own..])!));
        }
        if (context.CSharp?.Session is { } session)
        {
            IReadOnlyList<GodotKindUse> uses;
            try
            {
                uses = await CodeUsesAsync(session, named);
            }
            catch (LanguageServerFailed failed)
            {
                return RefactorOutcome.Refused($"{old} was not renamed: {failed.Message}");
            }
            foreach (GodotKindUse use in uses)
            {
                if (context.Editors.TextOf(use.File) is not { } text)
                {
                    continue;
                }
                int at = ServerLines.Of(text).OffsetOf(new LspPosition(use.At.Line, use.At.Character));
                if (at + old.Length > text.Length || text.GetText(at, old.Length) != old)
                {
                    return RefactorOutcome.Refused(Overtaken(use.File, old, patience));
                }
                made[use.File] = text.ToString();
                Add(use.File, new TextEdit(at, old.Length, name));
            }
        }
        List<XamlFileChange> changes = [.. edits.Select(e =>
        {
            string was = made[e.Key];
            IReadOnlyList<TextEdit> these = [.. e.Value.Distinct().OrderBy(t => t.Start)];
            return new XamlFileChange(e.Key, now => now == was ? these : []);
        })];
        if (changes.Count == 0)
        {
            return RefactorOutcome.Refused($"{old} was not renamed: no file writes it");
        }
        if (await GodotBehindAsync(made, old, patience) is { } behind)
        {
            return RefactorOutcome.Refused(behind);
        }
        if (await actions.ApplyFileChangesAsync($"Rename {old} to {name}", changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        List<string> left = [.. named.Places.Where(p => p.Unsure).Select(p => p.Shown)];
        string leaving = left.Count == 0 ? string.Empty : $", leaving {Plural.Listed(left)}, which may not name the {GodotChecks.What(named.Kind)}";
        return new RefactorOutcome($"Renamed {old} to {name} in {Plural.Of(changes.Count, "file")}{leaving}", null,
            [.. edits.Select(e => new RefactoredFile(e.Key, "changed", [.. e.Value.Select(t => TextSnapshot.From(made[e.Key]).GetLineFromOffset(t.Start) + 1).Distinct()]))], []);

        void Add(string file, TextEdit edit)
        {
            if (!edits.TryGetValue(file, out List<TextEdit>? list))
            {
                edits[file] = list = [];
            }
            list.Add(edit);
        }
    }

    /// <summary>The strings and numbers of the code naming a name of a kind (Passenger's extension's), those of an animation
    /// player, tree or material told to be another than one whose files write the name left out, a layer's among its own layers.</summary>
    private async Task<IReadOnlyList<GodotKindUse>> CodeUsesAsync(RoslynSession session, KindNamed named)
    {
        GodotKindUses uses = await session.ExtensionAsync<GodotKindUses>(Handlers.GodotKindStrings, new GodotKindAsked(named.Read.Folder, named.Kind, named.Name, GodotBuilding(named.Read.Folder)));
        HashSet<(string, int)> reached = [.. named.Places.Select(p => (p.Res, p.Start))];
        return [.. uses.Uses.Where(use => named.Kind switch
        {
            GodotStringKind.Animation or GodotStringKind.ShaderParameter or GodotStringKind.TreeParameter or (>= GodotStringKind.ThemeColor and <= GodotStringKind.ThemeStyle) => reached.Count == 0
                || GodotChecks.Named(named.Read.Scenes, named.Kind, use.Scripts, use.Node, use.Export, use.Member).Names.Where(n => n.Name == named.Name)
                    .SelectMany(n => n.Places).Any(p => reached.Contains((p.Res, p.Start))),
            GodotStringKind.Layer => use.Member == named.Layers,
            _ => true,
        })];
    }

    /// <summary>Find Usages of a name of a kind: every place the project's files write it (where they define it, a GDScript's
    /// strings, a scene's node paths) and every string or number of the code naming it, as places for the Find window.</summary>
    internal async Task<IReadOnlyList<LspLocation>> KindUsesAsync(KindNamed named)
    {
        GodotScenes scenes = named.Read.Scenes;
        List<LspLocation> places = [.. await BackgroundWork.Run(() => named.Places.GroupBy(p => p.Res, StringComparer.Ordinal).SelectMany(file =>
            scenes.FileOf(file.Key) is { } path && scenes.Defined.TextOf(file.Key) is { } text
                ? file.Select(p => new LspLocation(DocumentUris.FromPath(path), ServerLines.Of(TextSnapshot.From(text)).RangeOf(new TextRange(p.Start, p.End - p.Start))))
                : []).ToList())];
        if (context.CSharp?.Session is not { } session)
        {
            return places;
        }
        try
        {
            places.AddRange((await CodeUsesAsync(session, named)).Select(u => new LspLocation(DocumentUris.FromPath(u.File),
                new LspRange(new LspPosition(u.At.Line, u.At.Character), new LspPosition(u.End.Line, u.End.Character)))));
        }
        catch (LanguageServerFailed)
        {
            // The code's uses are left out where the server cannot be asked; the project's files still say theirs.
        }
        return places;
    }
}
