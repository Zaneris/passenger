namespace Passenger.App.Mcp;

/// <summary>Each MCP tool's name, as an agent calls it: what its [McpServerTool] declares and what the tests call, so a
/// misspelt one is a build error.</summary>
[IdentifierHome]
public static class McpTools
{
    public const string AppBindingErrors = "app_binding_errors";
    public const string AppClick = "app_click";
    public const string AppDetails = "app_details";
    public const string AppKey = "app_key";
    public const string AppScreenshot = "app_screenshot";
    public const string AppScroll = "app_scroll";
    public const string AppTree = "app_tree";
    public const string AppType = "app_type";
    public const string AppWindows = "app_windows";
    public const string ApplyCodeAction = "apply_code_action";
    public const string BreakpointSet = "breakpoint_set";
    public const string Build = "build";
    public const string CodeActions = "code_actions";
    public const string ContainerAction = "container_action";
    public const string ContainerExec = "container_exec";
    public const string Containers = "containers";
    public const string DebugAttach = "debug_attach";
    public const string DebugContinue = "debug_continue";
    public const string DebugEvaluate = "debug_evaluate";
    public const string DebugInput = "debug_input";
    public const string DebugPause = "debug_pause";
    public const string DebugSetNextStatement = "debug_set_next_statement";
    public const string DebugStart = "debug_start";
    public const string DebugState = "debug_state";
    public const string DebugStep = "debug_step";
    public const string Definition = "definition";
    public const string Diagnostics = "diagnostics";
    public const string Edit = "edit";
    public const string EditorText = "editor_text";
    public const string FindInFiles = "find_in_files";
    public const string FindSymbol = "find_symbol";
    public const string Format = "format";
    public const string Hierarchy = "hierarchy";
    public const string HotReload = "hot_reload";
    public const string IdeAction = "ide_action";
    public const string IdeActions = "ide_actions";
    public const string LocalHistory = "local_history";
    public const string LocalHistoryLabel = "local_history_label";
    public const string LocalHistoryRevert = "local_history_revert";
    public const string NugetInstall = "nuget_install";
    public const string NugetSearch = "nuget_search";
    public const string Open = "open";
    public const string Outline = "outline";
    public const string Output = "output";
    public const string Profile = "profile";
    public const string Refactor = "refactor";
    public const string Rename = "rename";
    public const string ReplaceInFiles = "replace_in_files";
    public const string Run = "run";
    public const string SaveAll = "save_all";
    public const string Settings = "settings";
    public const string SettingsSet = "settings_set";
    public const string Stop = "stop";
    public const string SymbolInfo = "symbol_info";
    public const string Telemetry = "telemetry";
    public const string TerminalRun = "terminal_run";
    public const string TestsList = "tests_list";
    public const string TestsRun = "tests_run";
    public const string Usages = "usages";
    public const string VcsCheckout = "vcs_checkout";
    public const string VcsCommit = "vcs_commit";
    public const string VcsLog = "vcs_log";
    public const string VcsResolve = "vcs_resolve";
    public const string VcsStash = "vcs_stash";
    public const string VcsStatus = "vcs_status";
    public const string Workspace = "workspace";
}
