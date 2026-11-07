using System.Text.Json;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Godot sweep's own check of a project (tools/godot-sweep.py), run as the sweep runs it through a real headless
/// Godot: what Godot says of each resource it loads is told against that resource, however Godot's outputs interleave.</summary>
[Collection(Beside.Name)]
public sealed class GodotSweepCheckTests : IDisposable
{
    /// <summary>The check's script run by the sweep's own functions, each of its results printed as JSON.</summary>
    private const string Driver = """
        import importlib.util, json, os, sys
        tool, godot, project, work = sys.argv[1:]
        spec = importlib.util.spec_from_file_location("sweep", tool)
        sweep = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(sweep)
        check = os.path.join(work, "check_res.gd")
        with open(check, "w") as written:
            written.write(sweep.CHECK_SCRIPT)
        env = {**os.environ, **sweep.homes(os.path.join(work, "home")), "SDL_JOYSTICK_HIDAPI": "0"}
        code, out, _ = sweep.run([godot, "--headless", "--path", project, "--script", check], env=env, limit=120)
        print(json.dumps({"code": code, "errors": sweep.checked(out), "log": out}))
        """;

    private readonly string folder = TestFolders.Create("godot-sweep-check-");

    public void Dispose() => TestFolders.Delete(folder);

    [Fact]
    public async Task A_scripts_load_errors_are_told_against_it_and_a_clean_script_has_none()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The sweep's calls go through mcp-call.py, which runs on macOS and Linux alone");
        Assert.SkipWhen(Programs.OnPath("python3") is null, "No python3 on the PATH to run the sweep's check");
        Assert.SkipWhen(GodotExecutables.Find(null) is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications) to load the project");
        string project = Path.Combine(folder, "project");
        TestFolders.Write(folder, "project/project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Swept\"\n");
        TestFolders.Write(folder, "project/good.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tprint(\"fine\")\n");
        TestFolders.Write(folder, "project/broken.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tvar x := \n");

        string tool = Path.Combine(XamlTests.Repository, "tools", "godot-sweep.py");
        ChildResult ended = await ChildProcesses.RunAsync(ChildProcesses.Command("python3", ["-I", "-B", "-c", Driver, tool, GodotExecutables.Find(null)!.Executable, project, folder], folder),
            limit: TimeSpan.FromMinutes(3).Scaled(), cancel: TestContext.Current.CancellationToken);
        Assert.True(ended.Succeeded, $"The sweep's check exited with {ended.ExitCode}:\n{ended.Output}{ended.Error}");
        using JsonDocument read = JsonDocument.Parse(ended.Output);
        string log = JsonMembers.Text(read.RootElement, "log") ?? string.Empty;
        Assert.True(JsonMembers.Number(read.RootElement, "code") == 0, $"Godot's check did not end by itself:\n{log}");
        JsonElement? errors = JsonMembers.Object(read.RootElement, "errors");
        Assert.True(JsonMembers.Texts(errors, "res://broken.gd").Any(e => e.Contains("Parse Error", StringComparison.Ordinal)),
            $"A release's sweep would report the broken script as loading clean:\n{log}");
        Assert.True(JsonMembers.Items(errors, "res://good.gd") is { Count: 0 } && JsonMembers.Value(errors, "res://good.gd") is not null, $"The clean script was checked with errors, or not at all:\n{log}");
    }
}
