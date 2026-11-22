using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Passenger.Tests;

/// <summary>What a class marked [SameOnEverySystem] may run, read from the built tests: the mark keeps it to one system's
/// report lane, so nothing it runs may behave otherwise on another, the C# language server, the project host's reads and
/// the language servers whose answers it reads counted alike everywhere. Read are its tests, constructors and disposal,
/// its bases', its class fixtures' and every method of the tests they reach (a lambda's, an async method's body): a call
/// out of the tests is held to <see cref="Differing"/>, a helper of theirs to <see cref="DifferingHelpers"/>, the actions
/// they name to <see cref="Starting"/>, its own words to naming no system and no Godot project's file. What the IL cannot
/// show, what its windows ran, the mark itself holds (<see cref="SameOnEverySystemAttribute"/>).</summary>
[Collection(Beside.Name)]
public sealed partial class SameOnEverySystemRulesTests
{
    /// <summary>Calls whose work differs by system, by the start of the called member's full name (Type::Member), and why.</summary>
    private static readonly (string Called, string Why)[] Differing =
    [
        ("System.Diagnostics.Process", "starts or reads a process"),
        ("Passenger.Core.ChildProcesses", "starts a process"),
        ("Passenger.Core.Programs", "finds a program on the system"),
        ("Passenger.Core.Dotnet", "finds .NET as the system has it"),
        ("Passenger.Core.DotnetProcesses", "reads the system's processes"),
        ("Passenger.Terminal.", "runs the terminal"),
        ("Passenger.App.Terminal", "runs the terminal"),
        ("Passenger.Dap.", "runs the debugger"),
        ("Passenger.Debugger.", "runs the debugger"),
        ("Passenger.DebugAdapter.", "runs the debugger"),
        ("Passenger.App.Debugging.Debug", "runs the debugger"),
        ("Passenger.App.Debugging.InProcessAdapter", "runs the debugger"),
        ("Passenger.App.Debugging.WebAssemblyAdapter", "runs the debugger"),
        ("Passenger.App.Debugging.Browser", "runs a browser"),
        ("Passenger.App.Debugging.HotReload", "runs Hot Reload"),
        ("Passenger.App.Debugging.UpdateHandlerHook", "runs Hot Reload"),
        ("Passenger.Profiler.", "profiles a process"),
        ("Passenger.App.BuildRunController", "builds and runs"),
        ("Passenger.Packages.", "restores or reads NuGet's packages"),
        ("Passenger.Vcs.", "runs Git"),
        ("Passenger.App.Vcs.", "runs Git"),
        ("Passenger.App.Containers.ContainerRun", "runs containers"),
        ("Passenger.App.Aspire.", "runs Aspire"),
        ("Passenger.App.Godot.", "runs Godot"),
        ("Passenger.App.Web.WebLanguages::InstallAsync", "installs a language's tools"),
        ("Passenger.App.Web.WebLanguages::UseTools", "runs a language's tools"),
        ("Passenger.App.Web.EslintService", "runs ESLint"),
        ("Passenger.App.Web.PrettierService", "runs Prettier"),
        ("Passenger.App.Browsers", "runs a browser"),
        ("Passenger.App.MainWindow::Browse", "opens a browser"),
        ("Passenger.App.MainWindow::ShowInFileManager", "opens the file manager"),
        ("System.IO.FileSystemWatcher", "watches files"),
        ("Passenger.Workspace.WatchedTree", "watches files"),
        ("Passenger.Core.WatchedFile", "watches files"),
        ("System.IO.Path::get_DirectorySeparatorChar", "reads the system's path separator"),
        ("System.IO.Path::DirectorySeparatorChar", "reads the system's path separator"),
        ("System.IO.Path::PathSeparator", "reads the system's path separator"),
        ("System.Environment::get_NewLine", "reads the system's line ending"),
        ("System.OperatingSystem::", "asks which system it runs on"),
        ("System.Runtime.InteropServices.RuntimeInformation", "asks which system it runs on"),
        ("Passenger.Core.CommandKey", "takes the system's command key"),
        ("Passenger.Editor.Keymap::get_Command", "takes the system's command key"),
        ("Avalonia.Input.Platform.PlatformHotkeyConfiguration", "takes the system's keys"),
        ("Avalonia.Input.TextInput.", "runs an input method"),
        ("Passenger.Editor.ViewInputMethod", "runs an input method"),
        ("Avalonia.Media.FontManager", "reads the system's fonts"),
        ("Avalonia.Media.FormattedText", "shapes text"),
        ("Avalonia.Media.TextFormatting.", "shapes text"),
        ("Avalonia.Media.GlyphRun", "shapes text"),
        ("Avalonia.Media.Imaging.RenderTargetBitmap", "reads drawn pixels"),
        ("Avalonia.Automation.Peers.", "speaks to a screen reader"),
        ("Avalonia.Automation.Provider.", "speaks to a screen reader"),
        ("Passenger.Editor.UiAutomation.", "speaks to a screen reader"),
        ("Passenger.App.MacTextElements", "speaks to a screen reader"),
        ("System.Net.Sockets.", "speaks through the system's sockets"),
        ("System.Net.Http.", "speaks through the system's sockets"),
        ("System.Net.HttpListener", "speaks through the system's sockets"),
        ("System.GC::GetAllocatedBytesForCurrentThread", "measures what the system's runtime, drawing and text shaping allocate"),
        ("System.GC::GetTotalAllocatedBytes", "measures what the system's runtime, drawing and text shaping allocate"),
        ("Xunit.Assert::Skip", "skips"),
    ];

