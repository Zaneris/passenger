using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Passenger.RoslynPatch;

/// <summary>Writes Roslyn's server's Microsoft.CodeAnalysis.LanguageServer.dll (MIT) again without Visual Studio Telemetry's
/// session, which a server makes and starts from its Main whatever <c>--telemetryLevel</c> says, and whose start loads an
/// assembly left out of roslyn/ (#234): <c>LanguageServerTelemetry.CreateSession</c> returns null at once, as it does when
/// the server has no telemetry level, which every caller takes. The assembly is written as IL alone, so the runtime
/// compiles the method as patched rather than running its ReadyToRun code. The build fails, naming why, when the method
/// is no longer the session's one way in, or when another of the server's assemblies makes or starts a session.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Passenger.RoslynPatch : error PRP0001: takes the server's assembly, the file to write, and the assemblies left out of the server");
            return 1;
        }
        try
        {
            Patch.Write(args[0], args[1], args[2..]);
            return 0;
        }
        catch (PatchException e)
        {
            // MSBuild's own form, so the build fails with the line as an error.
            Console.Error.WriteLine($"Passenger.RoslynPatch : error PRP0002: {e.Message}");
            return 1;
        }
    }
}

/// <summary>Why the server could not be written without the session.</summary>
public sealed class PatchException(string message) : Exception(message);

public static class Patch
{
    public const string TelemetryType = "Microsoft.CodeAnalysis.LanguageServer.Telemetry.LanguageServerTelemetry";

    /// <summary>Writes the server's assembly to <paramref name="output"/> without the session, once no other method in the
    /// server's folder but those it leaves out makes or starts one; a file in place there is replaced whole.</summary>
    public static void Write(string server, string output, IReadOnlyCollection<string> leftOut)
    {
        if (!File.Exists(server))
        {
            throw new PatchException($"{server} is not there: the server's package has changed its layout");
        }
        string folder = Path.GetDirectoryName(Path.GetFullPath(server))!;
        Starts.Check(folder, leftOut);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        string written = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (DefaultAssemblyResolver resolver = new())
            {
                resolver.AddSearchDirectory(folder);
                using ModuleDefinition module = ModuleDefinition.ReadModule(server,
                    new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver });
                CutOff(module, server);
                module.Write(written);
            }
            Verify(written, server);
            File.Move(written, output, overwrite: true);
        }
        finally
        {
            File.Delete(written);
        }
    }

    /// <summary>CreateSession returning null at once, once it is still the one caller of InitializeSession, which makes,
    /// sets and starts the session; and the module IL alone, for any processor, its strong name no longer signed.</summary>
    private static void CutOff(ModuleDefinition module, string server)
    {
        MethodDefinition create = Create(module, server);
        MethodDefinition[] initialize = [.. create.DeclaringType.Methods.Where(method => method.Name == "InitializeSession")];
        if (initialize.Length != 1 || !Calls(create, initialize[0]))
        {
            throw new PatchException($"{TelemetryType}.CreateSession in {server} no longer makes the session through one InitializeSession: read it again at the package's commit");
        }
        foreach (MethodDefinition other in module.GetTypes().SelectMany(type => type.Methods).Where(method => method != create && Calls(method, initialize[0])))
        {
            throw new PatchException($"{other.FullName} in {server} calls {TelemetryType}.InitializeSession, which makes and starts the session, as CreateSession does: cut that off too");
        }
        create.Body = new MethodBody(create);
        ILProcessor il = create.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        module.Attributes = (module.Attributes & ~(ModuleAttributes.ILLibrary | ModuleAttributes.StrongNameSigned | ModuleAttributes.Required32Bit
            | ModuleAttributes.Preferred32Bit)) | ModuleAttributes.ILOnly;
        module.Architecture = TargetArchitecture.I386;
    }

    /// <summary>The static CreateSession that returns the server's telemetry, the one there is.</summary>
    private static MethodDefinition Create(ModuleDefinition module, string server)
    {
        TypeDefinition type = module.GetType(TelemetryType) ?? throw new PatchException($"{server} has no {TelemetryType}: read the server's start again at the package's commit");
        MethodDefinition[] create = [.. type.Methods.Where(method => method.Name == "CreateSession" && method.IsStatic && method.ReturnType.FullName == TelemetryType)];
        return create.Length == 1 ? create[0] : throw new PatchException($"{TelemetryType} in {server} has {create.Length} static CreateSession methods returning it, where it had one: read it again at the package's commit");
    }

    private static bool Calls(MethodDefinition method, MethodDefinition called) =>
        method.HasBody && method.Body.Instructions.Any(instruction => instruction.Operand == called);

    /// <summary>Fails unless the file written is IL alone, with no ReadyToRun code, and CreateSession returns null at once.</summary>
    private static void Verify(string written, string server)
    {
        using (FileStream stream = File.OpenRead(written))
        using (PEReader pe = new(stream))
        {
            CorHeader cor = pe.PEHeaders.CorHeader ?? throw new PatchException($"{server} was written as no .NET assembly");
            if ((cor.Flags & CorFlags.ILOnly) == 0 || (cor.Flags & CorFlags.ILLibrary) != 0 || cor.ManagedNativeHeaderDirectory.Size != 0)
            {
                throw new PatchException($"{server} was written with its ReadyToRun code, which the runtime would run in place of the patch");
            }
        }
        using ModuleDefinition module = ModuleDefinition.ReadModule(written, new ReaderParameters { InMemory = true });
        if (Create(module, server).Body.Instructions is not [{ OpCode.Code: Code.Ldnull }, { OpCode.Code: Code.Ret }])
        {
            throw new PatchException($"{server} was written with CreateSession doing more than returning null");
        }
    }
}

