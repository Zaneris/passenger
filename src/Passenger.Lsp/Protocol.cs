using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Lsp;

// The Language Server Protocol 3.17, as much of it as Passenger speaks, in its own field names
// (serialized camelCase). Where the protocol allows several shapes for one field, the field is a
// JsonElement and Shapes.cs reads it.

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End)
{
    /// <summary>Whether a place is in the range, either end included.</summary>
    public bool Holds(LspPosition at) =>
        (Start.Line < at.Line || (Start.Line == at.Line && Start.Character <= at.Character))
        && (at.Line < End.Line || (at.Line == End.Line && at.Character <= End.Character));
}

public sealed record LspLocation(string Uri, LspRange Range);

/// <summary>A call or type hierarchy's item: a symbol, where it is and its name's place there; Data is the server's,
/// sent back as it came when its callers, callees, supertypes or subtypes are asked for.</summary>
public sealed class HierarchyItem
{
    public string Name { get; init; } = string.Empty;
    public SymbolKind Kind { get; init; }
    public string? Detail { get; init; }
    public string Uri { get; init; } = string.Empty;
    public LspRange Range { get; init; } = new(new LspPosition(0, 0), new LspPosition(0, 0));
    public LspRange SelectionRange { get; init; } = new(new LspPosition(0, 0), new LspPosition(0, 0));
    public System.Text.Json.JsonElement? Data { get; init; }
}

public sealed record HierarchyItemParams(HierarchyItem Item);

/// <summary>A caller and where in it the calls are.</summary>
public sealed record IncomingCall(HierarchyItem From, IReadOnlyList<LspRange> FromRanges);

/// <summary>A callee and where in the caller the calls to it are.</summary>
public sealed record OutgoingCall(HierarchyItem To, IReadOnlyList<LspRange> FromRanges);

