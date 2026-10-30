namespace Passenger.App.Testing;

/// <summary>A test as the IDE knows it, whoever found it: the code (a method with a test attribute) or a runner's
/// discovery, which finds each of a theory's data rows too. Its id is its project and full name, and a data row's name,
/// so the same test found again, or found by the other, is the same test and a run's results land where the code put
/// it. A runner's tests carry the target framework they were built for, which the id leaves out.</summary>
public sealed record TestItem(string Project, string FullyQualifiedName, string DisplayName)
{
    /// <summary>The target framework of the assembly a runner found it in; null for a test found in the code.</summary>
    public string? Framework { get; init; }

    /// <summary>A data row's name as the tree shows it under its method ("Doubles(value: 1, twice: 2)"); null for a
    /// test that is its method.</summary>
    public string? Row { get; init; }

    public string? Path { get; init; }

    /// <summary>The line its name is on, from one; zero when unknown.</summary>
    public int Line { get; init; }

    /// <summary>Its traits, categories included, as name and value.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Traits { get; init; } = [];

    /// <summary>The names a test found in the code gives its cases (NUnit's TestName), which a runner names them by in
    /// place of the method's.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>The method it is or runs: its full name without the arguments NUnit names a case or a fixture's
    /// instance by.</summary>
    public string Method => MethodOf(FullyQualifiedName);

    /// <summary>The id of the method it is or belongs to, which a test found in the code has as its own.</summary>
    public string MethodId => $"{Project}::{Method}";

    public string Id => Row is null ? MethodId : $"{MethodId}::{Row}";

    /// <summary>Where the tree keeps it: its id under the target framework it ran for.</summary>
    internal string Key => $"{Framework}|{Id}";

    /// <summary>The length of its <see cref="Key"/>.</summary>
    internal int KeyLength => (Framework?.Length ?? 0) + 1 + Project.Length + 2 + Unargued(FullyQualifiedName, []) + (Row is null ? 0 : 2 + Row.Length);

    /// <summary>Its <see cref="Key"/> written into <paramref name="into"/>, which <see cref="KeyLength"/> fits.</summary>
    internal void WriteKey(Span<char> into)
    {
        _ = into.TryWrite($"{Framework}|{Project}::", out int at);
        at += Unargued(FullyQualifiedName, into[at..]);
        if (Row is not null)
        {
            _ = into[at..].TryWrite($"::{Row}", out _);
        }
    }

    /// <summary>"A.B.Class.Method(1, 2)" as "A.B.Class.Method", and a fixture's instance's "A.B.Fixture(1).Method" or
    /// "A.B.Fixture&lt;Int32&gt;.Method" as "A.B.Fixture.Method".</summary>
    public static string MethodOf(string fullyQualifiedName) =>
        fullyQualifiedName.AsSpan().IndexOfAny('(', '<') < 0 ? fullyQualifiedName
            : string.Create(Unargued(fullyQualifiedName, []), fullyQualifiedName, (into, name) => Unargued(name, into));

    /// <summary>The data row a runner's name for a test or a result says it is, as the tree names it: a fixture's
    /// instance's class and method ("Fixture(1).Method(2)"), else its display name less its type's ("Doubles(value: 1)"
    /// for xUnit, "Doubles(1)" for NUnit, "Doubles (1)" for MSTest); null for a test that is its method, whose name is the
    /// method's or has no arguments.</summary>
    public static string? RowOf(string fullyQualifiedName, string displayName)
    {
        if (Instance(fullyQualifiedName) is { } instance)
        {
            return instance;
        }
        string method = MethodOf(fullyQualifiedName);
        int dot = method.LastIndexOf('.');
        string shown = Shown(method, displayName);
        return shown == method[(dot + 1)..] || shown == method || (!shown.Contains('(', StringComparison.Ordinal) && method.Length == fullyQualifiedName.Length) ? null : shown;
    }

    /// <summary>A display name less the type its method's full name begins with ("Doubles(value: 1)" of
    /// "A.B.Class.Doubles(value: 1)"): a data row's name as the tree shows it.</summary>
    public static string Shown(string method, string displayName)
    {
        int dot = method.LastIndexOf('.');
        return dot > 0 && displayName.StartsWith(method[..(dot + 1)], StringComparison.Ordinal) ? displayName[(dot + 1)..] : displayName;
    }

    /// <summary>A test of a fixture's instance, its class named with the arguments or type arguments it was made with, as
    /// its class and method from its full name ("Fixture(1).Method" of "A.B.Fixture(1).Method"); null for any other.</summary>
    private static string? Instance(string fullyQualifiedName)
    {
        int type = -1;
        int method = -1;
        (int Depth, char Quote) inside = default;
        for (int at = 0; at < fullyQualifiedName.Length; at++)
        {
            if (Outside(fullyQualifiedName, ref at, ref inside) && fullyQualifiedName[at] == '.')
            {
                (type, method) = (method, at);
            }
        }
        return method > 0 && fullyQualifiedName.AsSpan(type + 1, method - type - 1).IndexOfAny('(', '<') >= 0 ? fullyQualifiedName[(type + 1)..] : null;
    }

    /// <summary>The length of a runner's name less its argument lists ("(1, \"a)\")", "&lt;Int32&gt;"), wherever they are,
    /// that much written into <paramref name="into"/> as far as it holds; a list never closed takes the rest of the name.</summary>
    private static int Unargued(ReadOnlySpan<char> name, Span<char> into)
    {
        int written = 0;
        (int Depth, char Quote) inside = default;
        for (int at = 0; at < name.Length; at++)
        {
            if (Outside(name, ref at, ref inside))
            {
                if (written < into.Length)
                {
                    into[written] = name[at];
                }
                written++;
            }
        }
        return written;
    }