/// <summary>Every call that makes or starts a Visual Studio Telemetry session across the server's assemblies, reading the
/// default session included, which makes one where none is set.</summary>
public static partial class Starts
{
    private const string Telemetry = "Microsoft.VisualStudio.Telemetry";

    private static readonly HashSet<string> Starting = new(StringComparer.Ordinal)
    {
        "TelemetrySession::.ctor", "TelemetrySession::Create", "TelemetrySession::Start", "TelemetryService::get_DefaultSession",
        "TelemetryService::SetDefaultSession", "TelemetryService::TrySetDefaultSession", "TelemetryService::CreateAndGetDefaultSession",
    };

    /// <summary>The methods that may, by file and by the source's name: any other fails the build.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // Called by CreateSession alone, which the patch cuts off.
        $"Microsoft.CodeAnalysis.LanguageServer.dll: {Patch.TelemetryType}::InitializeSession",
        // A brokered service of Visual Studio's ServiceHub host, which the server never runs.
        "Microsoft.CodeAnalysis.Remote.ServiceHub.dll: Microsoft.CodeAnalysis.Remote.RemoteProcessTelemetryService::InitializeTelemetrySessionAsync",
        // Razor's telemetry reporter, which reads the default session and so makes one that nothing starts.
        "Microsoft.CodeAnalysis.Remote.Razor.dll: Microsoft.CodeAnalysis.Remote.Razor.Telemetry.OutOfProcessTelemetryReporter::.ctor",
    };

    /// <summary>Fails unless every method that makes or starts a session, in the folder's assemblies but those left out, is
    /// one of <see cref="Known"/>.</summary>
    public static void Check(string folder, IReadOnlyCollection<string> leftOut)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".resources", StringComparison.Ordinal) || leftOut.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true });
            }
            catch (BadImageFormatException)
            {
                // A native library, no assembly.
                continue;
            }
            using (module)
            {
                if (module.AssemblyReferences.Any(reference => reference.Name == Telemetry))
                {
                    Check(module, Path.GetRelativePath(folder, path).Replace('\\', '/'));
                }
            }
        }
    }

    private static void Check(ModuleDefinition module, string file)
    {
        foreach (MethodDefinition method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called && called.DeclaringType.Namespace == Telemetry
                    && Starting.Contains($"{called.DeclaringType.Name}::{called.Name}") && !Known.Contains($"{file}: {SourceName(method)}"))
                {
                    throw new PatchException($"{file}: {method.FullName} makes or starts a Visual Studio Telemetry session ({called.FullName}), which Passenger cuts off: find whether the server runs it, and cut it off too");
                }
            }
        }
    }

    /// <summary>A method as its source names it: a lambda's, a local function's or an async or iterator method's code,
    /// which the compiler puts in a method or nested type named after it, as the method it is written in.</summary>
    private static string SourceName(MethodDefinition method)
    {
        List<string> generated = [method.Name];
        TypeDefinition type = method.DeclaringType;
        while (type.Name.StartsWith('<') && type.DeclaringType is { } outer)
        {
            generated.Insert(0, type.Name);
            type = outer;
        }
        string source = generated.Select(name => Written().Match(name)).FirstOrDefault(match => match.Success)?.Groups[1].Value ?? generated[^1];
        return $"{type.FullName}::{source}";
    }

    [GeneratedRegex(@"^<+([^<>]+)>")]
    private static partial Regex Written();
}
