using System.Diagnostics;
using System.Text.Json;

namespace Passenger.Lsp;

/// <summary>How a language server other than Roslyn starts (a web language's, on Node.js; GDScript's, in Godot's editor):
/// the program as <c>ChildProcesses</c> made it (none for a server another program already runs, on <see cref="Port"/>),
/// the folder it works on, where its log goes, the capabilities Passenger tells it, its own initialization options, what
/// <c>workspace/configuration</c> answers for each section (null for its default), and the content it asks the client for
/// by URI (<c>vscode/content</c>: the JSON server's schemas).</summary>
public sealed record ServerStart(ProcessStartInfo? Process, string Folder, string LogFolder, JsonElement Capabilities, object? InitializationOptions = null)
{
    /// <summary>The loopback port the server is reached on over TCP, rather than its standard input and output: the program
    /// started to serve there, once it listens, its own output going to the server's log; or, with no program, the server
    /// already listening there.</summary>
    public int? Port { get; init; }

    /// <summary>How long a program started to serve on <see cref="Port"/> may take to listen (Godot's editor imports its
    /// project first).</summary>
    public TimeSpan Listening { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>What holds <see cref="Port"/> so the program started to serve there cannot listen on it, said as a clause,
    /// else null: asked now and then while it runs and has yet to listen.</summary>
    public Func<int, string?>? Taken { get; init; }

    /// <summary>Whether the server is told the folder's path as well as its URI (LSP's <c>rootPath</c>), which Godot's reads
    /// to know it is the project open in it.</summary>
    public bool TellsRootPath { get; init; }

    public Func<string?, object?>? Configuration { get; init; }

    public Func<string, CancellationToken, Task<string>>? Content { get; init; }

    /// <summary>How the diagnostics it pushes naming no version follow the texts it is sent.</summary>
    public PushPace Pushes { get; init; } = PushPace.Latest;
}

/// <summary>How a server that pushes a document's diagnostics without naming their version answers the texts it is sent,
/// so each push is told the version it answers as it arrives (<see cref="LspDocument.Pushed"/>): each text that changed, in
/// order (<see cref="EachText"/>, the Dockerfile server's), or the latest once it has rested <see cref="Rest"/> (a debounce,
/// YAML's and TypeScript's), one before answering no text known, but a clean one where the server then pushes nothing for a
/// clean text (<see cref="QuietWhenClean"/>, TypeScript's); else (<see cref="Latest"/>) the latest sent.</summary>
public sealed record PushPace(bool EachText, TimeSpan Rest, bool QuietWhenClean = false)
{
    public static readonly PushPace Latest = new(false, TimeSpan.Zero);
}
