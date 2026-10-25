using Passenger.App.CSharp;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.App.Xaml;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;
using Passenger.Core;

namespace Passenger.App.Mcp;

/// <summary>The code tools for a file of any language Passenger serves, each through that language's own service as the
/// window's commands go: C# and Razor through Roslyn's server, XAML (Avalonia's and WPF's) through Passenger's own, the web
/// languages through their servers with ESLint and Prettier, MSBuild files through their project's evaluation. What a
/// language lacks is said plainly, naming the language.</summary>
public sealed partial class PassengerTools
{
    /// <summary>A file and the service that answers for it.</summary>
    private abstract record Served(OpenDocument Document, string Language);

    /// <summary>A file a language server answers for: C# or Razor's, or a web language's (<paramref name="Web"/>).</summary>
    private sealed record ServerServed(OpenDocument Document, string Language, LspDocument Lsp, WebDocument? Web = null) : Served(Document, Language)
    {
        /// <summary>Whether the file's server answers a request; C#'s answers every one the tools ask.</summary>
        public bool Provides(string capability) => Web?.Provides(capability) ?? true;
    }

    /// <summary>A XAML file of an Avalonia or WPF project, read as it stands.</summary>
    private sealed record XamlServed(OpenDocument Document, XamlDocument Xaml, XamlFile File) : Served(Document, "XAML");

    /// <summary>A project, .props or .targets file, read through its project's evaluation.</summary>
    private sealed record MSBuildServed(OpenDocument Document, MSBuild.MSBuildDocument MSBuild) : Served(Document, "MSBuild");

    /// <summary>What a tool says of a language that has no such command: plainly, naming the language and what it has instead.</summary>
    private static InvalidOperationException Lacks(Served served, string command, string instead) =>
        new($"{served.Language} files have no {command} in Passenger, so {Path.GetFileName(served.Document.Path)} has none: {instead}");

    private const string MSBuildHas = "definition goes to where a property, item, target, import or SDK is defined, symbol_info says a property's "
        + "value and where it was set, outline gives its elements, and diagnostics its evaluation's problems";

    /// <summary>The file opened as the editor would, with the service that answers for it once that can: a XAML file's
    /// project read, the C# server's workspace loaded. A file no service reads is refused at once, saying which do.</summary>
    private async Task<Served> Serve(string path, CancellationToken cancel)
    {
        OpenDocument document = await DocumentAsync(path, cancel);
        string name = Path.GetFileName(document.Path);
        if (XamlService.IsXaml(document.Path))
        {
            if (Session.Xaml.For(document) is not { } xaml)
            {
                throw new InvalidOperationException($"{name} is in no C# project, so Passenger's XAML does not read it");
            }
            XamlFile? file = await xaml.FileAsync().WaitAsync(ServerLimit, cancel);
            return file is null
                ? throw new InvalidOperationException($"{name} is not XAML of an Avalonia or WPF project: Passenger reads the XAML of a project that references Avalonia or WPF")
                : new XamlServed(document, xaml, file);
        }
        if (Session.MSBuild.For(document) is { } msbuild)
        {
            return new MSBuildServed(document, msbuild);
        }
        if (Session.Web is { } languages && languages.For(document) is { } web)
        {
            // Looked at every 50 ms: a web server's document comes as the server starts, raising nothing.
            await Until(() => web.Lsp is not null || languages.CannotStart(document), ServerLimit, cancel);
            return web.Lsp is { } server
                ? new ServerServed(document, LanguageOf(document), server, web)
                : throw new InvalidOperationException(languages.Unready("Its code help", document));
        }
        if (document.Language.Service == Languages.LanguageService.CSharp)
        {
            (_, LspDocument lsp) = await Code(path, cancel);
            return new ServerServed(document, document.Language.Name, lsp);
        }
        throw new InvalidOperationException($"Passenger has no code help for {LanguageOf(document)} files such as {name}: the code tools answer C#, Razor, "
            + "XAML, JavaScript, TypeScript, HTML, CSS, SCSS, Less, JSON, YAML, Dockerfiles and MSBuild files");
    }

