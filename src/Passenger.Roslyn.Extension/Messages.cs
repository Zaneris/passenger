using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

// Messages to Passenger's extension in Roslyn's server, compiled into both; names given for System.Text.Json's defaults.

/// <summary>The handlers' names, as the server knows them: their types' full names.</summary>
public static class Handlers
{
    public const string DescribeSignature = "Passenger.Roslyn.Extension.DescribeSignature";
    public const string ChangeSignature = "Passenger.Roslyn.Extension.ChangeSignature";
    public const string DescribeInterface = "Passenger.Roslyn.Extension.DescribeInterface";
    public const string ExtractInterface = "Passenger.Roslyn.Extension.ExtractInterface";
    public const string DescribeType = "Passenger.Roslyn.Extension.DescribeType";
    public const string MoveTypeToFile = "Passenger.Roslyn.Extension.MoveTypeToFile";
    public const string MoveTypeToNamespace = "Passenger.Roslyn.Extension.MoveTypeToNamespace";
    public const string DescribeHierarchy = "Passenger.Roslyn.Extension.DescribeHierarchy";
    public const string PullMembersUp = "Passenger.Roslyn.Extension.PullMembersUp";
    public const string PushMembersDown = "Passenger.Roslyn.Extension.PushMembersDown";
    public const string DocumentTexts = "Passenger.Roslyn.Extension.DocumentTexts";
    public const string DocumentsWritten = "Passenger.Roslyn.Extension.DocumentsWritten";
    public const string ReferencesRead = "Passenger.Roslyn.Extension.ReferencesRead";
    public const string ReferencedFiles = "Passenger.Roslyn.Extension.ReferencedFiles";
    public const string AspireProjectDeclaration = "Passenger.Roslyn.Extension.AspireProjectDeclaration";
    public const string AspireCodeHelp = "Passenger.Roslyn.Extension.AspireCodeHelp";
    public const string AspireReferenceFix = "Passenger.Roslyn.Extension.AspireReferenceFix";
    public const string AspireRunResources = "Passenger.Roslyn.Extension.AspireRunResources";
    public const string AspireRename = "Passenger.Roslyn.Extension.AspireRename";
    public const string AspireParameters = "Passenger.Roslyn.Extension.AspireParameters";
    public const string ConnectionReads = "Passenger.Roslyn.Extension.ConnectionReads";
    public const string WebRoutes = "Passenger.Roslyn.Extension.WebRoutes";
    public const string RouteBody = "Passenger.Roslyn.Extension.RouteBody";
    public const string LocalizedReads = "Passenger.Roslyn.Extension.LocalizedReads";
    public const string SymbolsNamed = "Passenger.Roslyn.Extension.SymbolsNamed";
    public const string SymbolAt = "Passenger.Roslyn.Extension.SymbolAt";
    public const string WrittenPlaces = "Passenger.Roslyn.Extension.WrittenPlaces";
    public const string ComponentClass = "Passenger.Roslyn.Extension.ComponentClass";
    public const string GeneratedTexts = "Passenger.Roslyn.Extension.GeneratedTexts";
    public const string GeneratedNamed = "Passenger.Roslyn.Extension.GeneratedNamed";
    public const string PartialHalf = "Passenger.Roslyn.Extension.PartialHalf";
    public const string GeneratedParts = "Passenger.Roslyn.Extension.GeneratedParts";
    public const string GeneratedDocuments = "Passenger.Roslyn.Extension.GeneratedDocuments";
    public const string JsInteropCalls = "Passenger.Roslyn.Extension.JsInteropCalls";
    public const string GodotCode = "Passenger.Roslyn.Extension.GodotCode";
    public const string GodotNames = "Passenger.Roslyn.Extension.GodotNames";
    public const string GodotPaths = "Passenger.Roslyn.Extension.GodotPaths";
    public const string GodotKindStrings = "Passenger.Roslyn.Extension.GodotKindStrings";
    public const string GodotNameStrings = "Passenger.Roslyn.Extension.GodotNameStrings";
    public const string GodotFix = "Passenger.Roslyn.Extension.GodotFix";
    public const string ToolkitRenames = "Passenger.Roslyn.Extension.ToolkitRenames";

    public static readonly IReadOnlyList<string> All =
    [
        DescribeSignature, ChangeSignature, DescribeInterface, ExtractInterface, DescribeType, MoveTypeToFile, MoveTypeToNamespace,
        DescribeHierarchy, PullMembersUp, PushMembersDown, DocumentTexts, DocumentsWritten, ReferencesRead, ReferencedFiles, AspireProjectDeclaration, AspireCodeHelp, AspireReferenceFix, AspireRunResources,
        AspireRename, AspireParameters, ConnectionReads, WebRoutes, RouteBody, LocalizedReads, SymbolsNamed, SymbolAt, WrittenPlaces,
        ComponentClass, GeneratedTexts, GeneratedNamed, PartialHalf, GeneratedParts, GeneratedDocuments, JsInteropCalls, GodotCode, GodotNames, GodotPaths, GodotKindStrings, GodotNameStrings, GodotFix, ToolkitRenames,
    ];
}

/// <summary>Requests of the server that the extension answers itself, as an LSP method of its own.</summary>
public static class ExtensionMethods
{
    /// <summary>A request about a file in no project, its <c>textDocument</c> and, for part of it, its <c>range</c>: its
    /// colours, as the server's semantic tokens encode them; null for a file a project holds.</summary>
    public const string LooseColours = "passenger/looseSemanticTokens";

    /// <summary>workspace/diagnostic with a <c>frameworks</c> map from a multi-targeted project's file to the framework
    /// solution-wide analysis takes it in, its reports and previous results each naming their project (<c>_vs_projectContext</c>).</summary>
    public const string WorkspaceDiagnostic = "passenger/workspaceDiagnostic";
}

