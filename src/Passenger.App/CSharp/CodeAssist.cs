using Avalonia.Controls;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A fix Passenger offers itself at a place in a file, beside the language server's (a running app's failed binding's
/// nearest member, a compose file's undefined name): what Show Context Actions lists, a quieter detail, and the text it puts
/// in place of a range.</summary>
public sealed record EditorFix(string Title, string Detail, TextRange Range, string Text)
{
    /// <summary>The fix worked out again on the file's text as it stands when it is chosen, null when that text no longer
    /// has what it fixes; none for a fix made as it was offered.</summary>
    public Func<TextSnapshot, EditorFix?>? Again { get; init; }

    /// <summary>Where the caret goes in <see cref="Text"/> once it is made: its end when none is given.</summary>
    public int? Caret { get; init; }

    /// <summary>A fix made in another file than the one shown (an AppHost's WithReference, for its service's warning), in
    /// place of <see cref="Text"/>'s edit: made as it is chosen, given <see cref="Range"/> as it then stands and an agent's
    /// call's patience, and saying what it came to.</summary>
    public Func<TextRange, Patience?, Task<RefactorOutcome>>? Elsewhere { get; init; }

    /// <summary>A fix that asks for a value before it is made (an AppHost parameter's), in place of <see cref="Text"/>'s
    /// edit: what is asked, whether the answer is a secret (masked as it is typed, never said back), and the fix made from it.</summary>
    public ValueAsked? Asks { get; init; }

    /// <summary>The fix to make on a text now, worked out again where it can be; null when it no longer fits.</summary>
    public EditorFix? On(TextSnapshot text) => Again is null ? this : Again(text);
}

/// <summary>What a fix asks before it is made: the question, whether the answer is a secret, and the fix made from the
/// answer, given an agent's call's patience, saying what it came to (never the answer).</summary>
public sealed record ValueAsked(string Question, bool Secret, Func<string, Patience?, Task<RefactorOutcome>> Make);

/// <summary>What the language services add to the editor as you work, for the window to wire once: completion as you type
/// (<see cref="CodeCompletion"/>), a card when the pointer rests (<see cref="QuickInfo"/>), signature help after an opening
/// bracket or a comma (<see cref="ParameterInfo"/>), going to declarations and usages (<see cref="CodeNavigation"/>), and
/// the code actions and refactorings (<see cref="CodeActions"/>), their popups placed at the caret
/// (<see cref="PopupPlacement"/>); each file's language answers through the one ladder of <see cref="LanguageAssists"/>.</summary>
public sealed class CodeAssist
{
    private readonly AssistContext context;
    private readonly PopupPlacement popups;
    private readonly QuickInfo quickInfo;
    private readonly ParameterInfo parameterInfo;
    private readonly CodeCompletion completion;
    private readonly CodeNavigation navigation;
    private readonly CodeActions actions;

    public CodeAssist(EditorArea editors, Canvas layer, Func<CSharpService?> service)
    {
        context = new AssistContext(editors, service);
        popups = new PopupPlacement(context, layer);
        // The cards first, under the completion list and its item's card, as they always lie on the layer.
        quickInfo = new QuickInfo(context, popups, () => completion!.IsOpen);
        parameterInfo = new ParameterInfo(context, popups);
        completion = new CodeCompletion(context, popups);
        navigation = new CodeNavigation(context, popups, quickInfo);
        actions = new CodeActions(context, popups, navigation);
        XamlAssist xaml = new(context, popups, completion);
        context.Languages = new LanguageAssists(new HttpAssist(context, navigation), new MSBuildAssist(context, popups, completion), xaml,
            new ComposeAssist(context, popups, completion, navigation, actions), new SettingsAssist(context, completion, navigation),
            new WebAssist(context, popups, completion, parameterInfo, actions), new CSharpAssist(context, popups, completion, parameterInfo, navigation, actions, xaml));
        // Every editor a split makes is wired the same; what it asks is answered for the active one, which it is.
        editors.ForEachEditor(editor =>
        {
            editor.Hovered += (_, e) =>
            {
                // A value's card in use (its keys, its menu, a value being typed) stays over what else the pointer rests on.
                if (editor == Editor && !quickInfo.ValuesInUse)
                {
                    IdeErrors.Observe(e.Hint is { } hint ? quickInfo.ShowHint(hint, e.Point) : quickInfo.Show(e.Offset, e.Point));
                }
            };
            editor.HoverEnded += (_, _) => quickInfo.PointerLeft();
            editor.Typed += (_, text) =>
            {
                if (editor == Editor)
                {
                    quickInfo.Hide();
                    completion.Typed(text);
                }
            };
            editor.SelectionChanged += (_, _) =>
            {
                if (editor == Editor)
                {
                    completion.CaretMoved();
                }
            };
            editor.NavigateRequested += (_, offset) =>
            {
                if (editor == Editor)
                {
                    IdeErrors.Observe(navigation.GoToDefinition(offset));
                }
            };
        });
        editors.ActiveChanged += (_, _) =>
        {
            CloseAll();
            popups.FollowLayer();
        };
    }