    /// <summary>Where Go to Declaration goes from a name in an MSBuild file: where a property was set (the one in force
    /// first), an item type or target defined, the files an import or SDK brings in (the SDK's own, read-only).</summary>
    private async Task<IReadOnlyList<Place>> MSBuildDefinition(MSBuildServed msbuild, int offset, CancellationToken cancel) =>
        await msbuild.MSBuild.TargetsAsync(offset) is { } found
            ? await PlacesOf([.. found.Targets.Select(t => (t.Place.Path, new TextPosition(Math.Max(0, t.Place.Line - 1), Math.Max(0, t.Place.Column - 1))))], cancel)
            : [];

    /// <summary>An MSBuild file's elements, indented, each with its line: a property or metadata with its value as written,
    /// an item with what it includes, a target, an import or an SDK with what names it.</summary>
    private static string MSBuildOutline(MSBuildServed msbuild)
    {
        XmlTree tree = msbuild.MSBuild.Tree;
        TextSnapshot snapshot = TextSnapshot.From(tree.Text);
        System.Text.StringBuilder text = new();
        foreach (XmlElement root in tree.Roots)
        {
            Write(root, 0);
        }
        return text.Length == 0 ? "The file has no elements" : text.ToString().TrimEnd();

        void Write(XmlElement element, int depth)
        {
            MSBuild.MSBuildRole role = MSBuild.MSBuildSyntax.RoleOf(element);
            text.Append(' ', depth * 2).Append(element.Name);
            if (role is MSBuild.MSBuildRole.Property or MSBuild.MSBuildRole.Metadata && element.Children.Count == 0)
            {
                text.Append(" = ").Append(MSBuild.MSBuildNavigation.Shown(tree.Text[element.Content.Start..element.Content.End]));
            }
            foreach (XmlAttribute attribute in element.Attributes.Where(a => a.Name is "Sdk" or "Name" or "Project" or "Include" or "Update" or "Remove" or "Version" or "Label"))
            {
                text.Append(' ').Append(attribute.Name).Append("=\"").Append(attribute.Value).Append('"');
            }
            string kind = role switch
            {
                MSBuild.MSBuildRole.Property => "property, ",
                MSBuild.MSBuildRole.Item => "item, ",
                MSBuild.MSBuildRole.Metadata => "metadata, ",
                MSBuild.MSBuildRole.Task => "task, ",
                _ when element.LocalName == "Target" => "target, ",
                _ when element.LocalName == "Import" => "import, ",
                _ => string.Empty,
            };
            text.Append(" (").Append(kind).Append("line ").Append(snapshot.GetLineFromOffset(Math.Min(element.Start, snapshot.Length)) + 1).Append(")\n");
            foreach (XmlElement child in element.Children)
            {
                Write(child, depth + 1);
            }
        }
    }

    /// <summary>A file's language as the tools name it: XAML, MSBuild and the web languages by what reads them, the rest as
    /// the editor names them.</summary>
    private static string LanguageOf(OpenDocument document) => Languages.Language.ForPath(document.Path) switch
    {
        { Service: Languages.LanguageService.Xaml } => "XAML",
        { Service: Languages.LanguageService.MSBuild } => "MSBuild",
        { Service: not (Languages.LanguageService.None or Languages.LanguageService.CSharp) } web => web.Name,
        _ => document.Language.Name,
    };

    /// <summary>Go to Declaration in JSON, whose server has none: a package.json dependency's package, a script's run.</summary>
    private async Task<IReadOnlyList<Place>> JsonDefinition(ServerServed json, int offset, CancellationToken cancel)
    {
        // A settings file's key: the options property it sets, else what reads it, as the window's goes.
        if (await window.Assist.Languages.Settings.SettingsDeclarationsAsync(json.Document, offset) is { } settings)
        {
            return await PlacesOf(settings, cancel);
        }
        ((string Path, TextPosition Position)? place, string said) = await WebAssist.JsonDeclarationAsync(json.Lsp, json.Document, offset);
        return place is { } found ? await PlacesOf([found], cancel)
            : said == Refusals.NoDeclaration ? []
            : throw new InvalidOperationException(said);
    }