/// <summary>Files the solution has, by their paths, whose texts as the server holds them are asked for: an edit to a file
/// not open is placed by the server's lines and columns, so it lands where meant only on the text the server read.</summary>
public sealed record TextsAsked([property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>Each file asked for, with its text's <see cref="TextHash"/> as the server holds it, or null when the server
/// has no document of that path.</summary>
public sealed record TextsHeld([property: JsonPropertyName("texts")] IReadOnlyList<TextHeld> Texts);

public sealed record TextHeld(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hash")] string? Hash);

/// <summary>The solution's documents written on disk at or after a time, or gone from it since, asked for, and of the files
/// whose change on disk the server is told of (<paramref name="Changed"/>), those that are documents: what the server worked
/// out a moment after may be from those files as they were, until it reads them again or loads their projects again
/// without them.</summary>
public sealed record WrittenAsked(
    [property: JsonPropertyName("since")] DateTime Since,
    [property: JsonPropertyName("changed")] IReadOnlyList<string> Changed);

/// <summary>The documents written since the time asked, and each changed file asked of that is a document, with its text's
/// <see cref="TextHash"/> as the server holds it.</summary>
public sealed record WrittenFound(
    [property: JsonPropertyName("documents")] IReadOnlyList<DocumentWritten> Documents,
    [property: JsonPropertyName("changed")] IReadOnlyList<TextHeld> Changed);

/// <summary>A document of the solution written on disk since the time asked, or gone from it (<paramref name="Gone"/>).</summary>
public sealed record DocumentWritten(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("gone")] bool Gone);

/// <summary>A build ended: the assemblies the solution's projects reference that it wrote are wanted now.</summary>
public sealed record ReferencesAsked;

/// <summary>How many assemblies were read again.</summary>
public sealed record ReferencesTaken([property: JsonPropertyName("read")] int Read);

/// <summary>Files changed on disk, by their paths, of which those the solution's projects reference are asked for.</summary>
public sealed record ReferencedAsked([property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>The paths asked of that a project references, as an assembly or an analyzer.</summary>
public sealed record ReferencedFound([property: JsonPropertyName("paths")] IReadOnlyList<string> Paths);

/// <summary>A text's fingerprint as both sides make it: SHA-256 of its characters with every line break as '\n', since a
/// server's line and column are the same whichever break a line ends in (and Passenger holds a CR file's as '\n').</summary>
public static class TextHash
{
    public static string Of(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))));
}

/// <summary>A name as an agent writes it: <c>Name</c>, the names it is in before it (<c>Type.Member</c>,
/// <c>Namespace.Type</c>), an overload by its parameters' types (<c>Member(string, int)</c>, <c>Member()</c>), or a
/// documentation id (<c>M:Namespace.Type.Member(System.String)</c>). A type's generic parameters may be written or left out.
/// Only symbols declared in the solution's own files count, never only in generated ones.</summary>
public sealed record SymbolsAsked([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("limit")] int Limit);

/// <summary>The symbols found, at most the limit asked, and how many more there were.</summary>
public sealed record SymbolsFound([property: JsonPropertyName("symbols")] IReadOnlyList<NamedSymbol> Symbols, [property: JsonPropertyName("more")] int More);

/// <summary>A symbol as C# shows it (<paramref name="Shown"/>, with its parameters), its kind as LSP names it, its
/// documentation id, the symbol it is in, and its first declaration's place in the solution's own files (null for one
/// declared outside them, a package's or the framework's).</summary>
public sealed record NamedSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shown")] string Shown,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("at")] Place? At);

/// <summary>A place in a document as the server counts it: a line and a UTF-16 column, from zero.</summary>
public sealed record Place([property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("character")] int Character);

/// <summary>A span of a document as the server names it: its URI (a source generator's document's own) and its two ends.</summary>
public sealed record UriSpan(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("start")] Place Start,
    [property: JsonPropertyName("end")] Place End);

/// <summary>Spans whose places in the files their code was written in are asked for, and the answer: each in order, a
/// generated document's in the file its generator wrote it from, any other as it was.</summary>
public sealed record WrittenSpans([property: JsonPropertyName("spans")] IReadOnlyList<UriSpan> Spans);

/// <summary>A place naming a partial member, and which of its halves is asked for: the one with its body, or the one that
/// declares it.</summary>
public sealed record HalfAsked([property: JsonPropertyName("at")] UriSpan At, [property: JsonPropertyName("body")] bool Body);

/// <summary>Where the half asked for is named, or null where the place names no other half.</summary>
public sealed record HalfFound([property: JsonPropertyName("half")] UriSpan? Half);

/// <summary>A place in a document naming a type, whose parts source generators made are asked for.</summary>
public sealed record PartsAsked([property: JsonPropertyName("uri")] string Uri, [property: JsonPropertyName("at")] Place At);

/// <summary>Where source generators' parts of a type declare it; none for anything else.</summary>
public sealed record PartsFound([property: JsonPropertyName("parts")] IReadOnlyList<UriSpan> Parts);

/// <summary>Documents source generators made, whose texts are asked for.</summary>
public sealed record GeneratedAsked([property: JsonPropertyName("documents")] IReadOnlyList<GeneratedSource> Documents);

/// <summary>A source generator's document by its URI, and its project's file and name as an earlier answer gave them (or
/// nulls), by which a server started since, whose projects the URI no longer names, finds it by its generator and hint name.</summary>
public sealed record GeneratedSource(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("projectPath")] string? ProjectPath,
    [property: JsonPropertyName("projectName")] string? ProjectName);

/// <summary>Documents source generators make under a file name, for the projects of a name (a project's file without its
/// extension, or its name where it has no file).</summary>
public sealed record GeneratedNameAsked([property: JsonPropertyName("project")] string Project, [property: JsonPropertyName("file")] string File);

/// <summary>Each document asked for, in order, or null for one no generator makes now.</summary>
public sealed record GeneratedFound([property: JsonPropertyName("texts")] IReadOnlyList<GeneratedText?> Texts);

/// <summary>A source generator's document as the server holds it now: its URI, its text, its project's file and name, and
/// its generator's type.</summary>
public sealed record GeneratedText(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("generator")] string Generator);

