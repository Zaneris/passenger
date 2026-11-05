using Passenger.App.Godot;
using Passenger.Core;
using Passenger.Workspace.GDScript;

namespace Passenger.Tests;

/// <summary>The engine's API read from two Godots of different versions at one moment, as a person changing the Godot chosen
/// while the first is dumped does: each Godot's dump is its own, however their runs overlap. Stand-ins, each writing a class
/// of its own into the folder it is started in only once the other has started there too.</summary>
[Collection(Beside.Name)]
public sealed class GodotEngineApisTests
{
    [Fact]
    public async Task Two_godots_dumped_at_one_moment_each_give_their_own_api_and_leave_no_folder_behind()
    {
        string folder = PathNames.Canonical(TestFolders.Create("engine-apis-"));
        string first = StandIn(folder, "first", "second");
        string second = StandIn(folder, "second", "first");
        IReadOnlyDictionary<string, string> home = GodotUserFolders.Under(Path.Combine(folder, "home"));
        GodotEngineApis firstApis = new(() => first, () => home);
        GodotEngineApis secondApis = new(() => second, () => home);

        GodotEngineApi?[] read = await Task.WhenAll(firstApis.ApiAsync(), secondApis.ApiAsync());

        Assert.True(read[0] is not null && read[1] is not null, "A dump overlapping another's gave no API, so GDScript was left without the engine's types");
        Assert.True(read[0]!.Classes.ContainsKey("FirstOnly") && !read[0]!.Classes.ContainsKey("SecondOnly"),
            $"The first Godot's API holds {string.Join(", ", read[0]!.Classes.Keys)}: another Godot's dump was kept as its own, so GDScript is typed by the wrong engine");
        Assert.True(read[1]!.Classes.ContainsKey("SecondOnly") && !read[1]!.Classes.ContainsKey("FirstOnly"),
            $"The second Godot's API holds {string.Join(", ", read[1]!.Classes.Keys)}: another Godot's dump was kept as its own, so GDScript is typed by the wrong engine");
        string cache = Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "cache", "godot", "extension-api");
        Assert.DoesNotContain(Directory.EnumerateDirectories(cache), d => Path.GetFileName(d).StartsWith("reading-", StringComparison.Ordinal));
    }

    /// <summary>A stand-in Godot answering <c>--version</c> with a version of its own, which asked for its dump marks its start,
    /// waits for the other's, and writes a class of its own into its working folder.</summary>
    private static string StandIn(string folder, string name, string other)
    {
        string own = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(own, "GodotSharp", "Api"));
        string started = Path.Combine(folder, $"{name}.started");
        string waited = Path.Combine(folder, $"{other}.started");
        string answer = $"4.{(name == "first" ? 91 : 92)}.0.stable.mono.official.standin";
        string dump = $$"""{ "header": { "version_major": 4, "version_minor": 7 }, "classes": [ { "name": "{{char.ToUpperInvariant(name[0])}}{{name[1..]}}Only" } ] }""";
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(own, "godot.cmd");
            File.WriteAllText(batch, $"@echo off\r\nif \"%~2\"==\"--version\" (\r\n  echo {answer}\r\n  exit /b 0\r\n)\r\necho.>\"{started}\"\r\nset /a tries=0\r\n"
                + $":wait\r\nif exist \"{waited}\" goto write\r\nset /a tries+=1\r\nif %tries% geq 60 goto write\r\nping -n 2 127.0.0.1 >nul\r\ngoto wait\r\n"
                + $":write\r\necho {dump}>extension_api.json\r\n");
            return batch;
        }
        string script = Path.Combine(own, "godot");
        File.WriteAllText(script, $"#!/bin/sh\ncase \" $* \" in *\" --version \"*) echo {answer}; exit 0;; esac\n: > '{started}'\ntries=0\n"
            + $"while [ ! -e '{waited}' ] && [ \"$tries\" -lt 600 ]; do sleep 0.1; tries=$((tries + 1)); done\nprintf '%s' '{dump}' > extension_api.json\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