    /// <summary>A place to go: a file and a line and column (from zero).</summary>
    public event EventHandler<(string Path, TextPosition Position)>? Navigate
    {
        add => context.Navigated += value;
        remove => context.Navigated -= value;
    }

    /// <summary>Find Usages' answer, for the Find window: what was searched for and where it is used.</summary>
    public event EventHandler<(string Title, IReadOnlyList<LspLocation> Usages)>? UsagesFound
    {
        add => context.UsagesFound += value;
        remove => context.UsagesFound -= value;
    }

    /// <summary>Something to say in the status bar ("No usages found").</summary>
    public event EventHandler<string>? Message
    {
        add => context.Message += value;
        remove => context.Message -= value;
    }

    /// <summary>A refactoring changed files beyond the open ones, or made new ones, which the Explorer and Git then show.</summary>
    public event EventHandler? FilesChanged
    {
        add => context.FilesChanged += value;
        remove => context.FilesChanged -= value;
    }

    /// <summary>A page to open in the browser: an ESLint rule's, an image's registry page.</summary>
    public event EventHandler<Uri>? Browse
    {
        add => context.Browsed += value;
        remove => context.Browsed -= value;
    }

    /// <summary>An option of Rename's turned on or off, for the window to keep.</summary>
    public event EventHandler? RenameSettingsChanged
    {
        add => actions.RenameSettingsChanged += value;
        remove => actions.RenameSettingsChanged -= value;
    }

    /// <summary>The code actions and refactorings, which an agent's tools make as the window's commands do.</summary>
    internal CodeActions Actions => actions;

    /// <summary>The languages' own answers, which an agent's tools ask as the window's commands do.</summary>
    internal LanguageAssists Languages => context.Languages;

    public bool IsCompletionOpen => completion.IsOpen;

    /// <summary>What the card beside the completion list says about the chosen item while it shows, null otherwise.</summary>
    public string? ItemDocumentation => completion.ItemDocumentation;

    /// <summary>The completion list's labels, in order, while it shows.</summary>
    public IReadOnlyList<string> CompletionLabels => completion.Labels;

    /// <summary>The completion list's rows, in order, each with the detail it shows on its right, while it shows.</summary>
    public IReadOnlyList<(string Label, string Detail)> CompletionDetails => completion.Details;

    /// <summary>What was typed of the name the completion list shows its rows for, while it shows.</summary>
    public string? CompletionNarrowedBy => completion.NarrowedBy;

    /// <summary>The controls showing the completion list's rows on screen, while it shows.</summary>
    public IReadOnlyList<Control> CompletionRowControls => completion.RowControls;

    /// <summary>What the hover card says while it shows, null otherwise.</summary>
    public string? HoverText => quickInfo.Text;

    /// <summary>The rows of a paused program's value the hover card shows, while it shows them.</summary>
    public Debugging.VariablesList? HoverValues => quickInfo.Values;

    /// <summary>The hover card while it shows, its frame the popups' (<c>Border.popup</c>).</summary>
    public Border? HoverCard => quickInfo.Card;

    /// <summary>What Parameter Info's card says, while it shows.</summary>
    public string? SignatureText => parameterInfo.Text;

    /// <summary>The name in the rename box while it is open, null otherwise.</summary>
    public string? Renaming => popups.Modal is RenameBox box ? box.Text : null;

    /// <summary>The files the open rename box offers to rename with the type, none while it offers none.</summary>
    public IReadOnlyList<string> RenameOffersFiles => popups.Modal is RenameBox box ? box.Files : [];

    /// <summary>Rename's options as they were left: whether a type's files and a file's type are renamed with it.</summary>
    public RenameSettings RenameSettings
    {
        get => actions.RenameSettings;
        set => actions.RenameSettings = value;
    }

    /// <summary>What the open list of choices (fixes, refactorings, definitions) offers, by its words; empty with none open.</summary>
    public IReadOnlyList<string> Offered => popups.Offered;