/// <summary>Documents by the URIs the server names them by (a source generator's its own), asked whether each is generated
/// code.</summary>
public sealed record GeneratedDocumentsAsked([property: JsonPropertyName("uris")] IReadOnlyList<string> Uris);

/// <summary>Whether each document asked about is generated code, in order: false for a URI of no C# document.</summary>
public sealed record GeneratedDocumentsFound([property: JsonPropertyName("generated")] IReadOnlyList<bool> Generated);

/// <summary>A Razor component's file, whose class is asked for.</summary>
public sealed record ComponentAsked([property: JsonPropertyName("path")] string Path);

/// <summary>The class a Razor component's file makes, and its name's span in the C# Razor generates of the file, where the
/// server takes the class; both null for a file of no project's.</summary>
public sealed record ComponentFound([property: JsonPropertyName("symbol")] NamedSymbol? Symbol, [property: JsonPropertyName("name")] UriSpan? Name);

/// <summary>Where the caret is: what a describing message asks about.</summary>
public sealed record Caret([property: JsonPropertyName("at")] Place At);

/// <summary>Where Go to Declaration goes in Aspire's code: an AddProject call's project file, or the AppHost's file and the
/// place (<paramref name="At"/>) its Add call names a resource a service reads; a null path when neither can be proven.</summary>
public sealed record AspireProjectTarget([property: JsonPropertyName("path")] string? Path, [property: JsonPropertyName("at")] Place? At = null);

/// <summary>Semantic Aspire help; a caret requests resource-name completion as well as diagnostics.</summary>
public sealed record AspireHelpRequest([property: JsonPropertyName("at")] Place? At, bool Explain = false);

/// <summary>The selected missing edge; its identity is checked again against the current semantic graph.</summary>
public sealed record AspireFixRequest(Place At, string Service, string Resource);

/// <summary>A proven absent AppHost reference, with its source range and proposed workspace change.</summary>
public sealed record AspireMissingReference(Place Start, Place End, string Service, string Resource, Refactored Fix);

/// <summary>A connection string a service reads by a name no resource of its AppHost has: where the name is (its characters
/// within the string when it is written plainly, <paramref name="Exact"/>, else the whole expression), the name, the
/// service's project, whose settings may give it, and the names of the AppHost's resources that give connection strings.</summary>
public sealed record AspireUnknownConnection(Place Start, Place End, string Name, bool Exact, string Project, IReadOnlyList<string> Declared);

/// <summary>Names valid at the caret's string argument, references missing from this service's AppHost, and the connection
/// strings it reads that no resource of the AppHost is named.</summary>
public sealed record AspireHelp(Place? Start, Place? End, IReadOnlyList<string> Names, IReadOnlyList<AspireMissingReference> Missing, string? Evidence = null,
    IReadOnlyList<AspireUnknownConnection>? Unknown = null);

/// <summary>Rename of the resource whose name is at a place: described (no new name), or made with the new name.</summary>
public sealed record AspireRenameRequest([property: JsonPropertyName("at")] Place At, [property: JsonPropertyName("newName")] string? NewName = null);

/// <summary>The resource a rename would rename, by its name and the stretch of it at the place asked (none where no resource's
/// name is), and once a new name is given, what renaming it changes or why it does not.</summary>
public sealed record AspireRenaming(string? Name, Place? Start, Place? End, Refactored? Made);

/// <summary>An AppHost file's parameters that take their value from its configuration alone are asked for.</summary>
public sealed record AspireParametersRequest;

/// <summary>A parameter an AppHost declares with no value of its own: its name, whether it is a secret, and where its name is.</summary>
public sealed record AspireParameter(string Name, bool Secret, Place Start, Place End);

/// <summary>The parameters an AppHost's file declares that take their value from its configuration alone.</summary>
public sealed record AspireParametersFound(IReadOnlyList<AspireParameter> Parameters);

/// <summary>The projects whose code's connection-string reads are asked for, by their project files' paths, and with
/// <paramref name="Keys"/> every configuration key their code reads or binds too (<see cref="ConfigurationKey"/>).</summary>
public sealed record ConnectionReadsRequest(
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects,
    [property: JsonPropertyName("keys")] bool Keys = false);

/// <summary>A configuration key a project's code reads or binds, as .NET's configuration names it (sections joined by ':',
/// <c>*</c> for any item of a collection or key of a dictionary): what reads it (<see cref="ConfigurationKinds"/>), the type
/// it is read as (<see cref="ConfigurationTypes"/>, with that type's name in C# and an enum's names), whether the read has
/// a default or its result is checked for null (or, for an options property, whether it has a value of its own), and where
/// it is written: the string naming it in a read or a binding, the property's name in an options class (none for one of a
/// library's), the stretch the name's characters in a plainly written string.</summary>
public sealed record ConfigurationKey(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("typeName")] string TypeName,
    [property: JsonPropertyName("guarded")] bool Guarded,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("at")] Place? At,
    [property: JsonPropertyName("end")] Place? End,
    [property: JsonPropertyName("values")] IReadOnlyList<string>? Values = null,
    [property: JsonPropertyName("owner")] string? Owner = null);

/// <summary>What reads a configuration key: a value read by its key (an indexer, GetValue, GetConnectionString, a section's
/// Value), a section (GetSection, and GetRequiredSection, which throws without one), an options class bound to a section
/// (Configure, Bind, BindConfiguration, Get), or one of that class's properties.</summary>
public static class ConfigurationKinds
{
    public const string Read = "read";
    public const string Section = "section";
    public const string Binding = "binding";
    public const string Option = "option";
}

/// <summary>What a configuration key's value is read as.</summary>
public static class ConfigurationTypes
{
    public const string String = "string";
    public const string Boolean = "boolean";
    public const string Number = "number";
    public const string Enum = "enum";
    public const string Object = "object";
    public const string Array = "array";
    public const string Dictionary = "dictionary";
}

/// <summary>A connection string a project's code reads: the project's file, the name it reads, and the file and the stretch
/// where the name is written (the string or constant naming it).</summary>
public sealed record ConnectionRead(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End);

