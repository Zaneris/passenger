using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Mcp;

namespace Passenger.App.Settings;

/// <summary>The MCP Server page: Enable MCP Server, the folders agents join unasked (each Always for this folder the
/// window's question kept, each taken back by Remove), and how to set up each agent with this copy of Passenger, its
/// own path in every command and configuration, each with a Copy button.</summary>
public sealed partial class SettingsDialog
{
    /// <summary>The agents' setups the page shows, by their headings, in its order.</summary>
    public IReadOnlyList<(string Heading, string Text)> AgentSetups { get; private set; } = [];

    /// <summary>The folders agents join unasked, as the page's list shows them.</summary>
    public ListBox AgentFolderList { get; } = new() { Classes = { "tree" }, Height = 96 };

    /// <summary>Takes the folder chosen in <see cref="AgentFolderList"/> off it.</summary>
    public Button RemoveAgentFolderButton { get; } = new() { Content = "Remove", IsEnabled = false };

    /// <summary>Each setup's Copy button, by its heading.</summary>
    public IReadOnlyDictionary<string, Button> CopyButtons => copyButtons;

    private readonly Dictionary<string, Button> copyButtons = new(StringComparer.Ordinal);

    private void AddAgents(AgentSetup setup)
    {
        Add(Declared.McpServer);

        TextBlock none = new() { Classes = { "emptyText" }, Text = "None: a window asks before it lets an agent in" };
        void List()
        {
            AgentFolderList.ItemsSource = Working.AgentFolders.ToArray();
            none.IsVisible = Working.AgentFolders.Count == 0;
            RemoveAgentFolderButton.IsEnabled = AgentFolderList.SelectedItem is not null;
        }
        AgentFolderList.SelectionChanged += (_, _) => RemoveAgentFolderButton.IsEnabled = AgentFolderList.SelectedItem is not null;
        RemoveAgentFolderButton.Click += (_, _) =>
        {
            if (AgentFolderList.SelectedItem is string folder)
            {
                Change(s => s with { AgentFolders = [.. s.AgentFolders.Where(f => f != folder)] });
                List();
            }
        };
        List();
        StackPanel folders = new()
        {
            Spacing = 6,
            Width = 420,
            Children =
            {
                new Border { Classes = { "framed" }, Child = new Panel { Children = { AgentFolderList, none } } },
                RemoveAgentFolderButton,
            },
        };
        Add(McpPage, "Folders agents join unasked", folders, $"{McpWords} always for this folder allowed trusted remove take back let in",
            "Always for this folder, in the question a window asks an agent, puts its folder here: agents then join that folder's "
            + "window without the question. Remove takes it back once you choose OK or Apply, and an agent on its window is asked "
            + "again at its next call.");

        Control heading = Column(new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new TextBlock { Text = "Set up an agent", FontWeight = FontWeight.SemiBold },
                MutedText.Wrapped("Each runs this copy of Passenger, by its own path, with --mcp-attach. An agent has Passenger's tools from its "
                    + "start; its first call joins the window open on the folder it works in, or opens that folder in Passenger, "
                    + "starting it if need be, in a window you see. That window asks you before it lets the agent in and before "
                    + "the agent first makes each kind of change, saving, stopping and stepping aside, and a call waits 2 minutes at most for each answer, telling the agent so."),
                MutedText.Wrapped("For an agent working on its own, put --mcp and the folder in place of --mcp-attach: a Passenger of the "
                    + "agent's own, with no window, which asks nothing."),
            },
        });
        const string SetupWords = $"{McpWords} setup set up install configure command config json toml claude code desktop codex opencode copy path";
        options.Add(new Option(McpPage, "Set up an agent", heading, $"{McpPage} {SetupWords}"));
        List<(string, string)> setups = [];
        void Setup(string title, string text, string note, string words)
        {
            setups.Add((title, text));
            AddBlock(SetupBlock(title, text, note), title, $"{SetupWords} {words}");
        }
        Setup("Claude Code", setup.ClaudeCode,
            "Run it in a terminal in your project's folder (with --scope user after add, for every project).", "terminal cli");
        Setup("A repository's .mcp.json", setup.McpJson,
            "For everyone who works in the repository with Claude Code, in .mcp.json at its root.", "repository project team shared");
        Setup("Codex", setup.Codex, "Run it in a terminal. The Codex app shares the CLI's settings.", "terminal cli openai");
        Setup("Codex's config.toml", setup.CodexToml, "Or in ~/.codex/config.toml.", "toml file openai");
        Setup("Claude Desktop", setup.ClaudeDesktop,
            setup.Folder is null
                ? $"In Claude's Settings, Developer, Edit Config, then restart Claude. It starts its servers in no folder of yours, so put the folder to work in where {AgentSetup.FolderPlaceholder} is."
                : "In Claude's Settings, Developer, Edit Config, then restart Claude. It starts its servers in no folder of yours, so the folder to work in follows --mcp-attach: this window's.",
            "desktop app config claude_desktop_config.json folder");
        Setup("OpenCode", setup.OpenCode,
            "OpenCode 1, in opencode.json at your project's root, or ~/.config/opencode/opencode.json for every project. The "
            + "timeout gives a call 10 minutes, and Passenger tells OpenCode its progress meanwhile.",
            "opencode.json sst anomaly");
        Setup("OpenCode 2", setup.OpenCode2,
            "OpenCode 2 lists servers under mcp.servers, in opencode.jsonc or opencode.json. With Code Mode off it calls "
            + "Passenger's tools itself, as Code Mode's permission prompts can leave a call waiting, and a call that never "
            + "came back would end after 30 minutes rather than 12 hours: OpenCode 2 counts a call's whole time, its progress or not, so a long build or test run needs the room.",
            "opencode.jsonc codemode code mode execution timeout");
        AgentSetups = setups;

        void AddBlock(Control row, string title, string words) => options.Add(new Option(McpPage, title, row, $"{McpPage} {title} {words}"));
    }

    /// <summary>A setup's text to the clipboard, its Copy then saying Copied and every other setup's Copy again.</summary>
    private async Task CopySetupAsync(Button copy, string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
        foreach (Button other in copyButtons.Values)
        {
            other.Content = other == copy ? "Copied" : "Copy";
        }
    }

    /// <summary>One agent's setup: its heading with Copy at the right, what to do with it, and the text to copy as code.</summary>
    private Control SetupBlock(string title, string text, string note)
    {
        Button copy = new() { Content = "Copy" };
        copyButtons[title] = copy;
        copy.Click += (_, _) => IdeErrors.Observe(CopySetupAsync(copy, text));
        DockPanel head = new();
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return Column(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { head, MutedText.Wrapped(note), CodeBlock.Of(text) },
        });
    }

    /// <summary>The setup's one column: every block as wide as the others, Copy buttons in a line, narrowing with the dialog.</summary>
    private static Grid Column(Control block)
    {
        Grid column = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Star) { MaxWidth = 640 } } };
        column.Children.Add(block);
        return column;
    }

}