    /// <summary>Passenger's own Go to Declaration in a compose file, as the window's goes; null where it finds nothing,
    /// for the YAML server's own (an alias's anchor) to be asked. An image's registry page is said, not gone to.</summary>
    private async Task<IReadOnlyList<Place>?> ComposeDefinition(Served served, int offset, CancellationToken cancel)
    {
        if (served is not ServerServed { Web.Kind: WebServerKind.Yaml } compose || !ComposeDeclarations.IsCompose(compose.Document.Path))
        {
            return null;
        }
        ComposeAnswer answer = await ComposeAssist.ComposeDeclarationAsync(compose.Lsp, compose.Document, offset,
            ServerPlaces.Texts(Editors), Session.Workspace?.Projects ?? []);
        if (answer.Page is { } page)
        {
            throw new InvalidOperationException($"{answer.Said} is declared on its registry, not in the folder: its page is {page}");
        }
        return answer.Places.Count > 0 ? await PlacesOf([.. answer.Places.Select(p => (p.Path, p.Position))], cancel)
            : answer.Said == Refusals.NoDeclaration ? null
            : throw new InvalidOperationException(answer.Said);
    }

    /// <summary>Aspire's Go to Declaration in an AppHost's or a service's C# file, as the window's goes: AddProject's project,
    /// a connection string's or service's name to the AppHost line declaring it; null elsewhere, for the server's own.</summary>
    private async Task<IReadOnlyList<Place>?> AspireDefinition(Served served, int offset, CancellationToken cancel) =>
        served is ServerServed { Web: null } csharp && csharp.Document.Language == Languages.Language.CSharp && Session.AspireFiles.RoleOf(csharp.Document.Path) != Aspire.AspireRole.None
            && await CSharpAssist.AspireDeclarationAsync(csharp.Lsp, offset, ServerPlaces.Texts(Editors)) is { } place
            ? await PlacesOf([place], cancel) : null;

    /// <summary>A localized key's Go to Declaration in a C# or Razor file, as the window's goes: each language's entry of it, a
    /// generated class's property as the server declares it first; null elsewhere, for the server's own.</summary>
    private async Task<IReadOnlyList<Place>?> LocalizedDefinition(Served served, int offset, CancellationToken cancel)
    {
        if (served is not ServerServed { Web: null } code || await window.Assist.Languages.CSharp.LocalizedDeclarationsAsync(code.Document, offset) is not { } found)
        {
            return null;
        }
        IReadOnlyList<Place> declared = found.Generated ? await Places(await code.Lsp.DefinitionAsync(offset), cancel) : [];
        return [.. declared, .. await PlacesOf([.. found.Places.Select(p => (p.Path, p.Position))], cancel)];
    }

    /// <summary>An AppHost's or a service's C# file's diagnostics pulled and marked now, a file reading what an open compose
    /// file's service lacks or one that may read configuration or localized strings too, so the fixes Passenger offers on
    /// Aspire's, Compose's, a configuration key's and a translation's warnings stand on the text as the agent wrote it, not
    /// on a pull still to come.</summary>
    private async Task AspireMarked(Served served, CancellationToken cancel)
    {
        if (served is ServerServed { Web: null } && Session.CSharp?.For(served.Document) is { } csharp
            && (csharp.AspireRole != Aspire.AspireRole.None || csharp.ReadsForCompose || csharp.MayReadConfiguration || csharp.MayLocalize))
        {
            await csharp.PullNowAsync(cancel);
        }
    }