/// <summary>The connection strings the projects asked for read, each where it is read, and when asked the configuration
/// keys they read or bind; none of a project the server has not loaded.</summary>
public sealed record ConnectionReadsFound(
    [property: JsonPropertyName("reads")] IReadOnlyList<ConnectionRead> Reads,
    [property: JsonPropertyName("keys")] IReadOnlyList<ConfigurationKey>? Keys = null);

/// <summary>A C# or Razor file whose localized strings are asked for, by its path.</summary>
public sealed record LocalizedReadsRequest([property: JsonPropertyName("path")] string Path);

/// <summary>A resource whose strings code reads, as its files are found: the name ResourceManager looks it up by
/// (<c>Shop.Pages.Home</c>), what reads it (<see cref="LocalizedKinds"/>), the type as the code names it, the project whose
/// .resx files it is and that project's root namespace (which MSBuild names them under), and the files' base paths beside the
/// code (<c>Folder/Name</c>, no culture or extension) that name it whatever their folder: a .cs file's of its type's name, by
/// MSBuild's DependentUpon convention, and a generated class's .resx beside its Designer file.</summary>
public sealed record LocalizedResource(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("rootNamespace")] string RootNamespace,
    [property: JsonPropertyName("beside")] IReadOnlyList<string> Beside);

/// <summary>What reads a resource's strings: a localizer of a type (<c>IStringLocalizer&lt;T&gt;</c>, <c>IHtmlLocalizer&lt;T&gt;</c>),
/// which shows the key itself where no file gives it, or a class a .resx generates, whose neutral file gives every key.</summary>
public static class LocalizedKinds
{
    public const string Localizer = "localizer";
    public const string Class = "class";
}

/// <summary>A key a file reads, of the resource at <paramref name="Resource"/> in the answer's list, and where it is written: a
/// string's characters, a generated property's name (<paramref name="Exact"/>), else the whole expression giving it.</summary>
public sealed record LocalizedKey(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("resource")] int Resource,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("exact")] bool Exact);

/// <summary>The keys a file reads, each where it is read, and the resources they are of.</summary>
public sealed record LocalizedReadsFound(
    [property: JsonPropertyName("keys")] IReadOnlyList<LocalizedKey> Keys,
    [property: JsonPropertyName("resources")] IReadOnlyList<LocalizedResource> Resources);

/// <summary>The identifier JS interop imports a module by, whose argument is the module's path.</summary>
public static class JsInteropNames
{
    public const string Import = "import";
}

/// <summary>A C# or Razor file whose calls of JavaScript are asked for, by its path.</summary>
public sealed record JsInteropRequest([property: JsonPropertyName("path")] string Path);

/// <summary>A call of JavaScript a file makes through JS interop: the identifier it calls ("app.greet", or "import" for a
/// module's import), where its string is written, and the module's path ("./Components/Pages/Map.razor.js") that an
/// object reference it is called on was imported from, or that an import gives; null where none is known.</summary>
public sealed record JsInteropCall(
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("module")] string? Module);

/// <summary>The calls of JavaScript a file makes, and the project files of its projects and those near them (referenced, or
/// referencing them), its own first, whose scripts they may call.</summary>
public sealed record JsInteropFound(
    [property: JsonPropertyName("calls")] IReadOnlyList<JsInteropCall> Calls,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>A C# file of a Godot project asked about, by its path, with what of the project's scenes its answer is read
/// against: engine classes by Godot's names, scripts by res:// path, and the signals and methods of nodes the scenes
/// connect, each named on an engine class or a script.</summary>
public sealed record GodotCodeRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("engine")] IReadOnlyList<string> Engine,
    [property: JsonPropertyName("scripts")] IReadOnlyList<string> Scripts,
    [property: JsonPropertyName("signals")] IReadOnlyList<GodotMemberAsked> Signals,
    [property: JsonPropertyName("methods")] IReadOnlyList<GodotMemberAsked> Methods)
{
    /// <summary>The properties the scenes set on nodes of C# scripts, by the names they write, each on its node's script.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyList<GodotMemberAsked> Properties { get; init; } = [];
}

/// <summary>A signal or method of a node, by its name as a scene writes it, on the node's script (res://) where it has one,
/// else on its engine class.</summary>
public sealed record GodotMemberAsked(
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("script")] string? Script,
    [property: JsonPropertyName("name")] string Name);

/// <summary>What a Godot project's C# file holds that its scenes speak of: its classes Godot can run, its calls of
/// GetNode, its res:// paths, the strings Godot's calls take naming signals, methods and properties; and what was asked of
/// the project's types, each list in the order asked.</summary>
public sealed record GodotCodeFound(
    [property: JsonPropertyName("classes")] IReadOnlyList<GodotClassFound> Classes,
    [property: JsonPropertyName("calls")] IReadOnlyList<GodotNodeCall> Calls,
    [property: JsonPropertyName("resources")] IReadOnlyList<GodotPathRead> Resources,
    [property: JsonPropertyName("engine")] IReadOnlyList<GodotChain> Engine,
    [property: JsonPropertyName("scripts")] IReadOnlyList<GodotChain?> Scripts,
    [property: JsonPropertyName("signals")] IReadOnlyList<GodotSignature?> Signals,
    [property: JsonPropertyName("methods")] IReadOnlyList<GodotSignature?> Methods)
{
    public static readonly GodotCodeFound None = new([], [], [], [], [], [], []);

    [JsonPropertyName("strings")]
    public IReadOnlyList<GodotStringRead> Strings { get; init; } = [];

    [JsonPropertyName("kinds")]
    public IReadOnlyList<GodotKindRead> Kinds { get; init; } = [];

    /// <summary>The names of the file's <see cref="Kinds"/> that a call elsewhere in the project's code adds, so they
    /// are defined though no file of Godot's says them.</summary>
    [JsonPropertyName("added")]
    public IReadOnlyList<GodotKindAdded> Added { get; init; } = [];

    /// <summary>Each property asked for (<see cref="GodotCodeRequest.Properties"/>) as its node's script or engine class has
    /// it, in the order asked; null for one neither has.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyList<GodotSignature?> Properties { get; init; } = [];
}