    /// <summary>What the list of choices shown says beside each of <see cref="Offered"/>.</summary>
    public IReadOnlyList<string> OfferedDetails => popups.OfferedDetails;

    /// <summary>What the open list of choices is titled ("Select expression to parenthesize"); null with none open.</summary>
    public string? OfferedTitle => popups.OfferedTitle;

    /// <summary>While the debugger is paused, the value of what the pointer rests on, which the hover card shows
    /// instead of the symbol's description; in a debugged app's .axaml file, a binding's.</summary>
    public Func<int, CancellationToken, Task<DebuggerHover?>>? DebuggerValue
    {
        get => quickInfo.DebuggerValue;
        set => quickInfo.DebuggerValue = value;
    }

    /// <summary>The window's JavaScript, TypeScript, HTML, CSS and JSON, for a web file shown.</summary>
    public Func<WebLanguages?>? Web
    {
        get => context.Web;
        set => context.Web = value;
    }

    /// <summary>The window's XAML, for a XAML file shown.</summary>
    public Func<XamlService?>? Xaml
    {
        get => context.Xaml;
        set => context.Xaml = value;
    }

    /// <summary>The window's MSBuild code help, for a project file shown.</summary>
    public Func<MSBuildService?>? MSBuild
    {
        get => context.MSBuild;
        set => context.MSBuild = value;
    }

    /// <summary>The solution's projects, which a Dockerfile's project is found among.</summary>
    public Func<IReadOnlyList<ProjectModel>>? Projects
    {
        get => context.Languages.Compose.Projects;
        set => context.Languages.Compose.Projects = value;
    }

    /// <summary>The projects a settings file or a secrets.json configures (the session's <see cref="ProjectSession.ConfiguredBy"/>).</summary>
    public Func<string, IReadOnlyList<ConfigurationProject>>? ConfiguredBy
    {
        get => context.Languages.Settings.ConfiguredBy;
        set => context.Languages.Settings.ConfiguredBy = value;
    }

    /// <summary>The configuration of the project a C# file is of (the session's <see cref="ProjectSession.ConfigurationOf(string)"/>).</summary>
    public Func<string, ConfigurationProject?>? ConfigurationOf
    {
        get => context.Languages.CSharp.ConfigurationOf;
        set => context.Languages.CSharp.ConfigurationOf = value;
    }

    /// <summary>The routes the request on a line (the editor's, from zero) of an .http file calls.</summary>
    public Func<string, int, IReadOnlyList<LspLocation>>? HttpRouteTargets
    {
        get => context.Languages.Http.HttpRouteTargets;
        set => context.Languages.Http.HttpRouteTargets = value;
    }

    /// <summary>The .http requests that call the route a file declares at an offset, with the route's words; null where it
    /// declares none.</summary>
    public Func<string, int, (string Title, IReadOnlyList<LspLocation> Requests)?>? RouteCallers
    {
        get => context.Languages.Http.RouteCallers;
        set => context.Languages.Http.RouteCallers = value;
    }

    /// <summary>The fixes Passenger offers itself at an offset of a file, which Show Context Actions lists before asking the
    /// language server; none by default.</summary>
    public Func<OpenDocument, int, IReadOnlyList<EditorFix>>? EditorFixes
    {
        get => actions.EditorFixes;
        set => actions.EditorFixes = value;
    }

    /// <summary>Asks the person for a value a fix sets (a title, what is asked, whether it is a secret, masked as typed);
    /// null when they gave none.</summary>
    public Func<string, string, bool, Task<string?>>? AskValue
    {
        get => actions.AskValue;
        set => actions.AskValue = value;
    }

    /// <summary>The templates in force, as Settings leaves them.</summary>
    public TemplateSet Templates
    {
        get => completion.Templates;
        set => completion.Templates = value;
    }

    /// <summary>The XAML code style as Settings leaves it, which a file's .editorconfig overrides.</summary>
    public XamlFormatSettings XamlFormat
    {
        get => context.Languages.Xaml.XamlFormat;
        set => context.Languages.Xaml.XamlFormat = value;
    }

    private CodeEditor Editor => context.Editor;

    /// <summary>Keys the open popups take before the editor does: the completion list's Up, Down, Enter,
    /// Tab and Escape, and Escape for a card.</summary>
    public bool TakeKey(string action)
    {
        if (completion.TakeKey(action))
        {
            return true;
        }
        if (action == EditorActions.Escape && (parameterInfo.IsVisible || quickInfo.IsVisible))
        {
            parameterInfo.Hide(cancel: false);
            quickInfo.Hide();
            return true;
        }
        return false;
    }