/// <summary>A document, and the project the server is to answer for it in (Visual Studio's <c>_vs_projectContext</c>, a
/// target framework of a multi-targeted project) when not the one it would choose.</summary>
public sealed record TextDocumentIdentifier(string Uri, [property: JsonPropertyName("_vs_projectContext")] ProjectContext? ProjectContext = null);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record TextDocumentContentChangeEvent(LspRange? Range, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

public sealed record LspTextEdit(LspRange Range, string NewText);

public sealed record WorkspaceFolder(string Uri, string Name);

/// <summary>What the client tells a server as it starts; <see cref="InitializationOptions"/> are the server's own (the JSON
/// server's schema protocols, the TypeScript server's preferences), none for Roslyn.</summary>
public sealed record InitializeParams(int ProcessId, string RootUri, IReadOnlyList<WorkspaceFolder> WorkspaceFolders, JsonElement Capabilities, string Locale = "en",
    object? InitializationOptions = null);

/// <summary>One project a document is compiled in, as Visual Studio's protocol extension names it: a target framework
/// of a multi-targeted project each, or Roslyn's Miscellaneous Files for a file in no project it has loaded. A request
/// that carries one is answered as that project sees the document.</summary>
public sealed record ProjectContext(
    [property: JsonPropertyName("_vs_label")] string Label,
    [property: JsonPropertyName("_vs_id")] string Id,
    [property: JsonPropertyName("_vs_kind")] int Kind = 0,
    [property: JsonPropertyName("_vs_is_miscellaneous")] bool IsMiscellaneous = false)
{
    /// <summary>The target framework a context's label ends with in parentheses, as Roslyn names each framework of a
    /// multi-targeted project ("Lib (net10.0)"); null for a project's name alone.</summary>
    public static string? FrameworkOf(string label) =>
        label.EndsWith(')') && label.LastIndexOf(" (", StringComparison.Ordinal) is int open and > 0 ? label[(open + 2)..^1] : null;

    /// <summary>The project a context's label names, its framework left off.</summary>
    public static string ProjectOf(string label) => FrameworkOf(label) is { } framework ? label[..^(framework.Length + 3)] : label;
}

/// <summary>The projects a document is in (<c>textDocument/_vs_getProjectContexts</c>) and the one the server answers
/// for unless a request names another.</summary>
public sealed record ProjectContextList(
    [property: JsonPropertyName("_vs_projectContexts")] IReadOnlyList<ProjectContext> ProjectContexts,
    [property: JsonPropertyName("_vs_defaultIndex")] int DefaultIndex)
{
    /// <summary>The context the server answers for by default; null when it named none.</summary>
    public ProjectContext? Default => DefaultIndex >= 0 && DefaultIndex < ProjectContexts.Count ? ProjectContexts[DefaultIndex] : null;
}

public sealed record GetProjectContextsParams([property: JsonPropertyName("_vs_textDocument")] TextDocumentItem TextDocument);

/// <summary>Roslyn's own: open a solution, or the projects of a folder with none.</summary>
public sealed record OpenSolutionParams(string Solution);

public sealed record OpenProjectParams(IReadOnlyList<string> Projects);

// Completion

public sealed record CompletionContext(CompletionTriggerKind TriggerKind, string? TriggerCharacter = null);

public sealed record CompletionParams(TextDocumentIdentifier TextDocument, LspPosition Position, CompletionContext Context);

public sealed class CompletionList
{
    public bool IsIncomplete { get; init; }
    public List<CompletionItem> Items { get; init; } = [];
    public CompletionItemDefaults? ItemDefaults { get; init; }
}

/// <summary>The commit characters alone: the one item default the client's capabilities say it reads.</summary>
public sealed class CompletionItemDefaults
{
    public List<string>? CommitCharacters { get; init; }
}

public sealed class CompletionItem
{
    public string Label { get; init; } = string.Empty;
    public CompletionItemLabelDetails? LabelDetails { get; init; }
    public CompletionItemKind? Kind { get; init; }
    public string? Detail { get; init; }
    public JsonElement? Documentation { get; init; }
    public string? SortText { get; init; }
    public string? FilterText { get; init; }
    public string? InsertText { get; init; }
    public InsertTextFormat? InsertTextFormat { get; init; }
    public LspTextEdit? TextEdit { get; init; }
    public string? TextEditText { get; init; }
    public List<LspTextEdit>? AdditionalTextEdits { get; init; }
    public List<string>? CommitCharacters { get; init; }
    public bool? Preselect { get; init; }
    public LspCommand? Command { get; init; }
    public JsonElement? Data { get; init; }
}

public sealed record CompletionItemLabelDetails(string? Detail, string? Description);

// Hover, signature help, highlights

public sealed class Hover
{
    public JsonElement Contents { get; init; }
    public LspRange? Range { get; init; }
}

public sealed class SignatureHelp
{
    public List<SignatureInformation> Signatures { get; init; } = [];
    public int? ActiveSignature { get; init; }
    public int? ActiveParameter { get; init; }
}

public sealed class SignatureInformation
{
    public string Label { get; init; } = string.Empty;
    public JsonElement? Documentation { get; init; }
    public List<ParameterInformation>? Parameters { get; init; }
    public int? ActiveParameter { get; init; }
}

public sealed class ParameterInformation
{
    public JsonElement Label { get; init; }
    public JsonElement? Documentation { get; init; }
}

public sealed record DocumentHighlight(LspRange Range, DocumentHighlightKind? Kind);

// References, rename, code actions, formatting

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, LspPosition Position, ReferenceContext Context);

public sealed record RenameParams(TextDocumentIdentifier TextDocument, LspPosition Position, string NewName);

public sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Only = null, CodeActionTriggerKind? TriggerKind = null);

public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, LspRange Range, CodeActionContext Context);

public sealed class CodeAction
{
    public string Title { get; init; } = string.Empty;
    public string? Kind { get; init; }
    public bool? IsPreferred { get; init; }
    public JsonElement? Disabled { get; init; }
    public JsonElement? Edit { get; init; }
    public LspCommand? Command { get; init; }
    public JsonElement? Data { get; init; }
    public List<Diagnostic>? Diagnostics { get; init; }
}