/// <summary>What a string Godot's calls take names that a Godot project defines outside its code: an input action
/// (project.godot's [input]), a group (its scenes' nodes'), an animation (an animation library of its scenes, or an animated
/// sprite's SpriteFrames), an autoload (project.godot's [autoload], a GetNode path's /root/Name), a shader parameter (a
/// shader's uniform), a translation's key (the CSV and PO files project.godot lists), an AnimationTree's parameter (its tree's
/// <c>parameters/…</c> path) or a global shader uniform (project.godot's [shader_globals]); or a layer, by its number
/// (project.godot's [layer_names] names it); or a theme item of each kind a control or window looks up by its class (Godot's
/// default theme, the project's themes).</summary>
public enum GodotStringKind
{
    Action,
    Group,
    Animation,
    Autoload,
    ShaderParameter,
    Translation,
    Layer,
    TreeParameter,
    ShaderGlobal,
    ThemeColor,
    ThemeConstant,
    ThemeFont,
    ThemeFontSize,
    ThemeIcon,
    ThemeStyle,
}

/// <summary>A string of a kind as the file gives it to Godot's call (<c>Input.IsActionPressed("jump")</c>): what it names
/// and where its characters are written (when <paramref name="Exact"/>), whether the call adds it (<c>AddToGroup</c>,
/// <c>InputMap.AddAction</c>), the class it is written in (by its index in the answer's), and the object the call is made
/// on where the code tells it: a node by its path from the node the class runs on, or by the <c>[Export]</c> member a scene
/// sets to it, and for a material the node's property holding it (<c>material</c>); none where it is not told. A layer's
/// name is its number as written in decimal, and its member the layers Godot numbers it among (<c>2d_physics</c>). A theme
/// item's member is the theme type it is looked up in (the receiver's engine class, or the type the call names), and its
/// node "." where the call is made on the class's own node with no type named.</summary>
public sealed record GodotKindRead(
    [property: JsonPropertyName("kind")] GodotStringKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("exact")] bool Exact,
    [property: JsonPropertyName("adds")] bool Adds,
    [property: JsonPropertyName("class")] int Class,
    [property: JsonPropertyName("node")] string? Node,
    [property: JsonPropertyName("export")] string? Export,
    [property: JsonPropertyName("member")] string? Member);

public sealed record GodotKindAdded([property: JsonPropertyName("kind")] GodotStringKind Kind, [property: JsonPropertyName("name")] string Name);

/// <summary>Each string of a kind naming the name asked for: its file, where the name's characters lie, and as
/// <see cref="GodotKindRead"/> tells it, the scripts its class runs as (res://) and the object its call is made on.</summary>
public sealed record GodotKindUses([property: JsonPropertyName("uses")] IReadOnlyList<GodotKindUse> Uses);

public sealed record GodotKindUse(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("scripts")] IReadOnlyList<string> Scripts,
    [property: JsonPropertyName("node")] string? Node,
    [property: JsonPropertyName("export")] string? Export,
    [property: JsonPropertyName("member")] string? Member)
{
    /// <summary>For a string a call passes the member's arguments after (<c>Call("f", 1)</c>, <c>CallDeferred</c>,
    /// <c>Rpc</c>, <c>EmitSignal</c>), those arguments as written; null for any other use.</summary>
    [JsonPropertyName("rest")]
    public GodotCallRest? Rest { get; init; }
}

/// <summary>The arguments a call passes after the string naming a method or signal: which call it is, where the text from
/// the end of the string's argument to the call's closing parenthesis lies, and each argument as written.</summary>
public sealed record GodotCallRest(
    [property: JsonPropertyName("call")] string Call,
    [property: JsonPropertyName("from")] Place From,
    [property: JsonPropertyName("to")] Place To,
    [property: JsonPropertyName("arguments")] IReadOnlyList<string> Arguments);

/// <summary>The strings of a kind naming one name, asked for in the C# of a Godot project: the projects in its folder and those
/// building its game wherever they lie (<paramref name="Projects"/>, by project file).</summary>
public sealed record GodotKindAsked(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("kind")] GodotStringKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>The strings Godot's calls take naming a member of a kind by a name on an engine class's object, asked for in the
/// C# of a Godot project, as <see cref="GodotKindAsked"/> reads it.</summary>
public sealed record GodotNameStringsAsked(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("kind")] GodotNameKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>A string Godot's calls take naming a signal, a method or a property (<c>EmitSignal("Scored")</c>,
/// <c>new Callable(this, "OnScored")</c>, <c>Set("Speed", 2)</c>), as the file gives it: what it names, where it is written
/// (its characters, when <paramref name="Exact"/>), the class of the object it names it on as shown, whether that is a
/// script's class (whose members the code declares, so one it lacks is known missing), the member found, how many
/// arguments an EmitSignal gives the signal or a Call the method (null for another call, or one given as an array), and, for a Callable's method
/// given to a Connect of a signal named by a string, that string's read by its index among these.</summary>
public sealed record GodotStringRead(
    [property: JsonPropertyName("kind")] GodotNameKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("exact")] bool Exact,
    [property: JsonPropertyName("on")] string On,
    [property: JsonPropertyName("script")] bool Script,
    [property: JsonPropertyName("found")] GodotSignature? Found,
    [property: JsonPropertyName("given")] int? Given,
    [property: JsonPropertyName("signal")] int? Signal)
{
    /// <summary>The class it is written in, by its index in the answer's; -1 outside them.</summary>
    [JsonPropertyName("class")]
    public int Class { get; init; } = -1;

    /// <summary>For a member named on an engine class's object, the node the object is as the code tells it: by its path from
    /// the node the class runs on, or by the <c>[Export]</c> member a scene sets to it; null where it does not tell.</summary>
    [JsonPropertyName("node")]
    public string? Node { get; init; }

    [JsonPropertyName("export")]
    public string? Export { get; init; }

    /// <summary>For a signal a Connect is given, the C# method its Callable calls (by a string, MethodName, nameof, or a method
    /// group given to Callable.From); null where it is none of those.</summary>
    [JsonPropertyName("handler")]
    public GodotSignature? Handler { get; init; }

    /// <summary>For a method a Callable names that a Connect is given, the C# signal it is connected to where the Connect
    /// names it otherwise than by a string (SignalName, a constant); null otherwise.</summary>
    [JsonPropertyName("connected")]
    public GodotSignature? Connected { get; init; }

    /// <summary>For a member an engine class's object lacks, the engine classes asked of (<see cref="GodotCodeRequest.Engine"/>)
    /// deriving from its class that have it, by Godot's names.</summary>
    [JsonPropertyName("engines")]
    public IReadOnlyList<string> Engines { get; init; } = [];
}