    /// <summary>The code actions, on the active editor's caret: those that ask the language server need a C# file of a
    /// folder it runs for; the error steps and Quick Documentation (the problems there, a paused program's values) only a
    /// file.</summary>
    public void AddActions(ActionRegistry registry)
    {
        WebAssist web = context.Languages.Web;
        Code(AppActions.Complete, "Basic Completion", context => IdeErrors.Observe(completion.Start(null)), new("Code/Code Completion", 0, 0), xaml: true, msbuild: true);
        Code(AppActions.CompleteStatement, "Complete Current Statement", _ => completion.CompleteStatement(), new("Code/Code Completion", 1, 0), server: false, syntax: true);
        // Passenger's own fixes first, which a file the server does not read (a XAML file) may have too.
        registry.Add(new IdeAction(AppActions.QuickFix, "Show Context Actions", "Code", context => IdeErrors.Observe(actions.ShowFixes()))
        {
            WhyNot = context => actions.OwnFixes(context.File, context.Editor.Selections.Primary.Caret).Count > 0 ? null : WhyNot(context, server: true, xaml: true),
            Menus = [new("Code", 0, 1)],
        });
        // Generate: the server's actions that write code for the type (constructors, equality, members, overrides).
        Code(AppActions.Generate, "Generate…", context => IdeErrors.Observe(actions.ShowFixes(a => RoslynNames.Generate.Any(w => a.Title.StartsWith(w, StringComparison.Ordinal)), "Generate")),
            new("Code", 0, 2));
        Code(AppActions.SurroundWith, "Surround With…", _ => completion.ShowSurround(), new("Code", 0, 3), server: false, syntax: true, markup: true);
        // A file Prettier formats (Run on 'Reformat Code' action) needs no language server.
        registry.Add(new IdeAction(AppActions.Reformat, "Reformat Code", "Code", context => IdeErrors.Observe(actions.Reformat()))
        {
            WhyNot = context => web.PrettierReformats(context.File) ? (context.Editor.IsReadOnly ? "The file is read-only" : null) : WhyNot(context, server: true, markup: true),
            Menus = [new("Code", 1, 0)],
        });
        web.AddLintingActions(registry);
        Code(AppActions.CodeCleanup, "Code Cleanup", context => IdeErrors.Observe(actions.Cleanup()), new("Code", 1, 1), markup: true);
        Code(AppActions.OptimizeUsings, "Optimize Imports", context => IdeErrors.Observe(actions.OptimizeUsings()), new("Code", 1, 2));
        Code(AppActions.RefactorThis, "Refactor This…", context => IdeErrors.Observe(actions.ShowFixes(a => a.Kind?.StartsWith("refactor", StringComparison.Ordinal) == true, "Refactor This")),
            new("Refactor", 0, 0), xaml: true);
        Code(AppActions.ChangeSignature, "Change Signature…", context => IdeErrors.Observe(actions.ChangeSignature()), new("Refactor", 1, 1));
        Code(AppActions.ExtractMethod, "Extract Method…", context => IdeErrors.Observe(actions.Refactor(a => a.Title == RoslynNames.ExtractMethod, "Extract Method", renameMethod: true)),
            new("Refactor", 1, 2));
        Code(AppActions.IntroduceVariable, "Introduce Variable…",
            context => IdeErrors.Observe(actions.Refactor(a => a.Title.StartsWith(RoslynNames.IntroduceLocal, StringComparison.Ordinal), "Introduce Variable", renameMethod: false)), new("Refactor", 1, 3));
        Code(AppActions.IntroduceField, "Introduce Field…", context => IdeErrors.Observe(actions.Refactor(CodeActions.IntroduceField)), new("Refactor", 1, 4));
        Code(AppActions.IntroduceParameter, "Introduce Parameter…", context => IdeErrors.Observe(actions.Refactor(CodeActions.IntroduceParameter)), new("Refactor", 1, 5));
        Code(AppActions.ExtractInterface, "Extract Interface…", context => IdeErrors.Observe(actions.ExtractInterface()), new("Refactor", 1, 6));
        Code(AppActions.Inline, "Inline…",
            context => IdeErrors.Observe(actions.Refactor(a => a.Kind == RoslynNames.InlineKind || a.Title.StartsWith(RoslynNames.Inline, StringComparison.Ordinal), "Inline", renameMethod: false)), new("Refactor", 1, 7));
        Code(AppActions.Move, "Move…", context => IdeErrors.Observe(actions.Move()), new("Refactor", 2, 0));
        Code(AppActions.SafeDelete, "Safe Delete…", context => IdeErrors.Observe(actions.SafeDelete()), new("Refactor", 2, 1));
        Code(AppActions.PullMembersUp, "Pull Members Up…", context => IdeErrors.Observe(actions.MoveMembers(up: true)), new("Refactor", 3, 0));
        Code(AppActions.PushMembersDown, "Push Members Down…", context => IdeErrors.Observe(actions.MoveMembers(up: false)), new("Refactor", 3, 1));
        Code(AppActions.EncapsulateField, "Encapsulate Field…", context => IdeErrors.Observe(actions.Refactor(CodeActions.EncapsulateField)), new("Refactor", 3, 2));
        registry.Add(new IdeAction(AppActions.ExtractResource, "Extract Resource…", "Code", context => IdeErrors.Observe(actions.ExtractResource()))
        {
            WhyNot = context => WhyNotXaml(context),
            Menus = [new("Refactor", 4, 0)],
        });
        registry.Add(new IdeAction(AppActions.ExtractStyle, "Extract Style…", "Code", context => IdeErrors.Observe(actions.ExtractStyle()))
        {
            WhyNot = context => WhyNotXaml(context),
            Menus = [new("Refactor", 4, 1)],
        });
        Code(AppActions.GoToDeclaration, "Go to Declaration", context => IdeErrors.Observe(navigation.GoToDefinition(Caret)), new("Navigate", 2, 0), xaml: true, msbuild: true, http: true);
        Code(AppActions.DerivedSymbols, "Derived Symbols", context => IdeErrors.Observe(navigation.GoToDerivedSymbols(Caret)), new("Navigate", 2, 1));
        Code(AppActions.GoToImplementation, "Go to Implementation", context => IdeErrors.Observe(navigation.GoToImplementation(Caret)), new("Navigate", 2, 2));
        Code(AppActions.GoToTypeDeclaration, "Go to Type Declaration", context => IdeErrors.Observe(navigation.GoToTypeDeclaration(Caret)), new("Navigate", 2, 3));
        Code(AppActions.BaseSymbols, "Base Symbols", context => IdeErrors.Observe(navigation.GoToBaseSymbols(Caret)), new("Navigate", 2, 4));
        Code(AppActions.FindUsages, "Find Usages", context => IdeErrors.Observe(navigation.FindUsages(Caret)), new("Navigate", 2, 5), xaml: true);
        Code(AppActions.ParameterInfo, "Parameter Info", context => IdeErrors.Observe(parameterInfo.Show()), new("View", 1, 2));
        Code(AppActions.NextError, "Next Highlighted Error", _ => navigation.StepToMarker(forward: true), new("Navigate", 3, 0), server: false);
        Code(AppActions.PreviousError, "Previous Highlighted Error", _ => navigation.StepToMarker(forward: false), new("Navigate", 3, 1), server: false);
        Code(AppActions.QuickDocumentation, "Quick Documentation", context => quickInfo.QuickDocumentation(), new("View", 1, 1), server: false);

        void Code(string id, string title, Action<ActionContext> run, MenuPlace menu, bool server = true, bool syntax = false, bool xaml = false, bool markup = false, bool msbuild = false,
            bool http = false) =>
            registry.Add(new IdeAction(id, title, "Code", run)
            {
                // An .http file's requests go to their routes, with no language server of its own.
                WhyNot = action => http && this.context.Languages.Http.IsHttpFile(action.File) ? null : WhyNot(action, server, syntax, xaml, markup, msbuild),
                Menus = [menu],
            });
    }