/// <summary>A command a completion item or a code action carries, run once it is chosen, after its edit. Roslyn's are
/// the client's own to run (<see cref="ClientCommands"/>).</summary>
public sealed record LspCommand(string Title, string Command, IReadOnlyList<JsonElement>? Arguments = null);

public sealed record ExecuteCommandParams(string Command, IReadOnlyList<JsonElement> Arguments);

/// <summary>A message of a server's own beyond the protocol, by its method, with what it sent.</summary>
public sealed record ServerMessage(string Method, JsonElement Params);

/// <summary>Roslyn's codeAction/resolveFixAll: a fix-all's data, as its code action carried it, and the scope chosen.</summary>
public sealed record FixAllCodeActionParams(string Title, JsonElement Data, string Scope);

/// <summary>How a whole document is formatted, the same for every server (<see cref="Of"/>).</summary>
public sealed record FormattingOptions(int TabSize, bool InsertSpaces, bool TrimTrailingWhitespace, bool InsertFinalNewline, bool TrimFinalNewlines)
{
    /// <summary>A text's options: its indentation; trailing whitespace trimmed where .editorconfig's
    /// <c>trim_trailing_whitespace</c> says so; and its end kept as it is, a final line break or none, unless
    /// <c>insert_final_newline</c> asks for one, or for none (every final line break trimmed).</summary>
    public static FormattingOptions Of(IndentOptions indent, EditorConfig conventions, TextSnapshot text)
    {
        bool broken = text.Length > 0 && text[text.Length - 1] is '\n' or '\r';
        return new(indent.Size, !indent.UseTabs, conventions.TrimTrailingWhitespace, conventions.FinalNewline ?? broken, conventions.FinalNewline == false);
    }
}

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, LspRange Range, FormattingOptions Options);

// Symbols

public sealed record WorkspaceSymbolParams(string Query);

public sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);

public sealed record FoldingRangeParams(TextDocumentIdentifier TextDocument);

/// <summary>A symbol found by name across the workspace.</summary>
public sealed record SymbolInformation(string Name, SymbolKind Kind, LspLocation Location, string? ContainerName = null)
{
    /// <summary>The type it is in, from Roslyn's container ("in Type (project P (tfm))") without the project, which its place
    /// says; null for a type's own ("project P (tfm)").</summary>
    [JsonIgnore]
    public string? Owner
    {
        get
        {
            if (ContainerName is not { } container)
            {
                return null;
            }
            int project = container.LastIndexOf(" (project ", StringComparison.Ordinal);
            string within = project >= 0 ? container[..project] : container.StartsWith("project ", StringComparison.Ordinal) ? string.Empty : container;
            within = within.StartsWith("in ", StringComparison.Ordinal) ? within[3..] : within;
            return within.Length == 0 ? null : within;
        }
    }
}

/// <summary>A symbol in a document's outline, its members as children. Range is the whole
/// declaration; SelectionRange is its name.</summary>
public sealed record DocumentSymbol(string Name, string? Detail, SymbolKind Kind, LspRange Range, LspRange SelectionRange, IReadOnlyList<DocumentSymbol>? Children = null)
{
    /// <summary>An outline's symbols from the top to the deepest holding a place, outermost first: of siblings that both
    /// hold it (one ends where the next begins), the later.</summary>
    public static List<DocumentSymbol> PathTo(IReadOnlyList<DocumentSymbol> outline, LspPosition at)
    {
        List<DocumentSymbol> path = [];
        IReadOnlyList<DocumentSymbol>? level = outline;
        while (level?.LastOrDefault(s => s.Range.Holds(at)) is { } inside)
        {
            path.Add(inside);
            level = inside.Children;
        }
        return path;
    }
}