/// <summary>A class of the file that derives from GodotObject: its name, full name and where its name is written, its first
/// engine class by Godot's name, the scripts its code runs as (res://: its own, and those of the classes deriving from it),
/// and its <c>[Export]</c> members and <c>[Signal]</c> delegates (by the signal's name, the delegate's less EventHandler).</summary>
public sealed record GodotClassFound(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("scripts")] IReadOnlyList<string> Scripts,
    [property: JsonPropertyName("exports")] IReadOnlyList<GodotMember> Exports,
    [property: JsonPropertyName("signals")] IReadOnlyList<GodotMember> Signals);

/// <summary>A member a class declares, by the name a scene writes it by: its type or parameters as shown, and where its name
/// is written, in the file asked of or another.</summary>
public sealed record GodotMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shown")] string Shown,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End)
{
    /// <summary>For an exported member a scene sets to a node by its path, the type that node must be: a NodePath's any
    /// node, a node type's that type; null for any other member.</summary>
    [JsonPropertyName("node")]
    public GodotTypeRef? Node { get; init; }

    /// <summary>For an exported member, what Godot's inspector takes it as: its Variant type (by its number), its hint and
    /// hint string (an enum's names and values, an <c>[Export]</c>'s own), and its default where the code writes it as a
    /// constant, in Godot's variant text; null for any other member.</summary>
    [JsonPropertyName("inspected")]
    public GodotExported? Inspected { get; init; }
}

/// <summary>An exported member as Godot's inspector takes it (<see cref="GodotMember.Inspected"/>).</summary>
public sealed record GodotExported(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("hint")] long Hint,
    [property: JsonPropertyName("hintString")] string HintString,
    [property: JsonPropertyName("default")] string? Default);

/// <summary>A call of GetNode or GetNodeOrNull on the node the code runs on: the path it is given and where its string is
/// written (its characters, when <paramref name="Exact"/>: a string written as it says, no escape in it), the class it is
/// called in (by its index in the answer's), and the type it asks for; none for the call that takes no type.
/// <paramref name="Literal"/> says one not exact is a string literal all the same, plain or verbatim, whole from At to End.</summary>
public sealed record GodotNodeCall(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("exact")] bool Exact,
    [property: JsonPropertyName("class")] int Class,
    [property: JsonPropertyName("type")] GodotTypeRef? Type,
    [property: JsonPropertyName("orNull")] bool OrNull,
    [property: JsonPropertyName("literal")] bool Literal = false);

/// <summary>A type as a node's is matched against it: an engine class by Godot's name, else a class or interface of the
/// code by its full name; with its name as shown.</summary>
public sealed record GodotTypeRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("engine")] bool Engine,
    [property: JsonPropertyName("shown")] string Shown);

/// <summary>A res:// path or uid:// name a string of the file gives, and where its characters are written.</summary>
public sealed record GodotPathRead(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End);

/// <summary>An engine class or a script asked for: the types a node of it is, itself first (Godot's names for an engine
/// class's; full names, its interfaces too, for a script's class), and a script's first engine class by Godot's name.</summary>
public sealed record GodotChain(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("chain")] IReadOnlyList<string> Chain,
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("shown")] string Shown);

/// <summary>A signal or method found: its parameters' types as shown, how many a call must give, and, in the code, where its
/// name is written (its declaring file's path and place); none for an engine class's.</summary>
public sealed record GodotSignature(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parameters")] IReadOnlyList<string> Parameters,
    [property: JsonPropertyName("required")] int Required,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("at")] Place? At,
    [property: JsonPropertyName("end")] Place? End)
{
    /// <summary>The parameters' names, as C# writes them.</summary>
    [JsonPropertyName("names")]
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>For a script's property a scene sets to a node by its path, the type that node must be (as
    /// <see cref="GodotMember.Node"/> says); null for any other.</summary>
    [JsonPropertyName("node")]
    public GodotTypeRef? Node { get; init; }
}

/// <summary>What a fix of a Godot check makes in a script's class (the class whose <c>[ScriptPath]</c> is
/// <paramref name="Script"/>, declared in the file of <paramref name="Path"/>): a handler named <paramref name="Name"/>
/// taking what <see cref="GodotFixRequest.Signal"/> gives, less its last <see cref="GodotFixRequest.Unbinds"/> and with
/// each bound value of <see cref="GodotFixRequest.Binds"/>' types, beside the class's other handlers
/// (<see cref="GodotFixRequest.Beside"/>); an exported property of that name of a Godot type
/// (<see cref="GodotFixRequest.Type"/>: a variant's, an engine class's, or a script's res://); or the type a GetNode call whose
/// path's string is at <see cref="GodotFixRequest.At"/> asks for.</summary>
public sealed record GodotFixRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("script")] string Script,
    [property: JsonPropertyName("kind")] GodotFixKind Kind,
    [property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("signal")]
    public GodotMemberAsked? Signal { get; init; }

    [JsonPropertyName("binds")]
    public IReadOnlyList<string> Binds { get; init; } = [];

    [JsonPropertyName("unbinds")]
    public int Unbinds { get; init; }

    [JsonPropertyName("beside")]
    public IReadOnlyList<string> Beside { get; init; } = [];

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("at")]
    public Place? At { get; init; }
}