    private int Caret => Editor.Selections.Primary.Caret;

    /// <summary>Rename at the editor's caret: a C# symbol through the server, a XAML name and a compose file's of its own.</summary>
    public void Rename() => actions.Rename();

    /// <summary>Why Rename cannot run at the editor's caret; null when it can.</summary>
    public string? WhyNotRename(ActionContext context) => WhyNot(context, server: true, xaml: true);

    /// <summary>Why an action of a XAML file's alone cannot run: no file, a file not XAML of a project Passenger reads, or
    /// one that cannot be edited.</summary>
    private string? WhyNotXaml(ActionContext action) =>
        action.File is null ? Refusals.NoFile
        : context.XamlOf(action.File) is not { } markup || (markup.Loaded.IsCompleted && !markup.IsLoaded) ? "The file is not XAML of an Avalonia or WPF project"
        : action.Editor.IsReadOnly ? "The file is read-only"
        : null;

    /// <summary>Why an action cannot run: no file; for one that asks the server, none for the file; for one that edits by
    /// the file's syntax, a file not C# or one that cannot be edited. One that also edits XAML (<paramref name="markup"/>)
    /// takes any XAML file that can be edited; XAML and MSBuild files answer those they say they do.</summary>
    private string? WhyNot(ActionContext action, bool server, bool syntax = false, bool xaml = false, bool markup = false, bool msbuild = false) =>
        action.File is null ? Refusals.NoFile
        : markup && XamlService.IsXaml(action.File.Path) ? (action.Editor.IsReadOnly ? "The file is read-only" : null)
        : xaml && context.XamlOf(action.File) is { } markupFile && (markupFile.IsLoaded || !markupFile.Loaded.IsCompleted) ? null
        : msbuild && context.MSBuildOf(action.File) is not null ? null
        : syntax && action.File.Syntax is null ? "The file is not C#"
        : syntax && action.Editor.IsReadOnly ? "The file is read-only"
        : !server || context.Current is not null ? null
        : context.CSharp is null ? Refusals.NoCSharp
        : "The file is not C#";

