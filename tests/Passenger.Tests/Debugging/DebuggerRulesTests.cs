using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

/// <summary>The debugger's code says each of its own rows by one name and takes each failure of the runtime's interfaces
/// one way: no string outside <see cref="DapRows"/> is one of its rows' names, no catch lists the runtime's failures by
/// hand rather than through Failures.OfRuntime, and a frame's locals are read in one place. Its tests say where their
/// processes stood only once they failed.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class DebuggerRulesTests
{
    [Fact]
    public void A_row_the_debugger_adds_is_named_only_in_DapRows()
    {
        HashSet<string> names = [.. typeof(DapRows).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)];
        List<string> found = Written(["src"], "src/Passenger.Dap/DapNames.cs", node => node switch
        {
            LiteralExpressionSyntax plain when plain.IsKind(SyntaxKind.StringLiteralExpression) && names.Contains(plain.Token.ValueText) && !XmlName(plain) && !LanguagesName(plain) => plain.Token.ValueText,
            // A name numbered after its row's ($ReturnValue1 on), made by a hole after the name.
            InterpolatedStringExpressionSyntax { Contents: [InterpolatedStringTextSyntax text, InterpolationSyntax, ..] } when names.Contains(text.TextToken.ValueText) =>
                text.TextToken.ValueText + "{}",
            _ => null,
        });
        Assert.True(found.Count == 0, $"{found.Count} strings write a row's name outside DapRows, so the debugger and the window can come to name one "
            + $"row two ways and the window no longer tells it apart (Set Value offered on Raw View, the exception or a value returned not marked "
            + $"as one): name each through DapRows:\n{string.Join("\n", found)}");

        // A language's name as its entry declares it (Language.Sql's "SQL"), the language's own word, never a row's.
        static bool LanguagesName(LiteralExpressionSyntax literal) => literal.Parent is ArgumentSyntax { NameColon: null, Parent: ArgumentListSyntax list } argument
            && list.Arguments.IndexOf(argument) == 1
            && list.Parent is ImplicitObjectCreationExpressionSyntax { Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: "Language" } } } } }
            && literal.Ancestors().OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "Language");

        // An element's or attribute's name in an XML namespace (ns + "Results"), another format's word, never a row's.
        static bool XmlName(LiteralExpressionSyntax literal) => literal.Parent is BinaryExpressionSyntax join && join.IsKind(SyntaxKind.AddExpression) && join.Right == literal
            && join.Left switch
            {
                IdentifierNameSyntax space => literal.SyntaxTree.GetRoot().DescendantNodes().OfType<VariableDeclarationSyntax>().Any(declared =>
                    declared.Type is IdentifierNameSyntax { Identifier.ValueText: "XNamespace" } && declared.Variables.Any(named => named.Identifier.ValueText == space.Identifier.ValueText)),
                MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "XNamespace" } } => true,
                _ => false,
            };
    }

    [Fact]
    public void A_catch_around_the_runtime_takes_all_its_failures_through_Failures_OfRuntime()
    {
        List<string> found = Written(["src", "Passenger.Debugger"], "src/Passenger.Debugger/Failures.cs", node =>
            node is CatchFilterClauseSyntax filter && !filter.FilterExpression.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name => name.Identifier.ValueText == "HResult")
                && filter.FilterExpression.DescendantNodesAndSelf().Any(test => test is TypeSyntax type && type.ToString().EndsWith("COMException", StringComparison.Ordinal))
                ? filter.ToString()
                : null);
        Assert.True(found.Count == 0, $"{found.Count} catches list the runtime's failures by hand, so the ones they leave out (an E_INVALIDARG's "
            + $"ArgumentException, an E_NOTIMPL's NotImplementedException) escape the read they guard and fail the request around it: take them "
            + $"through Failures.OfRuntime:\n{string.Join("\n", found)}");
    }

    /// <summary>Every read of a frame's locals, all at once or one by its slot, goes through ManagedDebugger's LocalsOf, LocalOf
    /// and TryLocalOf, which read a frame running code an update changed as that code keeps them: read from the IL of each
    /// assembly src builds, which names the type each call's member is on, as the source alone does not.</summary>
    [Fact]
    public void A_frames_locals_are_read_only_through_LocalsOf_LocalOf_and_TryLocalOf()
    {
        string[] through = ["LocalsOf", "LocalOf", "TryLocalOf"];
        List<string> found = [];
        List<string> read = [];
        foreach ((string project, string assembly) in SrcAssemblies.Built().Where(built => built.Project != "Passenger.Debugger.Interop"))
        {
            read.Add(project);
            using PEReader pe = new(File.OpenRead(assembly));
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                foreach (MethodDefinition method in type.GetMethods().Select(metadata.GetMethodDefinition).Where(m => m.RelativeVirtualAddress != 0))
                {
                    string name = metadata.GetString(method.Name);
                    // One of the three, or a lambda or local function written in one.
                    if (through.Any(own => (name == own && metadata.GetString(type.Name) == "ManagedDebugger") || name.StartsWith($"<{own}>", StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                    while (il.RemainingBytes > 0)
                    {
                        int code = il.ReadByte();
                        code = code == 0xFE ? 0xFE00 | il.ReadByte() : code;
                        if (code is not (0x28 or 0x6F or 0xFE06 or 0xFE07))
                        {
                            // Its size first: a switch's is read past the count of its targets.
                            int operand = DroppedTasks.OperandSize(ref il, code);
                            il.Offset += operand;
                        }
                        else if (ReadsALocal(metadata, MetadataTokens.EntityHandle(il.ReadInt32())) is { } reads)
                        {
                            found.Add($"{project}: {metadata.GetString(type.Name)}.{name} calls {reads}");
                        }
                    }
                }
            }
        }
        Assert.Contains("Passenger.Debugger", read);
        Assert.True(found.Count == 0, $"{Plural.Of(found.Count, "method")} {Plural.Agreeing(found.Count, "reads", "read")} a frame's locals from the runtime itself, which lays "
            + $"out a frame its interpreter runs in code an update changed by the build's code, so those locals come back not read or wrong: read them through "
            + $"ManagedDebugger's LocalsOf, LocalOf or TryLocalOf:\n{string.Join("\n", found)}");

        // A member of an IL frame's interface, or of the extensions over it, that reads its locals.
        static string? ReadsALocal(MetadataReader metadata, EntityHandle called)
        {
            (StringHandle member, EntityHandle owner) = called.Kind switch
            {
                HandleKind.MemberReference => (metadata.GetMemberReference((MemberReferenceHandle)called).Name, metadata.GetMemberReference((MemberReferenceHandle)called).Parent),
                HandleKind.MethodDefinition => (metadata.GetMethodDefinition((MethodDefinitionHandle)called).Name, metadata.GetMethodDefinition((MethodDefinitionHandle)called).GetDeclaringType()),
                HandleKind.MethodSpecification => (default, metadata.GetMethodSpecification((MethodSpecificationHandle)called).Method),
                _ => (default, default),
            };
            if (called.Kind is HandleKind.MethodSpecification)
            {
                return ReadsALocal(metadata, owner);
            }
            string type = owner.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)owner).Name),
                HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)owner).Name),
                _ => string.Empty,
            };
            string name = member.IsNil ? string.Empty : metadata.GetString(member);
            return type.Contains("ILFrame", StringComparison.Ordinal) && name.Contains("LocalVariable", StringComparison.Ordinal) ? $"{type}.{name}" : null;
        }
    }

    /// <summary>A failed debugging test says where the adapter and the program stood: on macOS it samples each for a second
    /// (NativeStacks), or attaches to it (ManagedStacks), so a test makes those words only once it has failed, never as
    /// the message of an Assert that may hold, which is made whether it holds or not.</summary>
    [Fact]
    public void A_debugging_tests_account_of_where_its_processes_stood_is_made_only_once_it_has_failed()
    {
        string[] describing = ["Heard", "Stacks", "ProgramStacks"];
        List<string> found = [
            .. Written(["tests", "Passenger.Tests"], "tests/Passenger.Tests/Rig.cs", node => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "NativeStacks" or "ManagedStacks" }, Name.Identifier.ValueText: "Describe" },
                } call && !OnceFailed(call, describing) ? call.ToString() : null),
            .. Written(["tests", "Passenger.Tests", "Debugging"], "", node => node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax name, ArgumentList.Arguments.Count: 0 } call
                && describing.Contains(name.Identifier.ValueText) && !OnceFailed(call, describing) ? call.ToString() : null),
        ];
        Assert.True(found.Count == 0, $"{Plural.Of(found.Count, "call")} {Plural.Agreeing(found.Count, "describes", "describe")} where a debugging test's processes stood whether it failed or "
            + $"not, which samples each for a second at every pass where NativeStacks can, the test waiting meanwhile: make them only in a throw or an Assert.Fail:\n"
            + string.Join("\n", found));

        // Inside a throw, an Assert.Fail, or a member making those words itself.
        static bool OnceFailed(InvocationExpressionSyntax call, string[] describing) => call.Ancestors().Any(around => around is ThrowStatementSyntax or ThrowExpressionSyntax
            || around is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" }, Name.Identifier.ValueText: "Fail" } }
            || around is MethodDeclarationSyntax method && describing.Contains(method.Identifier.ValueText));
    }

    /// <summary>Each C# node under the folder (from the repository's root) that <paramref name="written"/> names, by file
    /// and line, <paramref name="home"/> and the build's output passed over.</summary>
    private static List<string> Written(string[] folder, string home, Func<SyntaxNode, string?> written)
    {
        string root = Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine([root, .. folder]), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == home || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            foreach (SyntaxNode node in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes())
            {
                if (written(node) is { } said)
                {
                    found.Add($"{relative}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {said}");
                }
            }
        }
        return found;
    }
}