/// <summary>What a <see cref="GodotFixRequest"/> makes: a signal's handler, an exported property, or a GetNode call's type.</summary>
public enum GodotFixKind
{
    Handler,
    Export,
    NodeType,
}

/// <summary>The member of a Godot script's code at a caret asked for, with its uses by Godot's other names for it when
/// <paramref name="Uses"/>.</summary>
public sealed record GodotNamesAsked([property: JsonPropertyName("at")] Place At, [property: JsonPropertyName("uses")] bool Uses);

/// <summary>What Godot's names for a script's member name: a signal (a <c>[Signal]</c> delegate's), a method, or a
/// property or field.</summary>
public enum GodotNameKind
{
    Signal,
    Method,
    Property,
}

/// <summary>Which of Godot's names for a member a caret was on: the code's own, the event a signal's delegate makes, a
/// <c>SignalName</c>, <c>MethodName</c> or <c>PropertyName</c> field, a signal's <c>EmitSignalX</c>, or a string Godot's
/// calls take.</summary>
public enum GodotNameVia
{
    Own,
    Event,
    Name,
    Emit,
    String,
}

/// <summary>A Godot script's member at a caret: the code's own symbol, what it is, the name Godot knows it by (a signal's,
/// its delegate's less EventHandler), which of its names the caret was on, and, when asked, every use of it by Godot's
/// other names; none where the caret is on no such member.</summary>
public sealed record GodotNamesFound(
    [property: JsonPropertyName("symbol")] NamedSymbol? Symbol,
    [property: JsonPropertyName("kind")] GodotNameKind Kind,
    [property: JsonPropertyName("written")] string Written,
    [property: JsonPropertyName("via")] GodotNameVia Via,
    [property: JsonPropertyName("uses")] IReadOnlyList<GodotNameUse> Uses)
{
    public static readonly GodotNamesFound None = new(null, GodotNameKind.Signal, string.Empty, GodotNameVia.Own, []);

    /// <summary>What of Godot's engine the caret is on, by Godot's own names; null for anything else.</summary>
    [JsonPropertyName("engine")]
    public GodotEngineMember? Engine { get; init; }
}

/// <summary>A class of Godot's engine, or one of its members, by Godot's own names: the class (<c>Node2D</c>), and the
/// member's kind and name (<c>position</c>); none for the class itself, or a member GodotSharp gives no name of Godot's.</summary>
public sealed record GodotEngineMember(
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("kind")] GodotNameKind? Kind,
    [property: JsonPropertyName("member")] string? Member);

/// <summary>The field or method at a caret, to be renamed <paramref name="Name"/>, whose members the MVVM Toolkit makes are
/// asked for.</summary>
public sealed record ToolkitAsked([property: JsonPropertyName("at")] Place At, [property: JsonPropertyName("name")] string Name);

/// <summary>Each use in the solution's own files of what the MVVM Toolkit makes of the field or method asked of, with the
/// name it takes once that is renamed; and why not, where the new name makes no such member (<paramref name="Problem"/>).</summary>
public sealed record ToolkitFound(
    [property: JsonPropertyName("uses")] IReadOnlyList<ToolkitUse> Uses,
    [property: JsonPropertyName("problem")] string? Problem)
{
    public static readonly ToolkitFound None = new([], null);
}

/// <summary>Where a use of a member the MVVM Toolkit makes is written, and the name it takes.</summary>
public sealed record ToolkitUse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("name")] string Name);

/// <summary>A use of a member by one of Godot's other names for it, in a file of the solution's own: where the name is
/// written, and what it holds before the member's name (<c>EmitSignal</c> for an EmitSignalX).</summary>
public sealed record GodotNameUse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("before")] string Before);

/// <summary>The res:// strings asked for in the C# of a Godot project, as <see cref="GodotKindAsked"/> reads it: those naming one
/// of the paths, a folder's (ending in '/') any path inside it.</summary>
public sealed record GodotPathsAsked(
    [property: JsonPropertyName("folder")] string Folder,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>Each res:// string found, in its file, with what it says and where its characters are written.</summary>
public sealed record GodotPathsFound([property: JsonPropertyName("uses")] IReadOnlyList<GodotPathUse> Uses);

public sealed record GodotPathUse(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End);

/// <summary>The projects whose web routes are asked for, by their project files' paths; none asks for every project's.</summary>
public sealed record RoutesRequest([property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>A value a route takes: its name, where it comes from (route, query, body, header, form or services), its type
/// as written (none for a route parameter no handler parameter takes), its template constraints ("int:min(1)") and
/// whether it may be left out.</summary>
public sealed record RouteParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("constraints")] string? Constraints = null,
    [property: JsonPropertyName("optional")] bool Optional = false);

/// <summary>A route an ASP.NET Core project's code declares, read with no build: what declares it (<see cref="RouteKinds"/>),
/// its HTTP methods (none for any), its whole template from the app's root ("/api/todos/{id:int}"), the query a Razor
/// Page's named handler takes ("handler=Delete"), its parameters, its handler ("TodosController.Get"), where the route is
/// written (the file, the stretch from <paramref name="At"/> to <paramref name="End"/>, and that file's text's
/// <see cref="TextHash"/>, which places it only in the text it was read from), where its handler is, the body parameter's
/// type, and a hub's methods. The JSON body shaped from that type is asked for apart (<see cref="RouteBodyAsked"/>).</summary>
public sealed record WebRoute(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("methods")] IReadOnlyList<string> Methods,
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("query")] string? Query,
    [property: JsonPropertyName("parameters")] IReadOnlyList<RouteParameter> Parameters,
    [property: JsonPropertyName("handler")] string Handler,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("handlerPath")] string? HandlerPath = null,
    [property: JsonPropertyName("handlerAt")] Place? HandlerAt = null,
    [property: JsonPropertyName("bodyType")] string? BodyType = null,
    [property: JsonPropertyName("operations")] IReadOnlyList<string>? Operations = null);

/// <summary>The route whose JSON body is asked for, as its <see cref="WebRoute"/> names it: its project, the file it is
/// written in, where, and its whole template.</summary>
public sealed record RouteBodyAsked(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("template")] string Template);

