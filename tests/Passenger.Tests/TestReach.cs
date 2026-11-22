using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Passenger.Tests;

/// <summary>What a test class reaches, read from the built tests' IL: its tests, constructors and disposal, its bases',
/// its class fixtures' and every method of the tests they reach (a lambda's, an async method's body), each call out of
/// the tests, each helper of theirs a rule stops at and each string loaded, with the way to it. The rules that keep a
/// class to some systems' report lanes read it (<see cref="SameOnEverySystemRulesTests"/>,
/// <see cref="AboutMacOSRulesTests"/>).</summary>
internal sealed class TestReach(Assembly assembly) : IDisposable
{
    private readonly Module module = assembly.ManifestModule;
    private readonly PEReader pe = new(File.OpenRead(assembly.Location));

    /// <summary>One thing a class reaches: a helper of the tests a rule stops at, a member of another assembly as
    /// Namespace.Type::Member, or a string loaded, whether by the class's own code (<paramref name="Owned"/>); and the way
    /// from a test to it.</summary>
    public readonly record struct Reached(string? Helper, string? Called, string? Words, bool Owned, string Path);

    public void Dispose() => pe.Dispose();

    /// <summary>The tests of a class, as xunit runs them.</summary>
    public static IEnumerable<MethodInfo> Tests(Type type) => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Xunit.v3.IFactAttribute));

    /// <summary>Whether a class has a test in the report lane (<see cref="InReportLane"/>).</summary>
    public static bool InReport(Type type) => Tests(type).Any(InReportLane);

    /// <summary>Whether a test is in the report lane: not marked [Gate], nor of a class marked so.</summary>
    public static bool InReportLane(MethodInfo test) =>
        test.GetCustomAttribute<GateAttribute>() is null && test.ReflectedType?.GetCustomAttribute<GateAttribute>() is null;

    /// <summary>What a class reaches from those of its tests <paramref name="read"/> takes, each once: <paramref name="stopsAt"/>
    /// names a helper not followed but said, by its outermost type and by Type.Method; <paramref name="passedOver"/> names,
    /// as Type.Method, helpers neither followed nor said.</summary>
    public IEnumerable<Reached> Of(Type type, Func<MethodInfo, bool> read, Func<string, string, string?> stopsAt, IReadOnlySet<string> passedOver)
    {
        MetadataReader metadata = pe.GetMetadataReader();
        Dictionary<MethodDefinitionHandle, string> path = [];
        Queue<MethodDefinitionHandle> next = new();
        foreach (MethodBase root in Roots(type, read))
        {
            MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(root.MetadataToken);
            if (root.Module == module && !passedOver.Contains($"{root.DeclaringType!.Name}.{root.Name}") && path.TryAdd(handle, $"{root.DeclaringType!.Name}.{root.Name}"))
            {
                next.Enqueue(handle);
            }
        }
        HashSet<Type> own = [.. Owners(type)];
        while (next.TryDequeue(out MethodDefinitionHandle method))
        {
            MethodDefinition definition = metadata.GetMethodDefinition(method);
            string outer = Outermost(metadata, definition.GetDeclaringType());
            string named = Named(metadata, method);
            if (stopsAt(outer, named) is { } helper)
            {
                yield return new(helper, null, null, false, path[method]);
                continue;
            }
            bool owned = own.Any(owner => owner.Name == outer && owner.Namespace == Namespace(metadata, definition.GetDeclaringType()));
            foreach ((EntityHandle called, string? words) in Instructions(metadata, method))
            {
                if (words is not null)
                {
                    yield return new(null, null, words, owned, path[method]);
                    continue;
                }
                foreach (MethodDefinitionHandle inside in Defined(metadata, called).Where(inside => !passedOver.Contains(Named(metadata, inside))))
                {
                    if (path.TryAdd(inside, $"{path[method]} → {Named(metadata, inside)}"))
                    {
                        next.Enqueue(inside);
                    }
                }
                if (External(metadata, called) is { } name)
                {
                    yield return new(null, name, null, owned, path[method]);
                }
            }
        }
    }

    /// <summary>Where a class's tests begin: each test, and the constructors and disposal of the class, its bases in the
    /// tests and its class fixtures.</summary>
    private IEnumerable<MethodBase> Roots(Type type, Func<MethodInfo, bool> read)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        List<MethodBase> roots = [.. Tests(type).Where(read)];
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
