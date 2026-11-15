using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Passenger.Tests;

/// <summary>Which collection a test class runs in, held to what its tests do, read from the built tests.</summary>
[Collection(Beside.Name)]
public sealed class CollectionRulesTests
{
    /// <summary>A test that counts what every thread allocates (GC.GetTotalAllocatedBytes) runs in the Alone collection,
    /// whether it counts in its own code or in what it calls: a helper of its class or another, a base's, a lambda's or an
    /// async method's body; its class's constructors and disposal count as its own. Read from the built IL, which knows
    /// what each call reaches, as the source alone does not.</summary>
    [Fact]
    public void Every_test_counting_what_every_thread_allocates_runs_alone()
    {
        Assembly tests = typeof(CollectionRulesTests).Assembly;
        CountingCalls counting = new(tests);
        List<string> beside = [];
        List<string> alone = [];
        foreach (Type type in tests.GetTypes().Where(type => type.IsClass && !type.IsAbstract).OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            MethodInfo[] cases = [.. type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Xunit.v3.IFactAttribute))];
            if (cases.Length == 0)
            {
                continue;
            }
            bool runsAlone = type.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>().Any(collection => collection.Name == Alone.Name);
            List<MethodBase> around = [];
            for (Type? own = type; own is not null && own.Assembly == tests; own = own.BaseType)
            {
                const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                around.AddRange(own.GetConstructors(Declared));
                around.AddRange(own.GetMethods(Declared).Where(method => method.Name is "Dispose" or "DisposeAsync" or "InitializeAsync"));
            }
            foreach (MethodBase method in cases.Concat(around))
            {
                if (counting.CountsIn(method) is not { } where)
                {
                    continue;
                }
                string test = $"{type.Name}.{method.Name}";
                (runsAlone ? alone : beside).Add(where == $"{method.DeclaringType!.Name}.{method.Name}" ? test : $"{test}, counting in {where}");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Counting what every thread allocates, alone:\n{string.Join('\n', alone)}");
        Assert.True(alone.Count > 0, "No test of the Alone collection was found counting what every thread allocates, so the built tests' calls were not read");
        Assert.True(beside.Count == 0, "A test counting what every thread allocates (GC.GetTotalAllocatedBytes) outside the Alone collection counts what the "
            + "classes beside it allocate as its own, failing or passing for their work: move it into a class of its own marked [Collection(Alone.Name)]:\n"
            + string.Join('\n', beside));
    }

    /// <summary>The methods of a built assembly that call GC.GetTotalAllocatedBytes, or reach a method that does through a
    /// call, a delegate made of one (a lambda's) or an async or iterator method's state machine.</summary>
    private sealed class CountingCalls
    {
        private readonly Module module;

        /// <summary>For each counting method, the next method on its way to the call: itself where it calls.</summary>
        private readonly Dictionary<MethodDefinitionHandle, MethodDefinitionHandle> toward = [];

        /// <summary>Each counting method as its code names it (Type.Method), null for code only the compiler names.</summary>
        private readonly Dictionary<MethodDefinitionHandle, string?> named;

        public CountingCalls(Assembly assembly)
        {
            module = assembly.ManifestModule;
            using PEReader pe = new(File.OpenRead(assembly.Location));
            MetadataReader metadata = pe.GetMetadataReader();
            Dictionary<MethodDefinitionHandle, List<MethodDefinitionHandle>> callers = [];
            Queue<MethodDefinitionHandle> reached = new();
            foreach (MethodDefinitionHandle caller in metadata.MethodDefinitions)
            {
                foreach (MethodDefinitionHandle called in Calls(pe, metadata, caller))
                {
                    if (called.IsNil)
                    {
                        if (toward.TryAdd(caller, caller))
                        {
                            reached.Enqueue(caller);
                        }
                    }
                    else if (callers.TryGetValue(called, out List<MethodDefinitionHandle>? those))
                    {
                        those.Add(caller);
                    }
                    else
                    {
                        callers[called] = [caller];
                    }
                }
            }
            while (reached.TryDequeue(out MethodDefinitionHandle next))
            {
                foreach (MethodDefinitionHandle caller in callers.GetValueOrDefault(next) ?? [])
                {
                    if (toward.TryAdd(caller, next))
                    {
                        reached.Enqueue(caller);
                    }
                }
            }
            named = toward.Keys.ToDictionary(handle => handle, handle => Named(metadata, handle));
        }

