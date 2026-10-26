using System.Text.Json;
using Passenger.Roslyn.Extension;

namespace Passenger.Lsp;

/// <summary>Passenger's own extension to Roslyn's server (Passenger.Roslyn.Extension, carried under roslyn-extension/
/// beside the app): the server loads it as it starts (its <c>--extension</c>), Passenger activates its message handlers
/// once the server has answered (<c>server/_vs_activateExtension</c>), and each question is a message to one of them about
/// a document (<c>textDocument/_vs_dipatchExtensionMessage</c>, the typo Roslyn's) or the solution
/// (<c>workspace/_vs_dispatchExtensionMessage</c>), answered with what it found or would change.</summary>
public static class RoslynExtension
{
    /// <summary>Where the app carries the extension.</summary>
    public static string Assembly => Path.Combine(AppContext.BaseDirectory, "roslyn-extension", "Passenger.Roslyn.Extension.dll");

    /// <summary>Activates the extension's handlers in a server that loaded it; null once every one Passenger sends to is
    /// there, else why not.</summary>
    internal static async Task<string?> ActivateAsync(LanguageServer server, CancellationToken cancel)
    {
        if (!File.Exists(Assembly))
        {
            return "Passenger's extension to the C# language server is missing from the app's folder";
        }
        JsonElement answer = await server.RequestAsync<JsonElement>("server/_vs_activateExtension", new ActivateExtensionParams(Assembly), cancel).ConfigureAwait(false);
        if (Thrown(answer) is { } thrown)
        {
            return $"the C# language server could not load Passenger's extension: {thrown}";
        }
        HashSet<string> active = [.. Named(answer, "documentMessageHandlers"), .. Named(answer, "workspaceMessageHandlers")];
        string[] missing = [.. Handlers.All.Concat(HotReloadHandlers.All).Where(h => !active.Contains(h))];
        return missing.Length == 0 ? null : $"the C# language server did not take Passenger's {string.Join(", ", missing.Select(m => m[(m.LastIndexOf('.') + 1)..]))}";

        static IEnumerable<string> Named(JsonElement answer, string kind) =>
            answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(kind, out JsonElement names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.GetString() ?? string.Empty)
                : [];
    }

    /// <summary>A message to one of the extension's workspace handlers, about the solution the server has open, and its
    /// answer; a <see cref="LanguageServerFailed"/> when the server has no extension to ask, refuses, or the handler throws.</summary>
    internal static async Task<T> AskWorkspaceAsync<T>(LanguageServer server, string handler, object message, CancellationToken cancel)
    {
        if (server.ExtensionProblem is { } problem)
        {
            throw new LanguageServerFailed(problem, handler);
        }
        string text = JsonSerializer.Serialize(message, message.GetType());
        JsonElement answer = await server.RequestAsync<JsonElement>(LspMethods.DispatchExtensionMessage,
            new DispatchWorkspaceMessageParams(handler, text), cancel).ConfigureAwait(false);
        return Answer<T>(answer, handler);
    }

    /// <summary>A message to one of the extension's handlers about a document, and its answer; a
    /// <see cref="LanguageServerFailed"/> when the server has no extension to ask, refuses, or the handler throws.</summary>
    internal static async Task<T> AskAsync<T>(LanguageServer server, string handler, object message, TextDocumentIdentifier document, CancellationToken cancel)
    {
        if (server.ExtensionProblem is { } problem)
        {
            throw new LanguageServerFailed(problem, handler);
        }
        // The server reads and writes the message with System.Text.Json's own defaults, so it goes as the types name it.
        string text = JsonSerializer.Serialize(message, message.GetType());
        JsonElement answer;
        try
        {
            answer = await server.RequestAsync<JsonElement>(LspMethods.DispatchDocumentExtensionMessage,
                new DispatchExtensionMessageParams(handler, text, document), cancel).ConfigureAwait(false);
        }
        catch (LanguageServerFailed failed) when (failed.Reason.Contains("No handler found", StringComparison.Ordinal))
        {
            // The handlers are the solution's: a file in no project the server loaded is in a workspace of its own.
            throw new LanguageServerFailed("the file is in no project the C# language server has loaded", handler, failed);
        }
        return Answer<T>(answer, handler);
    }

    /// <summary>A message to one of the extension's workspace handlers that nothing waits on (one sent as a session or a
    /// window ends): sent through <see cref="LanguageServer.Tell"/>, dropped when the server is gone.</summary>
    internal static void TellWorkspace(LanguageServer server, string handler, object message)
    {
        if (server.ExtensionProblem is null)
        {
            server.Tell(LspMethods.DispatchExtensionMessage, new DispatchWorkspaceMessageParams(handler, JsonSerializer.Serialize(message, message.GetType())));
        }
    }

    /// <summary>A handler's answer read from the server's reply, or the failure it carries.</summary>
    private static T Answer<T>(JsonElement answer, string handler)
    {
        if (Thrown(answer) is { } thrown)
        {
            throw new LanguageServerFailed($"Passenger's extension to the C# language server failed: {thrown}", handler);
        }
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("response", out JsonElement response) || response.GetString() is not { Length: > 0 } json)
        {
            throw new LanguageServerFailed("the C# language server gave no answer from Passenger's extension", handler);
        }
        return JsonSerializer.Deserialize<T>(json) ?? throw new LanguageServerFailed("Passenger's extension answered with nothing", handler);
    }

    /// <summary>A refactoring's changes as the protocol's workspace edit: each new file created, then each file's edits, in
    /// the text the server had when it answered (no version, so an open file's are carried through what was typed since).</summary>
    public static JsonElement WorkspaceEdit(Refactored refactored)
    {
        List<object> changes = [];
        foreach (FileChange file in refactored.Changes)
        {
            string uri = DocumentUris.FromPath(file.Path);
            if (file.Created)
            {
                changes.Add(new { kind = "create", uri });
            }
            changes.Add(new
            {
                textDocument = new { uri },
                edits = file.Edits.Select(e => new LspTextEdit(new LspRange(new LspPosition(e.Start.Line, e.Start.Character), new LspPosition(e.End.Line, e.End.Character)), e.Text)),
            });
        }
        return JsonSerializer.SerializeToElement(new { documentChanges = changes }, Json.Options);
    }

    /// <summary>The message of an exception an answer carries, whichever way the server wrote it.</summary>
    private static string? Thrown(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("extensionException", out JsonElement thrown) || thrown.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        foreach (string name in new[] { "message", "Message" })
        {
            if (thrown.ValueKind == JsonValueKind.Object && thrown.TryGetProperty(name, out JsonElement message) && message.GetString() is { Length: > 0 } said)
            {
                return said;
            }
        }
        return thrown.ToString();
    }

    private sealed record ActivateExtensionParams(string AssemblyFilePath);

    private sealed record DispatchExtensionMessageParams(string MessageName, string Message, TextDocumentIdentifier TextDocument);

    /// <summary>A message to one of the extension's workspace handlers, by the handler's name, as the server is sent it.</summary>
    public sealed record DispatchWorkspaceMessageParams(string MessageName, string Message);
}
