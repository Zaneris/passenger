namespace Passenger.Lsp;

/// <summary>The methods Passenger and its language servers call on each other, by their names on the wire, each written once:
/// the Language Server Protocol's, then Visual Studio's and Roslyn's own that Roslyn's server speaks, then the TypeScript
/// server's. A name misspelt here is misspelt everywhere at once, so a request the other end never answers shows at its
/// one place.</summary>
[IdentifierHome]
public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string Progress = "$/progress";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string DidSave = "textDocument/didSave";
    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string Completion = "textDocument/completion";
    public const string CompletionResolve = "completionItem/resolve";
    public const string Hover = "textDocument/hover";
    public const string SignatureHelp = "textDocument/signatureHelp";
    public const string Definition = "textDocument/definition";
    public const string TypeDefinition = "textDocument/typeDefinition";
    public const string Implementation = "textDocument/implementation";
    public const string References = "textDocument/references";
    public const string DocumentHighlight = "textDocument/documentHighlight";
    public const string DocumentSymbol = "textDocument/documentSymbol";
    public const string CodeAction = "textDocument/codeAction";
    public const string CodeActionResolve = "codeAction/resolve";
    public const string Formatting = "textDocument/formatting";
    public const string RangeFormatting = "textDocument/rangeFormatting";
    public const string OnTypeFormatting = "textDocument/onTypeFormatting";
    public const string Rename = "textDocument/rename";
    public const string PrepareRename = "textDocument/prepareRename";
    public const string FoldingRange = "textDocument/foldingRange";
    public const string DocumentColor = "textDocument/documentColor";
    public const string ColorPresentation = "textDocument/colorPresentation";
    public const string LinkedEditingRange = "textDocument/linkedEditingRange";
    public const string InlayHint = "textDocument/inlayHint";
    public const string InlayHintResolve = "inlayHint/resolve";

    /// <summary>The method a server registers its colours' options under, in client/registerCapability.</summary>
    public const string SemanticTokens = "textDocument/semanticTokens";

    public const string SemanticTokensFull = "textDocument/semanticTokens/full";
    public const string SemanticTokensRange = "textDocument/semanticTokens/range";
    public const string DocumentDiagnostic = "textDocument/diagnostic";
    public const string PrepareCallHierarchy = "textDocument/prepareCallHierarchy";
    public const string IncomingCalls = "callHierarchy/incomingCalls";
    public const string OutgoingCalls = "callHierarchy/outgoingCalls";
    public const string PrepareTypeHierarchy = "textDocument/prepareTypeHierarchy";
    public const string Supertypes = "typeHierarchy/supertypes";
    public const string Subtypes = "typeHierarchy/subtypes";

    public const string WorkspaceSymbol = "workspace/symbol";
    public const string ExecuteCommand = "workspace/executeCommand";
    public const string ApplyEdit = "workspace/applyEdit";
    public const string Configuration = "workspace/configuration";
    public const string DidChangeConfiguration = "workspace/didChangeConfiguration";
    public const string DidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
    public const string WorkspaceDiagnostic = "workspace/diagnostic";
    public const string DiagnosticRefresh = "workspace/diagnostic/refresh";
    public const string SemanticTokensRefresh = "workspace/semanticTokens/refresh";
    public const string InlayHintRefresh = "workspace/inlayHint/refresh";
    public const string CodeLensRefresh = "workspace/codeLens/refresh";

    public const string WorkDoneProgressCreate = "window/workDoneProgress/create";
    public const string ShowMessage = "window/showMessage";
    public const string ShowMessageRequest = "window/showMessageRequest";
    public const string ShowDocument = "window/showDocument";
    public const string LogMessage = "window/logMessage";
    public const string TelemetryEvent = "telemetry/event";
    public const string RegisterCapability = "client/registerCapability";
    public const string UnregisterCapability = "client/unregisterCapability";

    /// <summary>Visual Studio's: what to insert as a character is typed.</summary>
    public const string OnAutoInsert = "textDocument/_vs_onAutoInsert";

    /// <summary>Visual Studio's: the projects a document is compiled in.</summary>
    public const string GetProjectContexts = "textDocument/_vs_getProjectContexts";

    /// <summary>Visual Studio's: a message for a handler of Roslyn's extensions, of the workspace.</summary>
    public const string DispatchExtensionMessage = "workspace/_vs_dispatchExtensionMessage";

    /// <summary>Visual Studio's: a message for a handler of Roslyn's extensions, of a document; misspelt as Roslyn spells it.</summary>
    public const string DispatchDocumentExtensionMessage = "textDocument/_vs_dipatchExtensionMessage";

    /// <summary>Roslyn's: a solution to open.</summary>
    public const string SolutionOpen = "solution/open";

    /// <summary>Roslyn's: projects to open, a folder's with no solution.</summary>
    public const string ProjectOpen = "project/open";

    /// <summary>Roslyn's: the projects it was asked to open are loaded.</summary>
    public const string ProjectInitializationComplete = "workspace/projectInitializationComplete";

    /// <summary>Roslyn's: a message to show, as showMessage's.</summary>
    public const string ShowToast = "window/_roslyn_showToast";

    /// <summary>Razor's, through Roslyn's server: an open Razor file's HTML, its C# blanked out.</summary>
    public const string RazorUpdateHtml = "razor/updateHtml";

    /// <summary>Razor's, through Roslyn's server: a Razor file closed.</summary>
    public const string RazorDocumentClosed = "razor/documentClosed";

    /// <summary>Roslyn's: a fix applied across a document, project or solution.</summary>
    public const string CodeActionResolveFixAll = "codeAction/resolveFixAll";

    /// <summary>The TypeScript server's: the version of TypeScript it runs.</summary>
    public const string TypeScriptVersion = "$/typescriptVersion";
}