        /// <summary>Where <paramref name="method"/> counts what every thread allocates: the last method code names on its way
        /// to the call, itself where it calls; null where it never counts.</summary>
        public string? CountsIn(MethodBase method)
        {
            MethodDefinitionHandle at = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
            if (method.Module != module || !toward.ContainsKey(at))
            {
                return null;
            }
            string last = $"{method.DeclaringType!.Name}.{method.Name}";
            while (true)
            {
                last = named[at] ?? last;
                if (toward[at] == at)
                {
                    return last;
                }
                at = toward[at];
            }
        }

        /// <summary>The methods of the assembly <paramref name="method"/> calls, makes a delegate of or runs as its state
        /// machine; a nil handle for a call of GC.GetTotalAllocatedBytes.</summary>
        private IEnumerable<MethodDefinitionHandle> Calls(PEReader pe, MetadataReader metadata, MethodDefinitionHandle method)
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
                    yield return MetadataTokens.MethodDefinitionHandle(moveNext.MetadataToken);
                }
            }
            BlobReader il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                int code = il.ReadByte();
                code = code == 0xFE ? 0xFE00 | il.ReadByte() : code;
                // call, callvirt, newobj, jmp, ldftn and ldvirtftn.
                if (code is 0x28 or 0x6F or 0x73 or 0x27 or 0xFE06 or 0xFE07)
                {
                    foreach (MethodDefinitionHandle called in Defined(metadata, MetadataTokens.EntityHandle(il.ReadInt32())))
                    {
                        yield return called;
                    }
                }
                else
                {
                    int operand = DroppedTasks.OperandSize(ref il, code);
                    il.Offset += operand;
                }
            }
        }

        /// <summary>The methods of the assembly a called method is: its definition, or every method of its name in a generic
        /// type of the assembly; a nil handle for GC.GetTotalAllocatedBytes, none for another assembly's method.</summary>
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
            if (parent.Kind == HandleKind.MethodDefinition)
            {
                return [(MethodDefinitionHandle)parent];
            }
            if (parent.Kind == HandleKind.TypeReference)
            {
                TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)parent);
                return metadata.StringComparer.Equals(type.Namespace, "System") && metadata.StringComparer.Equals(type.Name, nameof(GC))
                    && metadata.StringComparer.Equals(member.Name, nameof(GC.GetTotalAllocatedBytes)) ? [default] : [];
            }
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                {
                    return [];
                }
                signature.ReadSignatureTypeCode();
                parent = signature.ReadTypeHandle();
            }
            string name = metadata.GetString(member.Name);
            return parent.Kind == HandleKind.TypeDefinition
                ? [.. metadata.GetTypeDefinition((TypeDefinitionHandle)parent).GetMethods().Where(method => metadata.StringComparer.Equals(metadata.GetMethodDefinition(method).Name, name))]
                : [];
        }

        /// <summary>The name of an attribute's type, by its constructor.</summary>
        private static string? AttributeType(MetadataReader metadata, EntityHandle constructor) => constructor.Kind switch
        {
            HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent =>
                metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.MethodDefinition => metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()).Name),
            _ => null,
        };

        /// <summary>Type.Method for a method its code names; null for one only the compiler names (a lambda, a local
        /// function, a state machine's step), whose name or type's name begins with '&lt;'.</summary>
        private static string? Named(MetadataReader metadata, MethodDefinitionHandle handle)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);
            string type = metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);
            return name.StartsWith('<') || type.StartsWith('<') ? null : $"{type}.{name}";
        }
    }
}
