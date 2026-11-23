using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.CSharp;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>ESLint, among the folder's tools: VS Code's ESLint language server (carried under web/ in
/// vscode-langservers-extracted), one for the folder, running the project's own eslint package (found from each file's
/// folder up, or the one Settings names) with its own configuration, for the files "Run for files" covers.</summary>
public sealed partial class FolderTools
{
    /// <summary>The server's script in the package carried under web/.</summary>
    public static string EslintScript => Path.Combine(WebLanguages.Modules, "vscode-langservers-extracted", "lib", "eslint-language-server", "eslintServer.js");

    /// <summary>The ESLint package that checks a file as Settings configure it, null for a file it does not check (Disabled,
    /// outside "Run for files", no package found, or in a folder not trusted, whose configuration is code).</summary>
    public string? EslintPackageFor(string path)
    {
        AppSettings now = settings();
        if (now.Eslint == ToolConfiguration.Disabled || !ProjectPackages.Covers(now.EslintFiles, folder, path) || !WorkspaceTrust.Trusts(path))
        {
            return null;
        }
        return now.Eslint == ToolConfiguration.Manual ? ProjectPackages.Named(now.EslintPackage, folder) : ProjectPackages.Installed(path, "eslint");
    }

    /// <summary>The package.json that lists eslint for a file it is not installed for, under Automatic configuration.</summary>
    public string? EslintMissing(string path)
    {
        AppSettings now = settings();
        return now.Eslint == ToolConfiguration.Automatic && ProjectPackages.Covers(now.EslintFiles, folder, path) && WorkspaceTrust.Trusts(path)
            ? ProjectPackages.Missing(path, "eslint") : null;
    }

    /// <summary>"Run eslint --fix on save": a save fixes first what ESLint can fix in each file it checks.</summary>
    public bool FixesOnSave => settings().EslintFixOnSave;

    /// <summary>ESLint's server, while it runs.</summary>
    private NodeServer? EslintServer => servers.GetValueOrDefault(new Place(WebTool.Eslint, folder))?.Server;

    /// <summary>Whether a file configures ESLint: a configuration (flat or eslintrc), the ignore file, or package.json
    /// (its eslintConfig).</summary>
    private static bool EslintConfigures(string name) =>
        name is ".eslintignore" or "package.json" or ".eslintrc"
        || (name.StartsWith("eslint.config.", StringComparison.Ordinal) && Path.GetExtension(name) is ".js" or ".mjs" or ".cjs" or ".ts" or ".mts" or ".cts")
        || (name.StartsWith(".eslintrc.", StringComparison.Ordinal) && Path.GetExtension(name) is ".js" or ".cjs" or ".yaml" or ".yml" or ".json");

    /// <summary>What the server reads as each file's settings, asked for whole: lint as typed, the package found from
    /// the file's folder up unless Manual names one (by the node_modules it is in), the project's flat or eslintrc
    /// configuration as its eslint decides, and fixes and suppressions offered.</summary>
    private object? Section(string? section)
    {
        if (section is { Length: > 0 })
        {
            return null;
        }
        AppSettings now = settings();
        string? nodePath = now.Eslint == ToolConfiguration.Manual && ProjectPackages.Named(now.EslintPackage, folder) is { } package ? Path.GetDirectoryName(package) : null;
        JsonObject answer = new()
        {
            ["validate"] = "on",
            ["packageManager"] = "npm",
            ["useESLintClass"] = false,
            // Neither flat nor eslintrc forced: the project's eslint takes the configuration it finds.
            ["experimental"] = new JsonObject(),
            ["codeAction"] = new JsonObject
            {
                ["disableRuleComment"] = new JsonObject { ["enable"] = true, ["location"] = "separateLine", ["commentStyle"] = "line" },
                ["showDocumentation"] = new JsonObject { ["enable"] = true },
            },
            ["codeActionOnSave"] = new JsonObject { ["mode"] = "all" },
            ["format"] = false,
            ["quiet"] = false,
            ["onIgnoredFiles"] = "off",
            ["options"] = new JsonObject(),
            ["rulesCustomizations"] = new JsonArray(),
            ["run"] = "onType",
            ["problems"] = new JsonObject { ["shortenToSingleLine"] = false },
            ["nodePath"] = nodePath,
            ["workspaceFolder"] = new JsonObject { ["name"] = Path.GetFileName(folder), ["uri"] = DocumentUris.FromPath(folder) },
        };
        // As JSON whole, so nodePath goes as null rather than left out (the server takes a missing one for a path).
        return JsonDocument.Parse(answer.ToJsonString()).RootElement.Clone();
    }

    /// <summary>Listens to a server once it has started: its errors to the IDE's log, its own messages to the status bar
    /// once a kind and file.</summary>
    private void Hear(LanguageSession started)
    {
        LanguageServer server = started.Server;
        // Touched only by the server's messages, which come one at a time in order.
        string? introduced = null;
        bool configMissing = false;
        server.Logged += (_, message) =>
        {
            if (message.Type is not (MessageType.Error or MessageType.Warning))
            {
                return;
            }
            if (message.Message == UnexpectedError)
            {
                introduced = message.Message;
                return;
            }
            string said = introduced is null ? message.Message : $"{introduced} {message.Message}";
            introduced = null;
            if (IsConfigMissing(message.Message))
            {
                configMissing = true;
                return;
            }
            IdeErrors.Write($"ESLint language server (process {server.ProcessId}) {(message.Type == MessageType.Error ? "error" : "warning")}: {said}");
        };
        server.Said += (_, message) =>
        {
            bool unconfigured = false;
            if (message.Method == "eslint/status")
            {
                (unconfigured, configMissing) = (configMissing, false);
            }
            UiThread.Post(() => Heard(started, message, unconfigured));
        };
    }

    /// <summary>The first line vscode-eslint's catch-all writes before the error's stack, as a message of its own.</summary>
    private const string UnexpectedError = "An unexpected error occurred:";

    /// <summary>Whether what the server logged is ESLint's flat configuration not found (its config-file-missing, which
    /// the server takes for a failure): a project with eslint installed and no configuration, which is an answer for
    /// the status bar, not an error for the log.</summary>
    private static bool IsConfigMissing(string logged)
    {
        string first = ChildResult.FirstLineOf(logged);
        foreach (string lead in (string[])["An unexpected error occurred: ", "Error: "])
        {
            first = first.StartsWith(lead, StringComparison.Ordinal) ? first[lead.Length..] : first;
        }
        return first.StartsWith("Could not find config file", StringComparison.Ordinal);
    }

    /// <summary>One of the server's own messages: a rule's page to open, or a file it cannot check, said once; a file's
    /// status after its configuration was not found (<paramref name="unconfigured"/>) is said as eslint/noConfig is.</summary>
    private void Heard(LanguageSession from, ServerMessage message, bool unconfigured)
    {
        if (EslintServer?.Session != from)
        {
            return;
        }
        JsonElement body = message.Params;
        string? Text(string name) => JsonMembers.Text(body, name);
        string? Document() => JsonMembers.Text(JsonMembers.Object(body, "document"), "uri") is { } uri ? Path.GetFileName(DocumentUris.ToPath(uri)) : null;
        switch (message.Method)
        {
            case "eslint/openDoc" when Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out Uri? page):
                Browse?.Invoke(this, page);
                break;
            case "eslint/noConfig" when said.Add(message.Method + Document()):
                Message?.Invoke(this, $"ESLint: no configuration found for {Document() ?? "the file"}, so it is not checked");
                break;
            case "eslint/status" when unconfigured && Text("uri") is { } uri && Path.GetFileName(DocumentUris.ToPath(uri)) is { } name && said.Add("eslint/noConfig" + name):
                Message?.Invoke(this, $"ESLint: no configuration found for {name}, so it is not checked");
                break;
            case "eslint/noLibrary" when said.Add(message.Method + Document()):
                Message?.Invoke(this, $"ESLint: the eslint package could not be loaded for {Document() ?? "the file"}");
                break;
            case "eslint/exitCalled":
                IdeErrors.Write($"ESLint language server (process {from.Server.ProcessId}): a plugin called process.exit: {body}");
                break;
        }
    }
}