/// <summary>A route's JSON body shaped from its body parameter's type; null where it takes none or is no longer there.</summary>
public sealed record RouteBodyShaped([property: JsonPropertyName("body")] string? Body);

/// <summary>What declares a route.</summary>
public static class RouteKinds
{
    public const string Minimal = "Minimal API";
    public const string Controller = "Controller";
    public const string RazorPage = "Razor Page";
    public const string Blazor = "Blazor";
    public const string SignalR = "SignalR";
    public const string Grpc = "gRPC";
}

/// <summary>The routes of the projects asked for, and those of them that are web projects, read even when they declare none.</summary>
public sealed record RoutesFound(
    [property: JsonPropertyName("routes")] IReadOnlyList<WebRoute> Routes,
    [property: JsonPropertyName("projects")] IReadOnlyList<string> Projects);

/// <summary>The AppHost whose proven project resources and source locations are read.</summary>
public sealed record AspireResourcesRequest(string AppHost);

/// <summary>A project resource, where it is declared, and the fingerprint of the text that place is in.</summary>
public sealed record AspireRunResource(string Name, string Project, string Source, Place At, string SourceHash);

/// <summary>Proven resources, or why the AppHost cannot be read yet.</summary>
public sealed record AspireRunResourcesResult(IReadOnlyList<AspireRunResource> Resources, string? Problem = null);

/// <summary>One edit to a file's text, between two places of it as it was.</summary>
public sealed record Edit(
    [property: JsonPropertyName("start")] Place Start,
    [property: JsonPropertyName("end")] Place End,
    [property: JsonPropertyName("text")] string Text);

/// <summary>What a refactoring does to one file: its edits, or, for a file it makes, its whole text as the one edit.</summary>
public sealed record FileChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("edits")] IReadOnlyList<Edit> Edits);

/// <summary>A refactoring's answer: the files it changes, or why it changes none; and the uses it leaves as they were,
/// which the change leaves wrong.</summary>
public sealed record Refactored(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("changes")] IReadOnlyList<FileChange> Changes,
    [property: JsonPropertyName("left")] IReadOnlyList<UseLeft>? Left = null);

/// <summary>A use a refactoring could not change: its file, its place in the file as it was, and why, as words that
/// name it ("Circle.Make as a Func&lt;double, Circle&gt;, which the new signature no longer fits").</summary>
public sealed record UseLeft(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("why")] string Why);

/// <summary>A parameter as a signature has it: its modifier (ref, out, in, params, this, or none), type, name and the
/// default value its declaration gives (none when empty).</summary>
public sealed record Parameter(
    [property: JsonPropertyName("modifier")] string Modifier,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("defaultValue")] string DefaultValue);

/// <summary>The method or constructor at the caret, for Change Signature: what it is ("method", "constructor"),
/// its type's name, its modifiers ("public static"), its name, its return type (empty for a constructor), its parameters,
/// and whether it extends a type, whose first parameter stays first. A problem instead when there is none to change.</summary>
public sealed record Signature(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("modifiers")] string Modifiers,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("returnType")] string ReturnType,
    [property: JsonPropertyName("parameters")] IReadOnlyList<Parameter> Parameters,
    [property: JsonPropertyName("isExtension")] bool IsExtension);

/// <summary>A parameter of the signature asked for: the one it was (its place in the old signature), or -1 for a new one
/// with the value every call passes for it.</summary>
public sealed record ParameterChange(
    [property: JsonPropertyName("from")] int From,
    [property: JsonPropertyName("modifier")] string Modifier,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("defaultValue")] string DefaultValue,
    [property: JsonPropertyName("callValue")] string CallValue);

/// <summary>Change Signature's request: the method at the caret given this name, return type and parameters.</summary>
public sealed record SignatureChange(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("returnType")] string ReturnType,
    [property: JsonPropertyName("parameters")] IReadOnlyList<ParameterChange> Parameters);

/// <summary>A member a type could give an interface or a base type: its id (its documentation comment id), how it reads
/// ("Add(int, int): int"), and what it is (method, property, indexer, event, field).</summary>
public sealed record Member(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>The type at the caret, for Extract Interface: its name, the interface's suggested name and file, its
/// namespace, and the members it could give the interface. A problem instead when there is none.</summary>
public sealed record InterfaceSource(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("members")] IReadOnlyList<Member> Members);

/// <summary>Extract Interface's request: an interface of this name in a new file of this name beside the type's, holding
/// the members chosen, which the type then implements.</summary>
public sealed record InterfaceExtraction(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members);

/// <summary>The type at the caret, for Move: its name and kind, its namespace (empty for the global one), the
/// file it is in, the file it would move to, and how many types that file declares. A problem instead when there is none.</summary>
public sealed record TypeSource(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("targetFileName")] string TargetFileName,
    [property: JsonPropertyName("typesInFile")] int TypesInFile);

/// <summary>Move to Another File's request: the type at the caret into a new file of this name beside its own.</summary>
public sealed record TypeToFile(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("fileName")] string FileName);

/// <summary>Move to Namespace's request: the type at the caret into this namespace, its users following.</summary>
public sealed record TypeToNamespace(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("namespace")] string Namespace);

/// <summary>A type's place in its hierarchy, for Pull Members Up and Push Members Down: its name, its members that
/// could move, the base types in the solution they could go up to, and its derived types they could go down to.</summary>
public sealed record Hierarchy(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("members")] IReadOnlyList<Member> Members,
    [property: JsonPropertyName("bases")] IReadOnlyList<Member> Bases,
    [property: JsonPropertyName("derived")] IReadOnlyList<Member> Derived);

/// <summary>Pull Members Up's or Push Members Down's request: these members of the class at the caret moved into the base
/// type named by its id (up), or into each derived class whose id is a line of the target (down); those listed as abstract
/// made abstract where they stay.</summary>
public sealed record MembersMove(
    [property: JsonPropertyName("at")] Place At,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members,
    [property: JsonPropertyName("abstract")] IReadOnlyList<string> Abstract);
