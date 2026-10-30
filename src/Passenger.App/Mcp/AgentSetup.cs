using System.Text.Encodings.Web;
using System.Text.Json;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Terminal;

namespace Passenger.App.Mcp;

/// <summary>How an agent is given this copy of Passenger as its MCP server, as Settings' MCP Server page shows it: each
/// agent's command or configuration (Claude Code, Codex, Claude Desktop, OpenCode 1 and 2), naming this executable by its
/// real path (inside the app bundle on macOS) with --mcp-attach, and Claude Desktop's with the folder to work in after
/// it, since it starts its servers in no folder. The commands run in a terminal are written for the shell they are run
/// in (<see cref="Shell"/>).</summary>
public sealed record AgentSetup(string Executable, string? Folder, ShellKind Shell)
{
    /// <summary>What Claude Desktop's configuration names when no folder is open: the person's to fill in.</summary>
    public const string FolderPlaceholder = "FOLDER";

    private static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The running copy's setup, with the window's folder, its commands written for the Terminal's shell
    /// (<see cref="ShellOf"/>).</summary>
    public static AgentSetup Here(string? folder, string? terminalShell) => new(ThisExecutable(), folder, ShellOf(terminalShell));

    /// <summary>The kind of the Terminal's shell: the program the command line Settings give it (<paramref
    /// name="terminalShell"/>) starts, else the system's.</summary>
    public static ShellKind ShellOf(string? terminalShell) =>
        ShellWords.KindOf(CommandLine.Split(terminalShell) is [{ Length: > 0 } program, ..] ? program : TerminalSession.DefaultShell().App);

    /// <summary>Where Settings' notes say to run the commands: the shell they are written for, by its name, or a terminal
    /// for sh and the shells that quote as it does.</summary>
    public string RunIn => Shell switch
    {
        ShellKind.PowerShell => "PowerShell",
        ShellKind.Cmd => "cmd",
        ShellKind.Fish => "fish",
        ShellKind.Nushell => "Nushell",
        _ => "a terminal",
    };

    /// <summary>The executable an agent starts: this process's, or the app host beside it when it runs through dotnet.</summary>
    public static string ThisExecutable()
    {
        string? path = Environment.ProcessPath;
        if (path is null || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");
        }
        return PathNames.Canonical(path);
    }

    /// <summary>Claude Code's command, run in a terminal's <see cref="Shell"/>.</summary>
    public string ClaudeCode => $"claude mcp add passenger -- {ShellWords.Quoted(Executable, Shell)} --mcp-attach";

    /// <summary>A repository's .mcp.json, which Claude Code reads for everyone working there.</summary>
    public string McpJson => Json(["--mcp-attach"]);

    /// <summary>Codex's command, run in a terminal's <see cref="Shell"/>.</summary>
    public string Codex => $"codex mcp add passenger -- {ShellWords.Quoted(Executable, Shell)} --mcp-attach";

    /// <summary>Codex's ~/.codex/config.toml entry, which the Codex app shares.</summary>
    public string CodexToml => $"[mcp_servers.passenger]\ncommand = {Quoted(Executable)}\nargs = [\"--mcp-attach\"]";

    /// <summary>Claude Desktop's claude_desktop_config.json entry, with the folder to work in.</summary>
    public string ClaudeDesktop => Json(["--mcp-attach", Folder ?? FolderPlaceholder]);

    /// <summary>OpenCode 1's timeout for a call it hears nothing of: past the person's two questions (2 minutes each at
    /// most), and progress every 10 s starts it over, so a long build never reaches it.</summary>
    public const int OpenCodeTimeout = 600_000;

    /// <summary>How long OpenCode 2 lets a call take in all, which progress does not start over: past a long build or test
    /// run, yet a call that never came back would fail in half an hour rather than its 12 hours.</summary>
    public const int OpenCode2Timeout = 1_800_000;

    /// <summary>OpenCode 1's opencode.json entry: a local server, its command one list, its one timeout every request's.</summary>
    public string OpenCode =>
        "{\n  \"mcp\": {\n    \"passenger\": {\n" +
        "      \"type\": \"local\",\n" +
        $"      \"command\": [{Quoted(Executable)}, \"--mcp-attach\"],\n" +
        "      \"enabled\": true,\n" +
        $"      \"timeout\": {OpenCodeTimeout}\n" +
        "    }\n  }\n}";

    /// <summary>OpenCode 2's: under mcp.servers, Code Mode off, so the agent calls Passenger's tools itself (Code Mode's
    /// permission prompts can leave a call waiting), and a tool call's execution timeout.</summary>
    public string OpenCode2 =>
        "{\n  \"mcp\": {\n    \"servers\": {\n      \"passenger\": {\n" +
        "        \"type\": \"local\",\n" +
        $"        \"command\": [{Quoted(Executable)}, \"--mcp-attach\"],\n" +
        "        \"codemode\": false,\n" +
        $"        \"timeout\": {{ \"execution\": {OpenCode2Timeout} }}\n" +
        "      }\n    }\n  }\n}";

    private string Json(IReadOnlyList<string> arguments) =>
        "{\n  \"mcpServers\": {\n    \"passenger\": {\n" +
        $"      \"command\": {Quoted(Executable)},\n" +
        $"      \"args\": [{string.Join(", ", arguments.Select(Quoted))}]\n" +
        "    }\n  }\n}";

    /// <summary>A string as JSON and TOML both write one: in double quotes, a backslash or quote escaped.</summary>
    private static string Quoted(string text) => JsonSerializer.Serialize(text, Strings);
}
