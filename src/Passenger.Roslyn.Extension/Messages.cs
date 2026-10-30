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
    public const string AspireProjectDeclaration = "Passenger.Roslyn.Extension.AspireProjectDeclaration";
    public const string AspireCodeHelp = "Passenger.Roslyn.Extension.AspireCodeHelp";
    public const string AspireReferenceFix = "Passenger.Roslyn.Extension.AspireReferenceFix";
    public const string AspireRunResources = "Passenger.Roslyn.Extension.AspireRunResources";
    public const string AspireRename = "Passenger.Roslyn.Extension.AspireRename";
    public const string AspireParameters = "Passenger.Roslyn.Extension.AspireParameters";
    public const string ConnectionReads = "Passenger.Roslyn.Extension.ConnectionReads";
    public const string WebRoutes = "Passenger.Roslyn.Extension.WebRoutes";
    public const string LocalizedReads = "Passenger.Roslyn.Extension.LocalizedReads";
    public const string SymbolsNamed = "Passenger.Roslyn.Extension.SymbolsNamed";
    public const string SymbolAt = "Passenger.Roslyn.Extension.SymbolAt";
    public const string WrittenPlaces = "Passenger.Roslyn.Extension.WrittenPlaces";
    public const string ComponentClass = "Passenger.Roslyn.Extension.ComponentClass";
    public const string GeneratedTexts = "Passenger.Roslyn.Extension.GeneratedTexts";

    public static readonly IReadOnlyList<string> All =
    [
        DescribeSignature, ChangeSignature, DescribeInterface, ExtractInterface, DescribeType, MoveTypeToFile, MoveTypeToNamespace,
        DescribeHierarchy, PullMembersUp, PushMembersDown, DocumentTexts, DocumentsWritten, AspireProjectDeclaration, AspireCodeHelp, AspireReferenceFix, AspireRunResources,
        AspireRename, AspireParameters, ConnectionReads, WebRoutes, LocalizedReads, SymbolsNamed, SymbolAt, WrittenPlaces,
        ComponentClass, GeneratedTexts,
    ];
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

/// <summary>The solution's documents written on disk at or after a time, or gone from it since, asked for: what the server
/// worked out a moment after may be from those files as they were, until it reads them again or loads their projects
/// again without them.</summary>
public sealed record WrittenAsked([property: JsonPropertyName("since")] DateTime Since);

public sealed record WrittenFound([property: JsonPropertyName("documents")] IReadOnlyList<DocumentWritten> Documents);

/// <summary>A document of the solution written on disk since the time asked, or gone from it (<paramref name="Gone"/>).</summary>
public sealed record DocumentWritten(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("gone")] bool Gone);

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

/// <summary>Documents source generators made, whose texts are asked for.</summary>
public sealed record GeneratedAsked([property: JsonPropertyName("documents")] IReadOnlyList<GeneratedSource> Documents);

/// <summary>A source generator's document by its URI, and its project's file and name as an earlier answer gave them (or
/// nulls), by which a server started since, whose projects the URI no longer names, finds it by its generator and hint name.</summary>
public sealed record GeneratedSource(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("projectPath")] string? ProjectPath,
    [property: JsonPropertyName("projectName")] string? ProjectName);

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
/// type, a JSON body shaped from that type, and a hub's methods.</summary>
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
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("operations")] IReadOnlyList<string>? Operations = null);

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