    internal IReadOnlyList<EditorFix> OwnFixes(OpenDocument? file, int offset) => actions.OwnFixes(file, offset);

    /// <summary>A fix's changes to files made as one command (<see cref="CodeActions.ApplyFileChanges"/>): null once made, else why not.</summary>
    internal string? ApplyFileChanges(string name, IReadOnlyList<XamlFileChange> changes) => actions.ApplyFileChanges(name, changes);

    /// <summary>A XAML project was read anew: a list made from it as it was is asked again (<see cref="XamlAssist.ProjectRead"/>).</summary>
    internal void XamlProjectRead(XamlProject project) => context.Languages.Xaml.ProjectRead(project);

    /// <summary>What a save does first to a changed file, as Settings ask: <c>eslint --fix</c>, then Prettier. Each its
    /// own undoable edit.</summary>
    public Task BeforeSave(OpenDocument document) => context.Languages.Web.BeforeSave(document);

    /// <summary>A service's or an AppHost's Aspire fixes on an offset's line (<see cref="CSharpAssist.AspireFixes"/>).</summary>
    public IReadOnlyList<EditorFix> AspireFixes(OpenDocument file, int offset) => context.Languages.CSharp.AspireFixes(file, offset);

    /// <summary>A C# file's fixes that set a connection string in a compose file (<see cref="ComposeAssist.ComposeReadFixes"/>).</summary>
    public IReadOnlyList<EditorFix> ComposeReadFixes(OpenDocument file, int offset) => context.Languages.Compose.ComposeReadFixes(file, offset);

    /// <summary>A C# file's fixes that give a configuration key it reads (<see cref="CSharpAssist.ConfigurationFixes"/>).</summary>
    public IReadOnlyList<EditorFix> ConfigurationFixes(OpenDocument file, int offset) => context.Languages.CSharp.ConfigurationFixes(file, offset);

    /// <summary>A file's fixes that give a key a language lacks (<see cref="CSharpAssist.LocalizationFixes"/>).</summary>
    public IReadOnlyList<EditorFix> LocalizationFixes(OpenDocument file, int offset) => context.Languages.CSharp.LocalizationFixes(file, offset);

    /// <summary>A XAML colour's badge in the completion list: the colour itself, the content's own; null for no colour.</summary>
    internal static IBrush? Swatch(string? colour) =>
        KnownColours.Parse(colour) is { } known ? new SolidColorBrush(Color.FromArgb(known.A, known.R, known.G, known.B)) : null;

    private void CloseAll()
    {
        completion.Close();
        quickInfo.Hide();
        parameterInfo.Hide();
        popups.CloseModal();
    }
}

/// <summary>What the debugger's hover card says of what the pointer rests on: its value as code, and what went wrong reading
/// it (a binding that failed), said first in the error colour; a value with members as its row instead (<see cref="Values"/>).</summary>
public sealed record DebuggerHover(string Code, string? Problem = null)
{
    /// <summary>A value with members as its row of a stop's values, which opens in the card as the Variables view's rows do.</summary>
    public Debugging.VariablesList? Values { get; init; }

    /// <summary>Cancelled once the stop the value was read at is gone: the card goes with it.</summary>
    public CancellationToken Gone { get; init; }
}