    /// <summary>Rename in JSON, whose server has none: the property's name, and in a package.json's scripts every run of a
    /// script renamed with it, one undoable edit, saved.</summary>
    private async Task<string> JsonRename(ServerServed json, int offset, string newName)
    {
        OpenDocument document = json.Document;
        using ChangeTracker asked = new(document.Text);
        IReadOnlyList<DocumentSymbol> outline = await json.Lsp.OutlineAsync();
        ServerLines lines = ServerLines.Of(asked.Snapshot);
        if (WebAssist.JsonPropertyAt(outline, lines, offset) is not { } at)
        {
            throw new InvalidOperationException("Nothing to rename here: JSON's Rename renames a property, so put the place on its name");
        }
        string old = at.Path[^1].Name;
        if (asked.Map(WebAssist.JsonRenameEdits(at.Path, at.Key, lines, newName, WebAssist.IsPackageJson(document))) is not { } placed)
        {
            throw new InvalidOperationException("Rename was not applied: the property was edited meanwhile");
        }
        WorkspaceEdits.ApplyTo(Editors, document, placed);
        document.Save();
        int runs = placed.Count - 1;
        return $"Renamed {old} to {newName} in {Relative(document.Path)}{(runs == 0 ? string.Empty : $", and in {Plural.Of(runs, "script")} that run{(runs == 1 ? "s" : "")} it")}";
    }

    /// <summary>ESLint's fixes for the problems on a web file's line, which Show Context Actions lists first; none where ESLint
    /// does not check the file.</summary>
    private async Task<IReadOnlyList<EslintFix>> LintFixes(Served served, int offset) =>
        served is ServerServed { Web: not null } ? await window.Assist.Languages.Web.LintFixes(served.Document, offset) : [];

    /// <summary>One of ESLint's fixes made, as choosing it makes it: its server's command, which hands the window its edit
    /// before it answers; the file saved.</summary>
    private async Task<string> ApplyLintFix(ServerServed web, EslintFix fix)
    {
        if (fix.OpensDocumentation)
        {
            return $"'{fix.Title}' opens the rule's page in a browser, and changes nothing";
        }
        if (Session.Web?.Eslint.For(web.Document) is not { } lint)
        {
            throw new InvalidOperationException($"'{fix.Title}' was not applied: ESLint no longer checks {Path.GetFileName(web.Document.Path)}");
        }
        int version = web.Document.Text.Version;
        await lint.RunAsync(fix);
        if (web.Document.Text.Version == version)
        {
            return $"'{fix.Title}' changed nothing";
        }
        Editors.SaveAll();
        return $"Applied '{fix.Title}' to {Relative(web.Document.Path)}";
    }

    /// <summary>A XAML name at an offset, with the places it leads; null where there is none.</summary>
    private static async Task<XamlSymbol?> XamlSymbolAt(XamlServed xaml, int offset) =>
        await BackgroundWork.Run(() => XamlNavigation.At(xaml.File, offset)) is { Targets.Count: > 0 } symbol ? symbol : null;

    /// <summary>What the hover card says of a XAML name: the type, property, resource or element it is.</summary>
    private static async Task<string> XamlSymbolInfo(XamlServed xaml, int offset) =>
        await XamlSymbolAt(xaml, offset) is { } symbol ? XamlNavigation.Describe(xaml.File, symbol) : "Nothing is known about this position";

    /// <summary>Where Go to Declaration goes from a XAML name: the project's source or XAML, a library's type decompiled (read
    /// only), the theme's XAML from its Source Link, a localized string's entry in each language's file; why not, where none of
    /// the places can be had.</summary>
    private async Task<IReadOnlyList<Place>> XamlDefinition(XamlServed xaml, int offset, CancellationToken cancel)
    {
        if (await XamlSymbolAt(xaml, offset) is not { } symbol)
        {
            return [];
        }
        List<(string Path, TextPosition Position)> places = [];
        List<string> unreached = [];
        foreach (XamlTarget target in (IReadOnlyList<XamlTarget>)[.. symbol.Targets, .. await window.Assist.Languages.Xaml.XamlLocalizedTargetsAsync(xaml.File, offset)])
        {
            ((string Path, TextPosition Position)? place, string? said) = await window.Assist.Languages.Xaml.XamlPlaceAsync(target, xaml.File);
            if (place is { } found)
            {
                places.Add(found);
            }
            else if (said is not null)
            {
                unreached.Add(said);
            }
        }
        return places.Count == 0 && unreached.Count > 0
            ? throw new InvalidOperationException($"{symbol.Name}'s declaration cannot be reached: {string.Join("; ", unreached)}")
            : await PlacesOf(places, cancel);
    }

