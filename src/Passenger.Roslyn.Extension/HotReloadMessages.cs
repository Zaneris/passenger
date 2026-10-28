using System.Text.Json.Serialization;

namespace Passenger.Roslyn.Extension;

// Hot Reload's messages between Passenger and its extension in Roslyn's server, compiled into both.

/// <summary>The hot reload handlers' names, as the server knows them: their types' full names.</summary>
public static class HotReloadHandlers
{
    public const string Start = "Passenger.Roslyn.Extension.StartHotReload";
    public const string Emit = "Passenger.Roslyn.Extension.EmitHotReload";
    public const string Commit = "Passenger.Roslyn.Extension.CommitHotReload";
    public const string Discard = "Passenger.Roslyn.Extension.DiscardHotReload";
    public const string End = "Passenger.Roslyn.Extension.EndHotReload";

    public static readonly IReadOnlyList<string> All = [Start, Emit, Commit, Discard, End];
}

/// <summary>A debugging session's start, at its first edit: the edits the debugged runtime can take, as it names them
/// ("Baseline", "AddMethodToExistingType", ...), and the files edited so far as they were before (as built), which Roslyn
/// takes once their checksums match the build's PDB; every other file is as built.</summary>
public sealed record HotReloadStart(
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("originals")] IReadOnlyList<HotReloadOriginal> Originals);

/// <summary>A file's text before the session's first edit to it.</summary>
public sealed record HotReloadOriginal(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text);

/// <summary>The session started, by its number, or why none did.</summary>
public sealed record HotReloadStarted(
    [property: JsonPropertyName("session")] int Session,
    [property: JsonPropertyName("problem")] string? Problem);

/// <summary>A statement a thread is in, as the debugger finds it: its method (module, token, version), the instruction,
/// and its sequence point's place in its document (lines and columns from zero), with Roslyn's ActiveStatementFlags.</summary>
public sealed record HotReloadActiveStatement(
    [property: JsonPropertyName("module")] Guid Module,
    [property: JsonPropertyName("token")] int Token,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("ilOffset")] int ILOffset,
    [property: JsonPropertyName("document")] string? Document,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("startColumn")] int StartColumn,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("endColumn")] int EndColumn,
    [property: JsonPropertyName("flags")] int Flags);

/// <summary>What the edits since the session's start (or its last update taken) come to: the program paused, or running and
/// held stopped for the update, or running; the edits its runtime can take (as it says them now), the modules it has
/// loaded whose code can be edited (by their MVIDs) and, paused or held, the statements its threads are in.</summary>
public sealed record HotReloadEmit(
    [property: JsonPropertyName("session")] int Session,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("held")] bool Held,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("modules")] IReadOnlyList<Guid> Modules,
    [property: JsonPropertyName("activeStatements")] IReadOnlyList<HotReloadActiveStatement> ActiveStatements,
    [property: JsonPropertyName("locked")] IReadOnlyList<HotReloadLockedModule> Locked,
    [property: JsonPropertyName("originals")] IReadOnlyList<HotReloadOriginal> Originals);

/// <summary>A module the program has loaded whose code its runtime will not let be edited, and why (optimized code).</summary>
public sealed record HotReloadLockedModule(
    [property: JsonPropertyName("module")] Guid Module,
    [property: JsonPropertyName("why")] string Why);

/// <summary>A line of a document that moved with no change to its code (from zero): lines from it on move by the difference,
/// up to the next update's.</summary>
public sealed record HotReloadLineUpdate(
    [property: JsonPropertyName("oldLine")] int OldLine,
    [property: JsonPropertyName("newLine")] int NewLine);

/// <summary>A document's moved lines, by its name as the PDB has it.</summary>
public sealed record HotReloadLines(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("updates")] IReadOnlyList<HotReloadLineUpdate> Updates);

/// <summary>Where an active statement is in the new version of its method's source (lines and columns from zero).</summary>
public sealed record HotReloadStatementUpdate(
    [property: JsonPropertyName("token")] int Token,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("ilOffset")] int ILOffset,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("startColumn")] int StartColumn,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("endColumn")] int EndColumn);

/// <summary>A document an update edits and the first of its lines the edit changed (from zero), as it read before: the
/// lines above it read as they did.</summary>
public sealed record HotReloadFirstChange(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("line")] int Line);

/// <summary>One module's update: the metadata, IL and PDB deltas, the methods and types it changes (their tokens), the
/// lines that moved, where the active statements went, the runtime capabilities it needs, and the first line it changed
/// in each document it edits.</summary>
public sealed record HotReloadModuleUpdate(
    [property: JsonPropertyName("module")] Guid Module,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("metadata")] byte[] Metadata,
    [property: JsonPropertyName("il")] byte[] IL,
    [property: JsonPropertyName("pdb")] byte[] Pdb,
    [property: JsonPropertyName("updatedMethods")] IReadOnlyList<int> UpdatedMethods,
    [property: JsonPropertyName("updatedTypes")] IReadOnlyList<int> UpdatedTypes,
    [property: JsonPropertyName("lines")] IReadOnlyList<HotReloadLines> Lines,
    [property: JsonPropertyName("activeStatements")] IReadOnlyList<HotReloadStatementUpdate> ActiveStatements,
    [property: JsonPropertyName("requiredCapabilities")] IReadOnlyList<string> RequiredCapabilities,
    [property: JsonPropertyName("firstChanges")] IReadOnlyList<HotReloadFirstChange> FirstChanges);

/// <summary>An edit the update could not take, or a warning about one: Roslyn's id (ENC0020 for a rude edit, CS1002 for a
/// compile error) and words, whether it stops the update, whether restarting the program would take it, and its place
/// (lines and columns from zero; no path for one of the whole project).</summary>
public sealed record HotReloadProblem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("error")] bool Error,
    [property: JsonPropertyName("restartRequired")] bool RestartRequired,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("startColumn")] int StartColumn,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("endColumn")] int EndColumn,
    [property: JsonPropertyName("project")] string? Project);

/// <summary>What the edits come to: "none" (nothing to apply), "ready" (updates to apply, then commit or discard) or
/// "blocked" (problems stop it; nothing is pending), the updates, the problems, the documents whose edits make them, and
/// why there is no answer when there is none.</summary>
public sealed record HotReloadEmitted(
    [property: JsonPropertyName("problem")] string? Problem,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("updates")] IReadOnlyList<HotReloadModuleUpdate> Updates,
    [property: JsonPropertyName("problems")] IReadOnlyList<HotReloadProblem> Problems,
    [property: JsonPropertyName("changedDocuments")] IReadOnlyList<string> ChangedDocuments);

/// <summary>A message about a session alone: commit, discard or end.</summary>
public sealed record HotReloadSessionMessage([property: JsonPropertyName("session")] int Session);

/// <summary>Done, or why not.</summary>
public sealed record HotReloadDone([property: JsonPropertyName("problem")] string? Problem);