/// <summary>Each of LSP's symbol kinds by its name as an agent reads it.</summary>
public static class SymbolKinds
{
    private static readonly string[] Names =
    [
        "file", "module", "namespace", "package", "class", "method", "property", "field", "constructor", "enum", "interface",
        "function", "variable", "constant", "string", "number", "boolean", "array", "object", "key", "null", "enum member",
        "struct", "event", "operator", "type parameter",
    ];

    public static string Name(SymbolKind kind) => kind >= SymbolKind.File && (int)kind <= Names.Length ? Names[(int)kind - 1] : "symbol";
}

// Semantic tokens

public sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);

public sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, LspRange Range);

public sealed class SemanticTokens
{
    public string? ResultId { get; init; }

    [JsonConverter(typeof(TokenData))]
    public int[] Data { get; init; } = [];
}

/// <summary>A file's tokens, five numbers each, read into one array of their count: a long file's are a million numbers,
/// which the serializer's own reading grows a list to hold, doubling it, before copying it out.</summary>
public sealed class TokenData : JsonConverter<int[]>
{
    public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Semantic tokens are an array of numbers, not {reader.TokenType}");
        }
        // A converter is given its whole value, so a copy of the reader can count it first.
        Utf8JsonReader counting = reader;
        int count = 0;
        while (counting.Read() && counting.TokenType == JsonTokenType.Number)
        {
            count++;
        }
        int[] data = new int[count];
        for (int i = 0; i < count; i++)
        {
            reader.Read();
            data[i] = reader.GetInt32();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Semantic tokens hold something other than numbers");
        }
        return data;
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (int number in value)
        {
            writer.WriteNumberValue(number);
        }
        writer.WriteEndArray();
    }
}

public sealed record SemanticTokensLegend(IReadOnlyList<string> TokenTypes, IReadOnlyList<string> TokenModifiers);

// Diagnostics

/// <summary>A server's diagnostic, read from its UTF-8 by <see cref="DiagnosticConverter"/>.</summary>
[JsonConverter(typeof(DiagnosticConverter))]
public sealed class Diagnostic
{
    public LspRange Range { get; init; } = new(new LspPosition(0, 0), new LspPosition(0, 0));
    public DiagnosticSeverity? Severity { get; init; }

    /// <summary>The server's code for it, a number's as written (TypeScript's 2322).</summary>
    public string? Code { get; init; }

    /// <summary>Whether the server wrote <see cref="Code"/> as a number, as it is written back to it.</summary>
    public bool CodeIsNumber { get; init; }

    /// <summary>Where the code is explained: an ESLint rule's page.</summary>
    public CodeDescription? CodeDescription { get; init; }

    public string? Source { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<DiagnosticTag>? Tags { get; init; }

    /// <summary>What the server keeps with it, raw, as it is given back to the server with a request for its fixes.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>The page <see cref="CodeDescription"/> names, when it is a web address.</summary>
    [JsonIgnore]
    public Uri? CodePage => CodeDescription?.Href is { } href && Uri.TryCreate(href, UriKind.Absolute, out Uri? page) ? page : null;
}

/// <summary>A diagnostic code's page (<c>{ "href": … }</c>).</summary>
public sealed record CodeDescription(string Href);

/// <summary>A diagnostic read field by field from its UTF-8 and written back the same way: a code a string or a number, the
/// strings a server repeats across a report (codes, sources) made once per thread, nothing kept but what the
/// diagnostic holds. A workspace's report holds thousands of them.</summary>
public sealed class DiagnosticConverter : JsonConverter<Diagnostic>
{
    /// <summary>The codes and sources this thread read last, one made string each, so a report's thousand CS0168s share one.</summary>
    [ThreadStatic]
    private static string?[]? recent;

    [ThreadStatic]
    private static int next;