    /// <summary>Find Usages in XAML: an element's name in its file and its code-behind, a resource key in the project's XAML.</summary>
    private async Task<IReadOnlyList<Place>> XamlUsages(XamlServed xaml, int offset, CancellationToken cancel)
    {
        if (await XamlSymbolAt(xaml, offset) is not { } symbol)
        {
            return [];
        }
        if (symbol.Named is not null)
        {
            await CodeBehindRead(cancel);
        }
        return await window.Assist.Languages.Xaml.XamlUsesAsync(xaml.File, symbol) is { } uses
            ? await Places(uses, cancel)
            : throw new InvalidOperationException($"{XamlAssist.XamlUsesWhat}; {symbol.Name} is neither");
    }

    /// <summary>Waits for the C# server to load the workspace, when one runs: an element's field is found in the code-behind
    /// through it, and before it has loaded none are.</summary>
    private Task CodeBehindRead(CancellationToken cancel) => When(() => Session.CSharp is not { } csharp || csharp.IsReady, ServerLimit, cancel, ServerState());

    /// <summary>Rename in XAML: an element's name, in its file and where its code-behind uses its field, saved.</summary>
    private async Task<string> XamlRename(XamlServed xaml, int offset, string newName, CancellationToken cancel)
    {
        await CodeBehindRead(cancel);
        if (await BackgroundWork.Run(() => XamlNavigation.At(xaml.File, offset)) is not { Named: { } named })
        {
            throw new InvalidOperationException("Rename in XAML renames an element's name (its x:Name or Name): put the place on one, its value or a use of it");
        }
        if (named.Value == newName)
        {
            return $"{newName} is its name already";
        }
        if (await window.Assist.Languages.Xaml.XamlRenameAsync(xaml.Xaml, named.Value, newName) is not { } renamed)
        {
            throw new InvalidOperationException($"{Path.GetFileName(xaml.Document.Path)} is no longer XAML Passenger reads");
        }
        if (renamed.Files.Count == 0)
        {
            throw new InvalidOperationException(renamed.Said);
        }
        Editors.SaveAll();
        return $"Renamed {named.Value} to {newName} in {Plural.Of(renamed.Files.Count, "file")}: {string.Join(", ", renamed.Files.Select(Relative))}";
    }

    /// <summary>A XAML file's elements, indented, each with what names it (x:Class, x:Name, x:Key, a style's selector or
    /// target type) and its line.</summary>
    private static string XamlOutline(XamlServed xaml)
    {
        System.Text.StringBuilder text = new();
        TextSnapshot snapshot = TextSnapshot.From(xaml.File.Text);
        foreach (XmlElement root in xaml.File.Tree.Roots)
        {
            Write(root, 0);
        }
        return text.Length == 0 ? "The file has no elements" : text.ToString().TrimEnd();

        void Write(XmlElement element, int depth)
        {
            text.Append(' ', depth * 2).Append(element.Name);
            foreach (XmlAttribute attribute in element.Attributes.Where(a => a.Name is "x:Class" or "x:Name" or "Name" or "x:Key" or "Selector" or "TargetType" or "x:DataType"))
            {
                text.Append(' ').Append(attribute.Name).Append("=\"").Append(attribute.Value).Append('"');
            }
            text.Append(" (line ").Append(snapshot.GetLineFromOffset(Math.Min(element.Start, snapshot.Length)) + 1).Append(")\n");
            foreach (XmlElement child in element.Children)
            {
                Write(child, depth + 1);
            }
        }
    }