    /// <summary>The tests' own helpers whose work differs by system, by type name or Type.Method, and why.</summary>
    private static readonly Dictionary<string, string> DifferingHelpers = new(StringComparer.Ordinal)
    {
        ["McpRig"] = "starts a Passenger of its own", ["Debuggees.Program"] = "runs a debuggee", ["Docker"] = "runs containers",
        ["ContainerPrograms"] = "runs containers", ["AspireApp"] = "runs Aspire", ["AspireFunctionsApp"] = "runs Aspire",
        ["AspireDashboardHost"] = "runs Aspire", ["AspireInteractionHost"] = "runs Aspire", ["AvaloniaApp"] = "builds and runs an app",
        ["WpfApp"] = "builds and runs an app", ["MauiApp"] = "builds and runs an app", ["BellApp"] = "builds and runs an app",
        ["TestGodotEditors"] = "runs Godot", ["StandInHttp"] = "serves HTTP on the system's sockets",
        ["StandInFeed"] = "serves HTTP on the system's sockets", ["StandInRegistry"] = "serves HTTP on the system's sockets",
        ["StandInGitRemote"] = "runs Git", ["SilentPort"] = "listens on the system's sockets",
        ["StopRefusingPort"] = "listens on the system's sockets", ["DevCerts"] = "reads the system's certificates",
        ["TestVolumes"] = "makes the system's volumes", ["MacBridge"] = "speaks to a screen reader",
        ["AndroidSerials"] = "runs a device", ["SshKeygenQuestions"] = "runs ssh-keygen", ["RefreshClient"] = "runs a browser's page",
        ["TestPictures"] = "reads drawn pixels", ["Screenshot"] = "reads drawn pixels",
        ["HeldBytes"] = "measures what the system's runtime keeps",
    };

    /// <summary>The rig's own methods not followed, as Type.Method: what they run never decides a test's outcome.</summary>
    private static readonly HashSet<string> RigsOwn = new(StringComparer.Ordinal)
    {
        // Naming the programs still running where a wait gives up.
        "Ui.ProgramsRunning",
        // Deleting a test's folders once it has ended, waiting out what holds them on Windows.
        "TestFolders.Delete",
        // A table's skip where each of its rows skipped, which the rows' own code, followed, shows.
        "Cases.SkipWhereEverySkipped",
    };

    /// <summary>A system named in a test's own words.</summary>
    [GeneratedRegex(@"\b(macOS|Mac OS|OS X|Linux|Ubuntu|win-(x64|arm64|x86)|osx-(x64|arm64)|linux-(x64|arm64|musl))\b|(?<!Tool )\bWindows\b")]
    private static partial Regex SystemNamed();