    public override Diagnostic Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"A diagnostic is an object, not {reader.TokenType}");
        }
        LspRange? range = null;
        DiagnosticSeverity? severity = null;
        string? code = null;
        bool number = false;
        CodeDescription? description = null;
        string? source = null;
        string? message = null;
        List<DiagnosticTag>? tags = null;
        JsonElement? data = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            Field field = FieldOf(ref reader);
            reader.Read();
            switch (field)
            {
                case Field.Range:
                    range = JsonSerializer.Deserialize<LspRange>(ref reader, options);
                    break;
                case Field.Severity:
                    severity = reader.TokenType == JsonTokenType.Null ? null : (DiagnosticSeverity)reader.GetInt32();
                    break;
                case Field.Code:
                    (code, number) = Code(ref reader);
                    break;
                case Field.CodeDescription:
                    description = Description(ref reader);
                    break;
                case Field.Source:
                    source = Repeated(ref reader);
                    break;
                case Field.Message:
                    message = reader.GetString();
                    break;
                case Field.Tags:
                    tags = Tags(ref reader);
                    break;
                case Field.Data:
                    data = reader.TokenType == JsonTokenType.Null ? null : JsonElement.ParseValue(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return new Diagnostic
        {
            Range = range ?? new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
            Severity = severity,
            Code = code,
            CodeIsNumber = number,
            CodeDescription = description,
            Source = source,
            Message = message ?? string.Empty,
            Tags = tags,
            Data = data,
        };
    }

    public override void Write(Utf8JsonWriter writer, Diagnostic value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("range"u8);
        JsonSerializer.Serialize(writer, value.Range, options);
        if (value.Severity is { } severity)
        {
            writer.WriteNumber("severity"u8, (int)severity);
        }
        if (value.Code is { } code)
        {
            if (value.CodeIsNumber)
            {
                writer.WritePropertyName("code"u8);
                writer.WriteRawValue(code, skipInputValidation: false);
            }
            else
            {
                writer.WriteString("code"u8, code);
            }
        }
        if (value.CodeDescription is { } description)
        {
            writer.WriteStartObject("codeDescription"u8);
            writer.WriteString("href"u8, description.Href);
            writer.WriteEndObject();
        }
        if (value.Source is { } source)
        {
            writer.WriteString("source"u8, source);
        }
        writer.WriteString("message"u8, value.Message);
        if (value.Tags is { } tags)
        {
            writer.WriteStartArray("tags"u8);
            foreach (DiagnosticTag tag in tags)
            {
                writer.WriteNumberValue((int)tag);
            }
            writer.WriteEndArray();
        }
        if (value.Data is { } data)
        {
            writer.WritePropertyName("data"u8);
            data.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private enum Field
    {
        Other,
        Range,
        Severity,
        Code,
        CodeDescription,
        Source,
        Message,
        Tags,
        Data,
    }

    /// <summary>The field a property name names, as the serializer matches names, ignoring case.</summary>
    private static Field FieldOf(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("range"u8))
        {
            return Field.Range;
        }
        if (reader.ValueTextEquals("severity"u8))
        {
            return Field.Severity;
        }
        if (reader.ValueTextEquals("code"u8))
        {
            return Field.Code;
        }
        if (reader.ValueTextEquals("codeDescription"u8))
        {
            return Field.CodeDescription;
        }
        if (reader.ValueTextEquals("source"u8))
        {
            return Field.Source;
        }
        if (reader.ValueTextEquals("message"u8))
        {
            return Field.Message;
        }
        if (reader.ValueTextEquals("tags"u8))
        {
            return Field.Tags;
        }
        if (reader.ValueTextEquals("data"u8))
        {
            return Field.Data;
        }
        // Escaped, its length is past any of the names' unescaped.
        if ((reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) > 96)
        {
            return Field.Other;
        }
        Span<char> buffer = stackalloc char[96];
        ReadOnlySpan<char> name = buffer[..reader.CopyString(buffer)];
        return name.Equals("range", StringComparison.OrdinalIgnoreCase) ? Field.Range
            : name.Equals("severity", StringComparison.OrdinalIgnoreCase) ? Field.Severity
            : name.Equals("code", StringComparison.OrdinalIgnoreCase) ? Field.Code
            : name.Equals("codeDescription", StringComparison.OrdinalIgnoreCase) ? Field.CodeDescription
            : name.Equals("source", StringComparison.OrdinalIgnoreCase) ? Field.Source
            : name.Equals("message", StringComparison.OrdinalIgnoreCase) ? Field.Message
            : name.Equals("tags", StringComparison.OrdinalIgnoreCase) ? Field.Tags
            : name.Equals("data", StringComparison.OrdinalIgnoreCase) ? Field.Data
            : Field.Other;
    }

    /// <summary>A code as text, whether it is a number (its digits as written); true for a number.</summary>
    private static (string? Code, bool Number) Code(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Null => (null, false),
        JsonTokenType.String => (Repeated(ref reader), false),
        JsonTokenType.Number => (Repeated(ref reader), true),
        _ => (JsonElement.ParseValue(ref reader).ToString(), false),
    };

    /// <summary>A string or number token's text, the same string as this thread made for it lately.</summary>
    private static string? Repeated(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        string?[] kept = recent ??= new string?[8];
        foreach (string? one in kept)
        {
            if (one is not null && (reader.TokenType == JsonTokenType.String ? reader.ValueTextEquals(one) : Digits(Written(ref reader), one)))
            {
                return one;
            }
        }
        string made = reader.TokenType == JsonTokenType.String ? reader.GetString()! : System.Text.Encoding.UTF8.GetString(Written(ref reader));
        kept[next] = made;
        next = (next + 1) % kept.Length;
        return made;
    }

    /// <summary>A number's bytes as written, copied only where they cross the reader's segments.</summary>
    private static ReadOnlySpan<byte> Written(ref Utf8JsonReader reader) => reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan;

    /// <summary>Whether a number's bytes, as written, are a string's characters.</summary>
    private static bool Digits(ReadOnlySpan<byte> written, string text)
    {
        if (written.Length != text.Length)
        {
            return false;
        }
        for (int i = 0; i < written.Length; i++)
        {
            if (written[i] != text[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A code's page, from its href when that is a string.</summary>
    private static CodeDescription? Description(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        string? href = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool named = reader.ValueTextEquals("href"u8);
            reader.Read();
            if (named && reader.TokenType == JsonTokenType.String)
            {
                href = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }
        return href is null ? null : new CodeDescription(href);
    }

    private static List<DiagnosticTag>? Tags(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"A diagnostic's tags are an array, not {reader.TokenType}");
        }
        List<DiagnosticTag> tags = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            tags.Add((DiagnosticTag)reader.GetInt32());
        }
        return tags;
    }
}

public sealed record DocumentDiagnosticParams(TextDocumentIdentifier TextDocument, string? Identifier = null, string? PreviousResultId = null);

/// <summary>A workspace diagnostics request for one source, with the result each document last had, so documents that
/// have not changed come back as unchanged; its reports streamed as <c>$/progress</c> of the token as they are found.</summary>
public sealed record WorkspaceDiagnosticParams(string? Identifier, IReadOnlyList<PreviousResultId> PreviousResultIds, string? PartialResultToken = null);

public sealed record PreviousResultId(string Uri, string Value);

public sealed class WorkspaceDiagnosticReport
{
    public List<WorkspaceDocumentDiagnosticReport> Items { get; init; } = [];
}

/// <summary>One document's diagnostics in a workspace report: all of them ("full"), or "unchanged" since its result id.</summary>
public sealed class WorkspaceDocumentDiagnosticReport
{
    public string Kind { get; init; } = "full";
    public string? ResultId { get; init; }
    public List<Diagnostic>? Items { get; init; }
    public string Uri { get; init; } = string.Empty;
    public int? Version { get; init; }
}

public sealed class DocumentDiagnosticReport
{
    public string Kind { get; init; } = "full";
    public string? ResultId { get; init; }
    public List<Diagnostic>? Items { get; init; }
}

public sealed record PublishDiagnosticsParams(string Uri, int? Version, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A colour a server finds in a document (a CSS colour), its components from 0 to 1.</summary>
public sealed record ColorInformation(LspRange Range, LspColor Color);

public sealed record LspColor(double Red, double Green, double Blue, double Alpha);

public sealed record DocumentColorParams(TextDocumentIdentifier TextDocument);

// Messages from the server

/// <summary>A message the server logs or shows, and how grave it is.</summary>
public sealed record LogMessageParams(MessageType Type, string Message);

/// <summary>Roslyn's <c>window/_roslyn_showToast</c>: a message to show, as showMessage's.</summary>
public sealed record ShowToastParams(MessageType MessageType, string Message);

/// <summary>A <c>$/progress</c> notification: the token the server created, and its value, which for work done is a
/// begin, a report or an end.</summary>
public sealed record ProgressParams(JsonElement Token, JsonElement Value);

/// <summary>Work the server reports on: the token it goes by, whether this is its end, its
/// title (from its begin), the latest message and how far it is, in percent, when the server says.</summary>
public sealed record WorkProgress(string Token, bool Ended, string? Title, string? Message, int? Percentage)
{
    /// <summary>Work done progress from a <c>$/progress</c> notification; null for any other kind (partial results).</summary>
    public static WorkProgress? From(ProgressParams progress)
    {
        if (JsonMembers.Text(progress.Value, "kind") is not { } kind || kind is not ("begin" or "report" or "end")
            || progress.Token.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return null;
        }
        return new WorkProgress(
            progress.Token.ToString(),
            kind == "end",
            JsonMembers.Text(progress.Value, "title"),
            JsonMembers.Text(progress.Value, "message"),
            JsonMembers.Number(progress.Value, "percentage"));
    }
}

public sealed record ConfigurationItem(string? ScopeUri, string? Section);

public sealed record ConfigurationParams(IReadOnlyList<ConfigurationItem> Items);

public sealed record InlayHintParams(TextDocumentIdentifier TextDocument, LspRange Range);

/// <summary>A word the server would show inside a line, before the position it names: a parameter's name before its
/// argument, a type; padding asks for a space on that side. <see cref="Data"/> is what the server
/// knows it by, for its tooltip (<c>inlayHint/resolve</c>).</summary>
public sealed record LspInlayHint(LspPosition Position, string Label, InlayHintKind? Kind, bool PaddingLeft, bool PaddingRight, JsonElement? Data = null);

public sealed record Registration(string Id, string Method, JsonElement? RegisterOptions);

public sealed record RegistrationParams(IReadOnlyList<Registration> Registrations);

/// <summary>The serializer settings every message goes through: camelCase names, nulls left out.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// The protocol's numbered kinds, each the protocol's number on the wire (the serializer writes an enum as its number).

/// <summary>How grave a diagnostic is.</summary>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>What a symbol is: a document's outline's, a workspace symbol's, a hierarchy's item's.</summary>
public enum SymbolKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26,
}

/// <summary>What a completion item is, for its icon.</summary>
public enum CompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

/// <summary>What asked for completion.</summary>
public enum CompletionTriggerKind
{
    Invoked = 1,
    TriggerCharacter = 2,
    TriggerForIncompleteCompletions = 3,
}

/// <summary>How a completion item's text is inserted: as it is, or as a snippet with tab stops.</summary>
public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

/// <summary>What an inlay hint names: a type, or the parameter an argument is for.</summary>
public enum InlayHintKind
{
    Type = 1,
    Parameter = 2,
}

/// <summary>How a highlighted usage of the symbol at the caret uses it.</summary>
public enum DocumentHighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

/// <summary>What else a diagnostic says of its code: that it is unused (drawn faded), or deprecated.</summary>
public enum DiagnosticTag
{
    Unnecessary = 1,
    Deprecated = 2,
}

/// <summary>How grave a message the server logs or shows is.</summary>
public enum MessageType
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Log = 4,
    Debug = 5,
}

/// <summary>Who asked for code actions: the person, or the client on its own (a light bulb's check).</summary>
public enum CodeActionTriggerKind
{
    Invoked = 1,
    Automatic = 2,
}
