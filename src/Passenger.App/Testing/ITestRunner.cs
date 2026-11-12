using System.Text;

namespace Passenger.App.Testing;

/// <summary>A test host a runner would start, to be started under the debugger instead, with the framework it runs on
/// ("net10.0") where the runner knows it.</summary>
public sealed record TestHostStart(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment,
    string? Framework = null);

/// <summary>What a debugged test host is started with for Hot Reload: the variables its runtime and Passenger's update
/// handlers hook take, and the hook, which the attach to it takes.</summary>
public sealed record TestHostReload(IReadOnlyDictionary<string, string> Variables, Debugging.UpdateHandlerHook Hook);

/// <summary>Debugging for a runner: it hands over the test host it would start, or names the one it started.</summary>
public interface ITestDebugLauncher
{
    /// <summary>Starts the test host under the debugger, its process id once it runs.</summary>
    Task<int> LaunchAsync(TestHostStart start);

    /// <summary>What the test host a test assembly runs in is started with for Hot Reload, while the debugger has it on;
    /// null while it is off, or where the assembly's .NET takes no hook.</summary>
    TestHostReload? Reload(string assembly, IReadOnlyDictionary<string, string> environment);

    /// <summary>Attaches the debugger to a test host the runner started (with Hot Reload and its hook, where it was
    /// started for them); true once the debugger has hold of it.</summary>
    Task<bool> AttachAsync(int processId, TestHostReload? reload = null);

    /// <summary>Starts a Godot run of a project's tests (GUT's, GoDotTest's) under the debugger as Debug starts its game: its
    /// GDScript under the GDScript debugger, and its C# under .NET's where the game has C# (<paramref name="csharp"/>), the
    /// debugger's own arguments after the first <paramref name="leading"/>; done once it has started, with the run's end. Each
    /// line the game writes is handed to <paramref name="heard"/> as the debug session hears it, on the session's thread.</summary>
    Task<Task> DebugGameAsync(TestHostStart start, string folder, int leading, bool csharp, Action<string>? heard = null);
}

/// <summary>A built test assembly whose tests a runner could not look through (its test host never started, or broke off),
/// and what the runner said of it, as one line.</summary>
public sealed record UnfoundTests(TestAssembly Assembly, string Why)
{
    /// <summary>What a runner says where it said nothing of why.</summary>
    private const string Unsaid = "the runner gave no reason";

    /// <summary>An assembly's tests not found, as its runner said why: an exception's message without its type's name or its
    /// stack, its lines joined, the assembly's path named by its file's name.</summary>
    public static UnfoundTests Of(TestAssembly assembly, string said)
    {
        StringBuilder line = new();
        foreach (string each in said.Split('\n'))
        {
            string text = each.Trim();
            if (text.StartsWith("at ", StringComparison.Ordinal) && each.StartsWith(' '))
            {
                break;
            }
            if (line.Length == 0 && text.IndexOf(": ", StringComparison.Ordinal) is > 0 and int named
                && text.AsSpan(0, named).EndsWith("Exception", StringComparison.Ordinal) && !text.AsSpan(0, named).Contains(' '))
            {
                text = text[(named + 2)..];
            }
            if (text.Length > 0)
            {
                line.Append(line.Length == 0 || text[0] is '.' or ',' ? string.Empty : " ").Append(text);
            }
        }
        string why = line.Length == 0 ? Unsaid : line.ToString().Replace(assembly.Path, System.IO.Path.GetFileName(assembly.Path), StringComparison.Ordinal);
        return new UnfoundTests(assembly, why);
    }
}

/// <summary>What a look through built test assemblies found: their tests, and each assembly whose tests it could not find.</summary>
public sealed record FoundTests(IReadOnlyList<TestItem> Tests, IReadOnlyList<UnfoundTests> Unfound)
{
    /// <summary>Tests found where every assembly was looked through.</summary>
    public static FoundTests All(IReadOnlyList<TestItem> tests) => new(tests, []);
}

/// <summary>What finds and runs a folder's tests: VSTest's vstest.console (<see cref="VsTestRunner"/>), Microsoft.Testing.Platform's
/// own protocol (<see cref="TestingPlatformRunner"/>), or both behind one (<see cref="TestRunners"/>). It works in the IDE's
/// own tests and outcomes, so nothing above it knows which. Each call is canceled by its token: a discovery stops looking,
/// a run stops after what has run.</summary>
public interface ITestRunner : IDisposable
{
    /// <summary>Whether it can be asked again: not once a canceled discovery never said it had stopped.</summary>
    bool IsUsable { get; }

    /// <summary>The tests in built assemblies, each with its assembly's project and target framework, and the assemblies whose
    /// tests could not be found, with why: one such hides none of the others' tests.</summary>
    Task<FoundTests> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default);

    /// <summary>Runs tests it discovered, their outcomes told a batch at a time as they come; under the debugger when a
    /// launcher is given, with these variables in the test hosts' environment, and a Godot it starts started as
    /// <paramref name="godot"/> says.</summary>
    Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, GodotStart? godot = null, CancellationToken cancel = default);
}