    /// <summary>A file of a Godot project the test makes or opens, which a Godot of the system's own reads.</summary>
    [GeneratedRegex(@"\.(gd|tscn|tres|gdshader)\b|\bproject\.godot\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramsFile();

    /// <summary>The IDE's actions that build, run, debug, profile, publish or test a program, open the terminal or a
    /// browser, work Git, containers, Aspire, migrations or Godot, or install or update, by their ids, as a test names them
    /// to run: any its code or a helper it reaches names.</summary>
    private static readonly Dictionary<string, string> Starting = typeof(Passenger.App.AppActions)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && StartingAction().IsMatch(field.Name))
        .ToDictionary(field => (string)field.GetRawConstantValue()!, field => field.Name, StringComparer.Ordinal);

    /// <summary>The names of the actions <see cref="Starting"/> takes.</summary>
    [GeneratedRegex(@"^(Build|Rebuild|Publish|Run|Debug|Profile|AttachProfiler|AttachToProcess|Reattach|Rerun|ApplyHotReload|ShowTerminal|NewTerminal|OpenInTerminal|OpenInBrowser|Commit$|Push$|Fetch$|Stash$|Unstash$|CloneRepository|UpdateProject|ContinueRebase|Abort|Service(Run|Debug|Attach|Profile)|Container(Start|Stop|Restart|Pause|Remove|Scale|Pull|Prune|Shell)|AddMigration|RemoveLastMigration|UpdateDatabase|ScriptMigrations|BundleMigrations|Install|CheckForUpdates|RestartToUpdate|TestEndpoint|NuGetUpdateAll|Godot|AspireCommand)")]
    private static partial Regex StartingAction();

    /// <summary>A class marked [SameOnEverySystem] starts no process, build, run, terminal, debugger, Git or container of its
    /// own, no browser, file watcher, socket, font, shaping, screen reader, input method, system key or path separator,
    /// asks no system's name, measures no allocation and skips nothing; it is in neither the gate, which runs on every
    /// system, nor the Alone collection, whose classes hold to a time, debug or count every thread's allocations.</summary>
    [Fact]
    public void A_class_marked_same_on_every_system_runs_nothing_that_behaves_otherwise_on_another()
    {
        Assembly tests = typeof(SameOnEverySystemRulesTests).Assembly;
        Type[] marked = [.. tests.GetTypes().Where(type => type.GetCustomAttribute<SameOnEverySystemAttribute>() is not null).OrderBy(type => type.FullName, StringComparer.Ordinal)];
        Assert.True(marked.Length > 0, "No class marked [SameOnEverySystem] was found, so the built tests were not read");
        using Reach reach = new(tests);
        List<string> broken = [.. marked.SelectMany(reach.Breaks)];
        TestContext.Current.TestOutputHelper?.WriteLine($"Marked [SameOnEverySystem]:\n{string.Join('\n', marked.Select(type => type.Name))}");
        Assert.True(broken.Count == 0, "A class marked [SameOnEverySystem] runs on one system alone, yet runs what another system may do otherwise, "
            + "so a fault there would go unseen: take the mark off, or keep the class to what behaves the same everywhere:\n" + string.Join('\n', broken));
    }

    /// <summary>What the built tests' methods reach, read from their IL.</summary>
    private sealed class Reach(Assembly assembly) : IDisposable
    {
        private readonly Module module = assembly.ManifestModule;
        private readonly PEReader pe = new(File.OpenRead(assembly.Location));

        public void Dispose() => pe.Dispose();

        /// <summary>Why a marked class breaks its rule, a line each, the way to each call said; none where it keeps it.</summary>
        public IEnumerable<string> Breaks(Type type)
        {
            if (type.GetCustomAttribute<GateAttribute>() is not null || Tests(type).Any(method => method.GetCustomAttribute<GateAttribute>() is not null))
            {
                yield return $"{type.Name} has tests in the gate, which runs on every system";
            }
            if (type.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>().Any(collection => collection.Name == Alone.Name))
            {
                yield return $"{type.Name} runs alone: it holds what it tests to a time, debugs, or counts every thread's allocations, which each system's runtime and libraries shape";
            }
            foreach (MethodInfo test in Tests(type).Where(Skips))
            {
                yield return $"{type.Name}.{test.Name} skips where something is missing, which differs by system";
            }
            MetadataReader metadata = pe.GetMetadataReader();
            Dictionary<MethodDefinitionHandle, string> path = [];
            Queue<MethodDefinitionHandle> next = new();
            foreach (MethodBase root in Roots(type))
            {
                MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(root.MetadataToken);
                if (root.Module == module && path.TryAdd(handle, $"{root.DeclaringType!.Name}.{root.Name}"))
                {
                    next.Enqueue(handle);
                }
            }
            HashSet<string> said = [];
            HashSet<Type> own = [.. Owners(type)];
            while (next.TryDequeue(out MethodDefinitionHandle method))
            {
                MethodDefinition definition = metadata.GetMethodDefinition(method);
                string outer = Outermost(metadata, definition.GetDeclaringType());
                string named = Named(metadata, method);
                if (DifferingHelpers.TryGetValue(outer, out string? helper) || DifferingHelpers.TryGetValue(named, out helper))
                {
                    string which = DifferingHelpers.ContainsKey(outer) ? outer : named;
                    if (said.Add($"{which}:{path[method]}"))
                    {
                        yield return $"{type.Name} reaches {which}, which {helper}: {path[method]}";
                    }
                    continue;
                }
                bool owned = own.Any(owner => owner.Name == outer && owner.Namespace == Namespace(metadata, definition.GetDeclaringType()));
                foreach ((EntityHandle called, string? words) in Instructions(metadata, method))
                {
                    if (words is not null)
                    {
                        if (owned && (SystemNamed().Match(words) is { Success: true } system) && said.Add($"system:{system.Value}"))
                        {
                            yield return $"{type.Name}'s words name a system ({system.Value}): {path[method]}";
                        }
                        if (owned && (ProgramsFile().Match(words) is { Success: true } file) && said.Add($"file:{file.Value}"))
                        {
                            yield return $"{type.Name} makes or opens a Godot project's file ({file.Value}), which a Godot of its own reads: {path[method]}";
                        }
                        if ((Starting.TryGetValue(words, out string? action) || words == Passenger.App.ToolWindowIds.Terminal) && said.Add($"action:{words}"))
                        {
                            yield return $"{type.Name} names {action ?? "the Terminal window"}, which starts what another system may run otherwise: {path[method]}";
                        }
                        continue;
                    }
                    foreach (MethodDefinitionHandle inside in Defined(metadata, called).Where(inside => !RigsOwn.Contains(Named(metadata, inside))))
                    {
                        if (path.TryAdd(inside, $"{path[method]} → {Named(metadata, inside)}"))
                        {
                            next.Enqueue(inside);
                        }
                    }
                    if (External(metadata, called) is { } name && Differing.FirstOrDefault(entry => name.StartsWith(entry.Called, StringComparison.Ordinal)) is { Why: { } why }
                        && said.Add($"{name}:{why}"))
                    {
                        yield return $"{type.Name} {why}: {path[method]} → {name}";
                    }
                }
            }
        }

        private static IEnumerable<MethodInfo> Tests(Type type) => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Xunit.v3.IFactAttribute));

