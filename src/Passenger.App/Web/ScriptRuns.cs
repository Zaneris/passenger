using System.Diagnostics;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>How a Node package's script starts: by its own package manager (<see cref="NodePackage.Manager"/>), never npm
/// for a package pnpm, Yarn or Bun runs, found beside the Node.js in use (where its installer, nvm and corepack put it)
/// before the PATH, with that Node.js's folder first on the PATH the script is given, so what it runs as <c>node</c> is the
/// one checked. npm runs as its own script on that Node.js, so a Windows npm.cmd needs no shell.</summary>
public static class ScriptRuns
{
    /// <summary>The start of a package's script with these arguments after it in its folder, or why it cannot start: no
    /// Node.js (Bun runs scripts without one), or no package manager it runs with.</summary>
    public static (ProcessStartInfo? Start, string? Problem) Start(NodePackage package, string script, IReadOnlyList<string> arguments, NodeRuntime node)
    {
        if (node.Problem is { } noNode && package.Manager != NodeManager.Bun)
        {
            return (null, $"{package.Name}'s scripts run on Node.js, and {noNode}");
        }
        IReadOnlyList<string> words = NodeProjects.RunArguments(package.Manager, script, arguments);
        string? folder = node.Path is { } path && node.Problem is null ? Path.GetDirectoryName(path) : null;
        ProcessStartInfo? start = package.Manager == NodeManager.Npm && node.Path is { } runtime && folder is not null && Npm.Cli(runtime) is { } cli
            ? ChildProcesses.Command(runtime, [cli, .. words], package.Folder)
            : Program(package.Manager, folder) is { } program ? Started(program, words, package.Folder)
            : null;
        if (start is null)
        {
            return (null, Missing(package));
        }
        if (folder is not null)
        {
            string inherited = start.Environment.TryGetValue("PATH", out string? set) && set is not null ? set : string.Empty;
            start.Environment["PATH"] = $"{folder}{Path.PathSeparator}{inherited}";
        }
        return (start, null);
    }

    /// <summary>The words a start's command line is said in, as a run names what it runs ("pnpm run dev").</summary>
    public static string Said(NodePackage package, string script, IReadOnlyList<string> arguments) =>
        string.Join(' ', [NodeProjects.ProgramOf(package.Manager), .. NodeProjects.RunArguments(package.Manager, script, arguments)]);

    /// <summary>A package manager's program beside the Node.js in use, else on the PATH: on Windows its .exe before its .cmd.</summary>
    private static string? Program(NodeManager manager, string? nodeFolder)
    {
        string name = NodeProjects.ProgramOf(manager);
        string[] names = OperatingSystem.IsWindows() ? [$"{name}.exe", $"{name}.cmd"] : [name];
        return (nodeFolder is null ? null : names.Select(n => Path.Combine(nodeFolder, n)).FirstOrDefault(File.Exists))
            ?? names.Select(Programs.OnPath).FirstOrDefault(p => p is not null);
    }

    /// <summary>A program's start, a Windows .cmd through cmd.exe, which alone runs one.</summary>
    private static ProcessStartInfo Started(string program, IReadOnlyList<string> words, string folder) =>
        program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? ChildProcesses.Command(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", program, .. words], folder)
            : ChildProcesses.Command(program, words, folder);

    /// <summary>Why a package's manager cannot run it, with how to get it.</summary>
    private static string Missing(NodePackage package)
    {
        string name = NodeProjects.ProgramOf(package.Manager);
        string how = package.Manager switch
        {
            NodeManager.Npm => "it comes with Node.js: install Node.js again from https://nodejs.org",
            NodeManager.Pnpm => "corepack enable pnpm puts it there with Node.js, or see https://pnpm.io/installation",
            NodeManager.Yarn => "corepack enable yarn puts it there with Node.js, or see https://yarnpkg.com/getting-started/install",
            _ => "see https://bun.sh",
        };
        return $"{package.Name} runs its scripts with {name} ({package.ManagerSource}), and {name} was not found beside Node.js or on the PATH: {how}";
    }
}