    /// <summary>Whether a name's character is outside its argument lists, counting the lists and quoted words it goes into
    /// and out of; an escaped character in quotes is passed over with its backslash.</summary>
    private static bool Outside(ReadOnlySpan<char> name, ref int at, ref (int Depth, char Quote) inside)
    {
        char c = name[at];
        if (inside.Quote != '\0')
        {
            if (c == '\\')
            {
                at++;
            }
            else if (c == inside.Quote)
            {
                inside.Quote = '\0';
            }
            return false;
        }
        if (c is '(' or '<')
        {
            inside.Depth++;
            return false;
        }
        if (inside.Depth == 0)
        {
            return true;
        }
        if (c is ')' or '>')
        {
            inside.Depth--;
        }
        else if (c is '"' or '\'')
        {
            inside.Quote = c;
        }
        return false;
    }
}

/// <summary>A test project's tests as its code declares them, and the methods each of its files declares, by which a
/// test a runner found in a file and since removed from it is known to be gone.</summary>
public sealed record CodeTests(IReadOnlyList<TestItem> Tests, IReadOnlyDictionary<string, IReadOnlySet<string>> MethodsByFile)
{
    public static readonly CodeTests None = new([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));
}

/// <summary>How a test, or one of its data rows, ran, as its runner reports it.</summary>
public sealed record TestOutcome(TestItem Test, TestState State, TimeSpan Duration)
{
    /// <summary>The result's own name, which for a theory whose rows the runner makes as it runs names the row.</summary>
    public string? DisplayName { get; init; }

    public string? Message { get; init; }

    public string? StackTrace { get; init; }

    /// <summary>What the test wrote.</summary>
    public string? Output { get; init; }

    /// <summary>The files its result carries: what the test attached (xunit's AddAttachment, MSTest's AddResultFile, NUnit's
    /// AddTestAttachment), and the frames of a failing headless test's windows (<see cref="TestFrames"/>).</summary>
    public IReadOnlyList<TestAttachment> Attachments { get; init; } = [];

    /// <summary>The row this result is of, the test's own or one its run made; null for the method itself.</summary>
    public string? Row => Test.Row ?? (DisplayName is { } shown ? TestItem.RowOf(Test.FullyQualifiedName, shown) : null);
}

/// <summary>A file a test's result carries, by the name the test gave it, where its runner wrote it.</summary>
public sealed record TestAttachment(string Name, string Path)
{
    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>A picture the Unit Tests window shows as a frame of the result (a PNG of a window, as a rule).</summary>
    public bool IsPicture => Pictures.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>An attachment as a runner names it: a file URI or a path.</summary>
    public static TestAttachment Of(string name, string uriOrPath)
    {
        // Only a written URI is read as one: a path with % or # in it is a path.
        string path = uriOrPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(uriOrPath, UriKind.Absolute, out Uri? uri) ? uri.LocalPath : uriOrPath;
        return new TestAttachment(name.Length > 0 ? name : System.IO.Path.GetFileName(path), path);
    }
}

/// <summary>A test host a runner would start, to be started under the debugger instead.</summary>
public sealed record TestHostStart(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>Debugging for a runner: it hands over the test host it would start, or names the one it started.</summary>
public interface ITestDebugLauncher
{
    /// <summary>Starts the test host under the debugger, its process id once it runs.</summary>
    Task<int> LaunchAsync(TestHostStart start);

    /// <summary>Attaches the debugger to a test host the runner started; true once the debugger has hold of it.</summary>
    Task<bool> AttachAsync(int processId);
}

/// <summary>What finds and runs a folder's tests: VSTest's vstest.console (<see cref="VsTestRunner"/>), Microsoft.Testing.Platform's
/// own protocol (<see cref="TestingPlatformRunner"/>), or both behind one (<see cref="TestRunners"/>). It works in the IDE's
/// own tests and outcomes, so nothing above it knows which. Each call is canceled by its token: a discovery stops looking,
/// a run stops after what has run.</summary>
public interface ITestRunner : IDisposable
{
    /// <summary>Whether it can be asked again: not once a canceled discovery never said it had stopped.</summary>
    bool IsUsable { get; }

    /// <summary>The tests in built assemblies, each with its assembly's project and target framework.</summary>
    Task<IReadOnlyList<TestItem>> DiscoverAsync(IReadOnlyList<TestAssembly> assemblies, Action<string>? log = null, CancellationToken cancel = default);

    /// <summary>Runs tests it discovered, their outcomes told a batch at a time as they come; under the debugger when a
    /// launcher is given, and with these variables in the test hosts' environment.</summary>
    Task<RunSummary> RunAsync(IReadOnlyList<TestItem> tests, Action<IReadOnlyList<TestOutcome>> outcomes, Action<string>? log = null,
        ITestDebugLauncher? debugger = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancel = default);
}

/// <summary>How a run ended: how long its tests took, and whether it was stopped or broke off.</summary>
public sealed record RunSummary(TimeSpan Elapsed, bool Canceled, bool Aborted, string? Error)
{
    /// <summary>Every outcome of the run, which the run's end holds to whatever order they were told in.</summary>
    public IReadOnlyList<TestOutcome> Outcomes { get; init; } = [];

    /// <summary>This run and the next as one: their times added, stopped or broken off if either was, every outcome.</summary>
    public RunSummary And(RunSummary next) => new(Elapsed + next.Elapsed, Canceled || next.Canceled, Aborted || next.Aborted, Error ?? next.Error)
    {
        Outcomes = [.. Outcomes, .. next.Outcomes],
    };
}