        private static bool Skips(MethodInfo test) => test.GetCustomAttributes().OfType<Xunit.v3.IFactAttribute>()
            .Any(fact => fact.Skip is not null || fact.SkipWhen is not null || fact.SkipUnless is not null);

        /// <summary>Where a class's tests begin: each test, and the constructors and disposal of the class, its bases in the
        /// tests and its class fixtures.</summary>
        private IEnumerable<MethodBase> Roots(Type type)
        {
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            List<MethodBase> roots = [.. Tests(type)];
            foreach (Type owner in Owners(type))
            {
                roots.AddRange(owner.GetConstructors(Declared));
                roots.AddRange(owner.GetMethods(Declared).Where(method => method.Name is "Dispose" or "DisposeAsync" or "InitializeAsync"));
            }
            return roots;
        }

        /// <summary>The class, its bases in the tests and its class fixtures' types.</summary>
        private IEnumerable<Type> Owners(Type type)
        {
            List<Type> owners = [];
            for (Type? own = type; own is not null && own.Assembly == assembly; own = own.BaseType)
            {
                owners.Add(own);
            }
            owners.AddRange(type.GetInterfaces().Where(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IClassFixture<>))
                .Select(face => face.GetGenericArguments()[0]).Where(fixture => fixture.Assembly == assembly));
            return owners;
        }

        /// <summary>Each call, delegate made or field read of a method, as its member's handle, and each string it loads
        /// (with a nil handle); an async or iterator method's state machine as a call of its step.</summary>
        private IEnumerable<(EntityHandle Called, string? Words)> Instructions(MetadataReader metadata, MethodDefinitionHandle method)
        {
            MethodDefinition definition = metadata.GetMethodDefinition(method);
            if (definition.RelativeVirtualAddress == 0)
            {
                yield break;
            }
            foreach (CustomAttributeHandle attribute in definition.GetCustomAttributes())
            {
                if (AttributeType(metadata, metadata.GetCustomAttribute(attribute).Constructor) is nameof(AsyncStateMachineAttribute)
                    or nameof(IteratorStateMachineAttribute) or nameof(AsyncIteratorStateMachineAttribute)
                    && module.ResolveMethod(MetadataTokens.GetToken(method))?.GetCustomAttribute<StateMachineAttribute>()?.StateMachineType
                        .GetMethod(nameof(IAsyncStateMachine.MoveNext), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
                {
                    yield return (MetadataTokens.MethodDefinitionHandle(moveNext.MetadataToken), null);
                }
            }
            BlobReader il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                int code = il.ReadByte();
                code = code == 0xFE ? 0xFE00 | il.ReadByte() : code;
                // ldstr.
                if (code == 0x72)
                {
                    yield return (default, metadata.GetUserString((UserStringHandle)MetadataTokens.Handle(il.ReadInt32())));
                }
                // call, callvirt, newobj, jmp, ldftn, ldvirtftn, and the field loads and stores.
                else if (code is 0x28 or 0x6F or 0x73 or 0x27 or 0xFE06 or 0xFE07 or (>= 0x7B and <= 0x80))
                {
                    yield return (MetadataTokens.EntityHandle(il.ReadInt32()), null);
                }
                else
                {
                    // Its size first: a switch's is read from its operand, which the offset must then pass.
                    int operand = DroppedTasks.OperandSize(ref il, code);
                    il.Offset += operand;
                }
            }
        }

        /// <summary>The methods of the tests a called member is: its definition, or every method of its name in a generic
        /// type of the tests; none for another assembly's member or a field.</summary>
        private static IEnumerable<MethodDefinitionHandle> Defined(MetadataReader metadata, EntityHandle called)
        {
            if (called.Kind == HandleKind.MethodSpecification)
            {
                called = metadata.GetMethodSpecification((MethodSpecificationHandle)called).Method;
            }
            if (called.Kind == HandleKind.MethodDefinition)
            {
                return [(MethodDefinitionHandle)called];
            }
            if (called.Kind != HandleKind.MemberReference)
            {
                return [];
            }
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)called);
            EntityHandle parent = member.Parent;
            if (parent.Kind == HandleKind.TypeSpecification && GenericOf(metadata, (TypeSpecificationHandle)parent) is { } generic)
            {
                parent = generic;
            }
            string name = metadata.GetString(member.Name);
            return parent.Kind == HandleKind.TypeDefinition
                ? [.. metadata.GetTypeDefinition((TypeDefinitionHandle)parent).GetMethods().Where(method => metadata.StringComparer.Equals(metadata.GetMethodDefinition(method).Name, name))]
                : [];
        }

        /// <summary>Another assembly's member a call or field names, as Namespace.Type::Member (a nested type's as its
        /// outer's then +Name); null for one of the tests'.</summary>
        private static string? External(MetadataReader metadata, EntityHandle called)
        {
            if (called.Kind == HandleKind.MethodSpecification)
            {
                called = metadata.GetMethodSpecification((MethodSpecificationHandle)called).Method;
            }
            if (called.Kind != HandleKind.MemberReference)
            {
                return null;
            }
            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)called);
            EntityHandle parent = member.Parent;
            if (parent.Kind == HandleKind.TypeSpecification && GenericOf(metadata, (TypeSpecificationHandle)parent) is { } generic)
            {
                parent = generic;
            }
            return parent.Kind == HandleKind.TypeReference ? $"{TypeName(metadata, (TypeReferenceHandle)parent)}::{metadata.GetString(member.Name)}" : null;
        }

        /// <summary>The generic type a generic instance names.</summary>
        private static EntityHandle? GenericOf(MetadataReader metadata, TypeSpecificationHandle handle)
        {
            BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
            if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            {
                return null;
            }
            signature.ReadSignatureTypeCode();
            return signature.ReadTypeHandle();
        }

        private static string TypeName(MetadataReader metadata, TypeReferenceHandle handle)
        {
            TypeReference type = metadata.GetTypeReference(handle);
            string name = metadata.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? $"{TypeName(metadata, (TypeReferenceHandle)type.ResolutionScope)}+{name}"
                : $"{metadata.GetString(type.Namespace)}.{name}";
        }

        /// <summary>The name of the type a type of the tests is declared in, outermost: a lambda's or a state machine's
        /// class is its method's.</summary>
        private static string Outermost(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            return type.GetDeclaringType() is { IsNil: false } outer ? Outermost(metadata, outer) : metadata.GetString(type.Name);
        }

        private static string Namespace(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            return type.GetDeclaringType() is { IsNil: false } outer ? Namespace(metadata, outer) : metadata.GetString(type.Namespace);
        }

        /// <summary>Type.Method for a method of the tests, as the way to a call says it.</summary>
        private static string Named(MetadataReader metadata, MethodDefinitionHandle handle)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            return $"{Outermost(metadata, method.GetDeclaringType())}.{metadata.GetString(method.Name)}";
        }

        /// <summary>The name of an attribute's type, by its constructor.</summary>
        private static string? AttributeType(MetadataReader metadata, EntityHandle constructor) => constructor.Kind switch
        {
            HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent =>
                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.MethodDefinition => metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()).Name),
            _ => null,
        };
    }
}