    /// <summary>One of Passenger's own fixes made as choosing it makes it: worked out again on the file's text as it stands,
    /// one undoable edit, the file saved; refused when the text no longer has what it fixes. One made in another file (an
    /// AppHost's WithReference) is that file's command, told the call's <paramref name="patience"/>.</summary>
    private async Task<string> ApplyOwnFix(OpenDocument document, EditorFix fix, Patience? patience = null, string? value = null)
    {
        if (fix.On(document.Text.Snapshot) is not { } now)
        {
            throw new InvalidOperationException($"'{fix.Title}' was not applied: the file changed, so it no longer fits");
        }
        if (now.Asks is { } asks)
        {
            RefactorOutcome set = await asks.Make(value ?? throw new InvalidOperationException($"'{fix.Title}' sets a value: give it as name"), patience);
            if (set.Problem is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
            Editors.SaveAll();
            return set.Said;
        }
        if (now.Elsewhere is { } elsewhere)
        {
            RefactorOutcome made = await elsewhere(now.Range, patience);
            if (made.Problem is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            Editors.SaveAll();
            return $"Applied '{fix.Title}' to {string.Join(", ", made.Files.Select(f => Relative(f.Path)).Distinct())}";
        }
        WorkspaceEdits.ApplyTo(Editors, document, [new TextEdit(now.Range.Start, now.Range.Length, now.Text)]);
        Editors.SaveAll();
        return $"Applied '{fix.Title}' to {Relative(document.Path)}";
    }

    /// <summary>One of XAML's fixes or refactorings at a place, as code_actions lists it and apply_code_action makes it.</summary>
    private sealed record XamlAction(string Title, string Kind, IReadOnlyList<string> Choices, object Made);

    /// <summary>What Show Context Actions offers at a place of a XAML file: a running app's binding fixes, the fixes of the
    /// problems on its line (create from usage among them), then Extract Resource and Extract Style where they apply, each
    /// with the places the new resource or style may go.</summary>
    private async Task<IReadOnlyList<XamlAction>> XamlActions(XamlServed xaml, int offset)
    {
        // Passenger's own fixes (a translation a language lacks) stand on the file's check of the text as the agent wrote it.
        await xaml.Xaml.CheckedAsync();
        List<XamlAction> offered = [.. window.Assist.OwnFixes(xaml.Document, offset).Select(f => new XamlAction(f.Title, "quickfix", [], f))];
        XamlFile file = xaml.File;
        IReadOnlyList<XamlDiagnostic> found = await BackgroundWork.Run(() => XamlDiagnostics.Of(file));
        TextSnapshot text = TextSnapshot.From(file.Text);
        int line = text.GetLineFromOffset(Math.Min(offset, text.Length));
        offered.AddRange(found.Where(d => text.GetLineFromOffset(Math.Min(d.Range.Start, text.Length)) == line)
            .SelectMany(d => d.Fixes).Select(f => new XamlAction(f.Title, "quickfix", [], f)));
        Func<string, string?> texts = Editors.Texts().Text;
        if (await BackgroundWork.Run(() => XamlRefactorings.ValueAt(file, offset).Value) is { } value)
        {
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: false, texts));
            offered.Add(new XamlAction(ExtractResource, "refactor.extract", [.. places.Select(p => p.Title)], value));
        }
        if (await BackgroundWork.Run(() => XamlRefactorings.StyleAt(file, offset).Source) is { } source)
        {
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: source.ByClass, texts));
            offered.Add(new XamlAction(ExtractStyle, "refactor.extract", [.. places.Select(p => p.Title)], source));
        }
        return offered;
    }

    private const string ExtractResource = "Extract Resource…";

    private const string ExtractStyle = "Extract Style…";

    /// <summary>A XAML fix or refactoring made as choosing it in the window makes it, as one command, and the files saved:
    /// Extract Resource and Extract Style take the dialog's defaults (every use replaced; the element's properties that are
    /// not bindings) with the place chosen and the name given, else the one the dialog suggests.</summary>
    private async Task<string> ApplyXamlAction(XamlServed xaml, int offset, string title, string? choice, string? name)
    {
        IReadOnlyList<XamlAction> actions = await XamlActions(xaml, offset);
        XamlAction chosen = actions.FirstOrDefault(a => a.Title == title)
            ?? throw new InvalidOperationException($"No action '{title}' here; there are: {(actions.Count == 0 ? "none" : string.Join("; ", actions.Select(a => a.Title)))}");
        string? refused;
        string what = $"'{title}'";
        switch (chosen.Made)
        {
            case EditorFix fix:
                return await ApplyOwnFix(xaml.Document, fix, value: name);
            case XamlFix fix:
                IReadOnlyList<XamlFileChange> changes = [new XamlFileChange(fix.Path, fix.Edits), .. fix.Others];
                refused = window.Assist.Actions.ApplyFileChanges(fix.Title, changes);
                if (refused is not null)
                {
                    throw new InvalidOperationException($"{what} was not applied: {refused}");
                }
                Editors.SaveAll();
                return $"Applied {what} to {string.Join(", ", changes.Select(c => Relative(c.Path)).Distinct())}";
        }
        Func<string, string?> texts = Editors.Texts().Text;
        if (chosen.Made is ExtractableValue value)
        {
            ResourcePlace place = PlaceChosen(await BackgroundWork.Run(() => XamlRefactorings.Places(xaml.File, styles: false, texts)));
            string key = name ?? value.Suggested;
            if (XamlRefactorings.KeyProblem(xaml.File, key) is { } problem)
            {
                throw new InvalidOperationException($"{what} cannot name the resource '{key}': {problem}");
            }
            refused = window.Assist.Actions.ApplyFileChanges("Extract Resource", XamlRefactorings.ExtractResource(xaml.File, value, key, place, everywhere: true));
            if (refused is not null)
            {
                throw new InvalidOperationException($"{what} was not applied: {refused}");
            }
            Editors.SaveAll();
            return $"Extracted resource '{key}' to {place.Title}{(value.Uses.Count > 1 ? $", used in {value.Uses.Count} places" : string.Empty)}";
        }
        StyleSource source = (StyleSource)chosen.Made;
        ResourcePlace into = PlaceChosen(await BackgroundWork.Run(() => XamlRefactorings.Places(xaml.File, styles: source.ByClass, texts)));
        string style = name ?? source.Suggested;
        List<string> properties = [.. source.Properties.Where(p => !p.IsBinding).Select(p => p.Name)];
        if (properties.Count == 0)
        {
            throw new InvalidOperationException($"{what} has no property to extract: {source.ElementName}'s are all bindings");
        }
        if (XamlRefactorings.StyleNameProblem(xaml.File, source, style) is { } named)
        {
            throw new InvalidOperationException($"{what} cannot name the style '{style}': {named}");
        }
        refused = window.Assist.Actions.ApplyFileChanges("Extract Style", XamlRefactorings.ExtractStyle(xaml.File, source, style, properties, into));
        if (refused is not null)
        {
            throw new InvalidOperationException($"{what} was not applied: {refused}");
        }
        Editors.SaveAll();
        return $"Extracted {Plural.Of(properties.Count, "property", "properties")} of {source.ElementName} ({string.Join(", ", properties)}) to style '{style}' in {into.Title}";

        ResourcePlace PlaceChosen(IReadOnlyList<ResourcePlace> places) => choice is null
            ? places.Count == 1 ? places[0] : throw new InvalidOperationException($"{what} needs a choice of where it goes: {string.Join("; ", places.Select(p => p.Title))}")
            : places.FirstOrDefault(p => p.Title == choice) ?? throw new InvalidOperationException($"{what} cannot go in '{choice}'; it can go in: {string.Join("; ", places.Select(p => p.Title))}");
    }

    /// <summary>Places in files as an agent reads them, with each line's text.</summary>
    private Task<IReadOnlyList<Place>> PlacesOf(IReadOnlyList<(string Path, TextPosition Position)> places, CancellationToken cancel) =>
        Lines([.. places.Select(p => (DocumentUris.FromPath(p.Path), new LspPosition(p.Position.Line, p.Position.Column), false))], cancel);
}
