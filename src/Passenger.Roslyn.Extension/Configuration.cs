using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>The configuration keys a document's code reads or binds, through .NET's configuration API as Roslyn binds it, never
/// by names alone: a key by string (an indexer, GetValue, GetConnectionString, a section's Value) under the sections its
/// receiver was taken from (GetSection chains, followed through a local assigned once), the sections themselves, and the
/// options classes bound to a section (Configure, AddOptions' Bind and BindConfiguration, ConfigurationBinder's Bind and Get)
/// with their properties as the binder reads them: nested classes, collections and dictionaries (<c>*</c> for an item or a
/// key), <c>[ConfigurationKeyName]</c> honoured. A receiver whose sections cannot be proven (a section passed in) reads nothing.</summary>
internal static class ConfigurationSource
{
    private const string Configuration = "Microsoft.Extensions.Configuration.IConfiguration";
    private const string ConfigurationSection = "Microsoft.Extensions.Configuration.IConfigurationSection";
    private const string Binder = "Microsoft.Extensions.Configuration.ConfigurationBinder";
    private const string Extensions = "Microsoft.Extensions.Configuration.ConfigurationExtensions";
    private const string OptionsServices = "Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions";
    private const string OptionsBuilder = "Microsoft.Extensions.DependencyInjection.OptionsBuilderConfigurationExtensions";
    private const string KeyName = "Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute";

    /// <summary>How deep an options class's properties are followed, past which a class of classes is left unread.</summary>
    private const int Deepest = 8;

    /// <summary>Whether a document can read configuration, by its syntax alone: a call of one of the API's names, or an
    /// indexer given a string; only such a document's semantics are read.</summary>
    public static bool MayRead(SyntaxNode root) =>
        root.DescendantNodes().Any(node => node switch
        {
            InvocationExpressionSyntax call => ApiName(NameOf(call.Expression)),
            ElementAccessExpressionSyntax access => access.ArgumentList.Arguments is [{ Expression: LiteralExpressionSyntax literal }]
                && literal.IsKind(SyntaxKind.StringLiteralExpression),
            _ => false,
        });

    /// <summary>Whether a compilation holds the API a key is read through: configuration itself, or the options builder's
    /// binding by a section's path (its one read that takes no configuration); without either nothing reads a key.</summary>
    private static bool Present(Compilation compilation) =>
        !compilation.GetTypesByMetadataName(Configuration).IsEmpty || !compilation.GetTypesByMetadataName(OptionsBuilder).IsEmpty;

    /// <summary>Whether a call by this name can read configuration: the names of the API's methods <see cref="Read"/> knows.</summary>
    private static bool ApiName(string? name) => name is "GetSection" or "GetRequiredSection" or "GetValue" or "GetConnectionString" or "Configure" or "Bind"
        or "BindConfiguration" or "Get";

    /// <summary>The keys a document reads or binds, each where it is written. Only what can read a key by its syntax is bound:
    /// a call by one of the API's names, an indexer read with what can be a string constant, and, in a document that takes a
    /// section by name, a section's Value.</summary>
    public static IEnumerable<ConfigurationKey> Read(string project, string path, SyntaxNode root, SemanticModel model, SourceText text, CancellationToken cancel)
    {
        if (!Present(model.Compilation))
        {
            yield break;
        }
        bool sections = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => CalledBy(call) is "GetSection" or "GetRequiredSection");
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            cancel.ThrowIfCancellationRequested();
            switch (node)
            {
                case ElementAccessExpressionSyntax access:
                    if (MayIndex(access) && Indexed(access, model, cancel) is { } indexed)
                    {
                        yield return Key(project, path, text, indexed.Key, ConfigurationKinds.Read, ConfigurationTypes.String, "string", Checked(access, model, cancel), indexed.Written);
                    }
                    break;
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value
                    when sections && MaySection(value.Expression)
                    && model.GetSymbolInfo(value, cancel).Symbol is IPropertySymbol { Name: "Value" } property && Is(property.ContainingType, ConfigurationSection)
                    && Section(value.Expression, model, cancel) is { Path.Length: > 0, Written: { } written } section:
                    yield return Key(project, path, text, section.Path, ConfigurationKinds.Read, ConfigurationTypes.String, "string", Checked(value, model, cancel), written);
                    break;
                case InvocationExpressionSyntax call when ApiName(CalledBy(call)) && model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol method:
                    foreach (ConfigurationKey key in Called(project, path, text, call, method, model, cancel))
                    {
                        yield return key;
                    }
                    break;
            }
        }
    }

    private static IEnumerable<ConfigurationKey> Called(string project, string path, SourceText text, InvocationExpressionSyntax call, IMethodSymbol method,
        SemanticModel model, CancellationToken cancel)
    {
        string type = (method.ReducedFrom ?? method).ContainingType.ToDisplayString();
        switch (method.Name)
        {
            case "GetSection" or "GetRequiredSection" when Section(call, model, cancel) is { Path.Length: > 0, Written: { } written } section:
                // GetRequiredSection throws where no source gives the section: a read that wants it given.
                yield return Key(project, path, text, section.Path, ConfigurationKinds.Section, ConfigurationTypes.Object, "IConfigurationSection",
                    method.Name == "GetSection", written);
                break;
            case "GetValue" when type == Binder && Receiver(call, method) is { } receiver && Section(receiver, model, cancel) is { } prefix
                && Argument(call, method, "key") is { } named && Constant(named, model, cancel) is { Length: > 0 } key:
                ITypeSymbol? read = method.TypeArguments.FirstOrDefault()
                    ?? (Argument(call, method, "type") is TypeOfExpressionSyntax typeOf ? model.GetTypeInfo(typeOf.Type, cancel).Type : null);
                (string kind, IReadOnlyList<string>? values) = read is null ? (ConfigurationTypes.String, null) : Classify(read).Shape;
                // A default given, or a value type's own (false, 0), is what a missing key reads as.
                bool defaulted = Argument(call, method, "defaultValue") is not null
                    || read is { IsValueType: true } && read.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;
                yield return Key(project, path, text, Join(prefix.Path, key), ConfigurationKinds.Read, kind, read?.ToDisplayString() ?? "string",
                    defaulted || Checked(call, model, cancel), named, values);
                break;
            case "GetConnectionString" when type == Extensions && Receiver(call, method) is { } receiver && Section(receiver, model, cancel) is { } prefix
                && Argument(call, method, "name") is { } named && Constant(named, model, cancel) is { Length: > 0 } name:
                yield return Key(project, path, text, Join(prefix.Path, "ConnectionStrings:" + name), ConfigurationKinds.Read, ConfigurationTypes.String, "string",
                    Checked(call, model, cancel), named);
                break;
            case "Configure" when type == OptionsServices && method.TypeArguments is [{ } options]
                && Argument(call, method, "config") is { } given && Section(given, model, cancel) is { } section:
                foreach (ConfigurationKey each in Bound(project, path, text, section.Path, options, section.Written ?? given, model.Compilation))
                {
                    yield return each;
                }
                break;
            case "Bind" when type == OptionsBuilder && OptionsOf(call, method, model, cancel) is { } options
                && Argument(call, method, "config") is { } given && Section(given, model, cancel) is { } section:
                foreach (ConfigurationKey each in Bound(project, path, text, section.Path, options, section.Written ?? given, model.Compilation))
                {
                    yield return each;
                }
                break;
            case "BindConfiguration" when type == OptionsBuilder && OptionsOf(call, method, model, cancel) is { } options
                && Argument(call, method, "configSectionPath") is { } named && Constant(named, model, cancel) is { Length: > 0 } sectionPath:
                foreach (ConfigurationKey each in Bound(project, path, text, sectionPath, options, named, model.Compilation))
                {
                    yield return each;
                }
                break;
            case "Bind" when type == Binder && Receiver(call, method) is { } receiver && Section(receiver, model, cancel) is { } prefix
                && Argument(call, method, "instance") is { } instance && model.GetTypeInfo(instance, cancel).Type is { } bound:
                ExpressionSyntax? keyed = Argument(call, method, "key");
                string? under = keyed is null ? prefix.Path : Constant(keyed, model, cancel) is { Length: > 0 } within ? Join(prefix.Path, within) : null;
                if (under is not null)
                {
                    foreach (ConfigurationKey found in Bound(project, path, text, under, bound, keyed ?? prefix.Written ?? receiver, model.Compilation))
                    {
                        yield return found;
                    }
                }
                break;
            case "Get" when type == Binder && Receiver(call, method) is { } receiver && Section(receiver, model, cancel) is { } prefix
                && (method.TypeArguments.FirstOrDefault()
                    ?? (Argument(call, method, "type") is TypeOfExpressionSyntax gotType ? model.GetTypeInfo(gotType.Type, cancel).Type : null)) is { } got:
                foreach (ConfigurationKey each in Bound(project, path, text, prefix.Path, got, prefix.Written ?? receiver, model.Compilation))
                {
                    yield return each;
                }
                break;
        }
    }

    /// <summary>An options class bound to a section: the binding, where the section is named, then each property the binder
    /// sets, nested ones under theirs.</summary>
    private static IEnumerable<ConfigurationKey> Bound(string project, string path, SourceText text, string section, ITypeSymbol options, ExpressionSyntax written,
        Compilation compilation)
    {
        string shown = options.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        yield return Key(project, path, text, section, ConfigurationKinds.Binding, ConfigurationTypes.Object, shown, true, written, owner: shown);
        foreach (ConfigurationKey property in Properties(project, section, options, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), compilation))
        {
            yield return property;
        }
    }

    /// <summary>The properties of an options class the binder sets, as keys under a section: its own and its bases', public,
    /// read with a public getter, and set or (a class's or a collection's) bound into.</summary>
    private static IEnumerable<ConfigurationKey> Properties(string project, string section, ITypeSymbol type, HashSet<ITypeSymbol> holding, Compilation compilation)
    {
        if (holding.Count >= Deepest || !holding.Add(type))
        {
            yield break;
        }
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
        for (ITypeSymbol? level = type; level is not null && level.SpecialType != SpecialType.System_Object; level = level.BaseType)
        {
            foreach (IPropertySymbol property in level.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                {
                    continue;
                }
                string name = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == KeyName)?.ConstructorArguments
                    .FirstOrDefault().Value as string ?? property.Name;
                Shaped shaped = Classify(property.Type);
                bool settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public };
                if (!named.Add(name) || !settable && shaped.Shape.Type is not (ConfigurationTypes.Object or ConfigurationTypes.Array or ConfigurationTypes.Dictionary))
                {
                    continue;
                }
                string key = Join(section, name);
                Location? declared = property.Locations.FirstOrDefault(l => l.IsInSource);
                FileLinePositionSpan? span = declared?.GetLineSpan();
                yield return new ConfigurationKey(project, key, ConfigurationKinds.Option, shaped.Shape.Type, property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    HasValue(property, compilation), span is { } at ? at.Path : null,
                    span is { } start ? PlaceOf(start.StartLinePosition) : null,
                    span is { } end ? PlaceOf(end.EndLinePosition) : null,
                    shaped.Shape.Values, property.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + "." + property.Name);
                ITypeSymbol? inner = shaped.Shape.Type switch
                {
                    ConfigurationTypes.Object => property.Type,
                    ConfigurationTypes.Array or ConfigurationTypes.Dictionary => shaped.Element,
                    _ => null,
                };
                if (inner is not null && Classify(inner).Shape.Type == ConfigurationTypes.Object)
                {
                    string under = shaped.Shape.Type == ConfigurationTypes.Object ? key : key + ":*";
                    foreach (ConfigurationKey nested in Properties(project, under, inner, holding, compilation))
                    {
                        yield return nested;
                    }
                }
            }
        }
        holding.Remove(type);
    }

    /// <summary>Whether an options property has a value without the configuration: an initializer, a nullable type, or a value
    /// type's own, unless it is marked required (the C# modifier or DataAnnotations' <c>[Required]</c>).</summary>
    private static bool HasValue(IPropertySymbol property, Compilation compilation)
    {
        if (property.IsRequired || property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.RequiredAttribute"))
        {
            return false;
        }
        return property.Type.IsValueType || property.NullableAnnotation == NullableAnnotation.Annotated
            || property.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null });
    }

    /// <summary>What a type is read as, and an enum's names, with a collection's or a dictionary's item type.</summary>
    private sealed record Shaped((string Type, IReadOnlyList<string>? Values) Shape, ITypeSymbol? Element);

    private static Shaped Classify(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [{ } underlying] })
        {
            type = underlying;
        }
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return new((ConfigurationTypes.Boolean, null), null);
            case SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
                or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal:
                return new((ConfigurationTypes.Number, null), null);
            case SpecialType.System_String or SpecialType.System_Char or SpecialType.System_DateTime or SpecialType.System_Object:
                return new((ConfigurationTypes.String, null), null);
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            return new((ConfigurationTypes.Enum, [.. type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name)]), null);
        }
        if (type is IArrayTypeSymbol array)
        {
            return new((ConfigurationTypes.Array, null), array.ElementType);
        }
        if (type is not INamedTypeSymbol named)
        {
            return new((ConfigurationTypes.String, null), null);
        }
        INamedTypeSymbol[] faces = [named, .. named.AllInterfaces];
        if (faces.FirstOrDefault(f => f.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>") is { TypeArguments: [_, { } value] })
        {
            return new((ConfigurationTypes.Dictionary, null), value);
        }
        if (faces.FirstOrDefault(f => f.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) is { TypeArguments: [{ } item] })
        {
            return new((ConfigurationTypes.Array, null), item);
        }
        // A type the binder converts from a string (TimeSpan, Guid, Uri, Version and the like) is read as one.
        bool converted = named.ContainingNamespace?.ToDisplayString() is { } space && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal))
            || named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.TypeConverterAttribute");
        return converted || named.TypeKind is not (TypeKind.Class or TypeKind.Struct)
            ? new((ConfigurationTypes.String, null), null)
            : new((ConfigurationTypes.Object, null), null);
    }

    /// <summary>Whether an indexer can read a key, by its syntax: read, not written to, with one argument that can be a string
    /// constant (a string, a name, an operator's or a cast's result, <c>nameof</c>), where a number, a call or an index from
    /// the end never is.</summary>
    private static bool MayIndex(ElementAccessExpressionSyntax access) =>
        access.ArgumentList.Arguments is [{ Expression: { } argument }] && !(access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
        && argument switch
        {
            LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression),
            InvocationExpressionSyntax call => NameOf(call.Expression) == "nameof",
            PostfixUnaryExpressionSyntax suppressed => suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression),
            InterpolatedStringExpressionSyntax or NameSyntax or MemberAccessExpressionSyntax or BinaryExpressionSyntax or ConditionalExpressionSyntax
                or CastExpressionSyntax or ParenthesizedExpressionSyntax or CheckedExpressionSyntax => true,
            _ => false,
        };

    /// <summary>Whether a section's Value can be read of an expression, by its syntax: a section taken by name, or a local
    /// that may hold one (<see cref="Section"/> follows no other, and a local only to its initializer in the same document).</summary>
    private static bool MaySection(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
        {
            expression = expression is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : ((PostfixUnaryExpressionSyntax)expression).Operand;
        }
        return expression is IdentifierNameSyntax || expression is InvocationExpressionSyntax call && CalledBy(call) is "GetSection" or "GetRequiredSection";
    }

    /// <summary>The key a configuration indexer reads (<c>Configuration["A:B"]</c>), under the sections its receiver was taken
    /// from; none for one written to, or of anything but configuration.</summary>
    private static (string Key, ExpressionSyntax Written)? Indexed(ElementAccessExpressionSyntax access, SemanticModel model, CancellationToken cancel)
    {
        if (access.ArgumentList.Arguments is not [{ Expression: { } argument }]
            || access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access
            || model.GetSymbolInfo(access, cancel).Symbol is not IPropertySymbol { IsIndexer: true } indexer || !Is(indexer.ContainingType, Configuration)
            || Constant(argument, model, cancel) is not { Length: > 0 } key || Section(access.Expression, model, cancel) is not { } prefix)
        {
            return null;
        }
        return (Join(prefix.Path, key), argument);
    }

    /// <summary>The section an expression of configuration is: its path from the root (empty for the root itself), with the
    /// string naming its last section; none where the path cannot be proven (a section passed in, a key not constant).</summary>
    private static (string Path, ExpressionSyntax? Written)? Section(ExpressionSyntax expression, SemanticModel model, CancellationToken cancel, int depth = 0)
    {
        while (expression is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
        {
            expression = expression is ParenthesizedExpressionSyntax parenthesized ? parenthesized.Expression : ((PostfixUnaryExpressionSyntax)expression).Operand;
        }
        if (depth > Deepest || model.GetTypeInfo(expression, cancel).Type is not { } type || !Is(type, Configuration))
        {
            return null;
        }
        if (expression is InvocationExpressionSyntax call && model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol { Name: "GetSection" or "GetRequiredSection" } method)
        {
            return Receiver(call, method) is { } receiver && Section(receiver, model, cancel, depth + 1) is { } outer
                && Argument(call, method, "key") is { } named && Constant(named, model, cancel) is { Length: > 0 } key
                ? (Join(outer.Path, key), named) : null;
        }
        if (expression is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier, cancel).Symbol is ILocalSymbol local)
        {
            return local.DeclaringSyntaxReferences is [{ } reference] && reference.GetSyntax(cancel) is VariableDeclaratorSyntax { Initializer.Value: { } value }
                && !Reassigned(local, reference.GetSyntax(cancel), model, cancel)
                ? Section(value, model, cancel, depth + 1) : null;
        }
        // The root, as the host or a constructor is given it; a section passed in names no path that can be proven.
        return Is(type, ConfigurationSection) ? null : (string.Empty, null);
    }

    /// <summary>Whether a local is assigned again after its declaration, so its initializer is not all it holds.</summary>
    private static bool Reassigned(ILocalSymbol local, SyntaxNode declaration, SemanticModel model, CancellationToken cancel) =>
        (declaration.Ancestors().FirstOrDefault(a => a is BlockSyntax or CompilationUnitSyntax) ?? declaration).DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax name && name.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, cancel).Symbol, local));

    /// <summary>Whether a read's result is checked for null where it is read, or in the local it is kept in: <c>??</c>,
    /// <c>?.</c>, a comparison with null, a pattern, string.IsNullOrEmpty and the ThrowIfNull guards.</summary>
    private static bool Checked(ExpressionSyntax read, SemanticModel model, CancellationToken cancel, bool followLocal = true)
    {
        SyntaxNode node = read;
        while (node.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
        {
            node = node.Parent;
        }
        switch (node.Parent)
        {
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression) && coalesce.Left == node:
            case ConditionalAccessExpressionSyntax access when access.Expression == node:
            case IsPatternExpressionSyntax pattern when pattern.Expression == node:
                return true;
            case BinaryExpressionSyntax compared when compared.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression:
                return (compared.Left == node ? compared.Right : compared.Left) is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression };
            case ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }
                when model.GetSymbolInfo(call, cancel).Symbol is IMethodSymbol { Name: "IsNullOrEmpty" or "IsNullOrWhiteSpace" or "ThrowIfNull" or "ThrowIfNullOrEmpty" or "ThrowIfNullOrWhiteSpace" }:
                return true;
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } when followLocal
                && model.GetDeclaredSymbol(declarator, cancel) is ILocalSymbol local:
                SyntaxNode scope = declarator.Ancestors().FirstOrDefault(a => a is BlockSyntax or CompilationUnitSyntax) ?? declarator;
                return scope.DescendantNodes().OfType<IdentifierNameSyntax>().Any(used => used.Identifier.ValueText == local.Name
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(used, cancel).Symbol, local) && Checked(used, model, cancel, followLocal: false));
        }
        return false;
    }

    /// <summary>The OptionsBuilder&lt;T&gt;'s options class a Bind or BindConfiguration is called on.</summary>
    private static ITypeSymbol? OptionsOf(InvocationExpressionSyntax call, IMethodSymbol method, SemanticModel model, CancellationToken cancel) =>
        method.TypeArguments.FirstOrDefault()
        ?? (Receiver(call, method) is { } receiver && model.GetTypeInfo(receiver, cancel).Type is INamedTypeSymbol { TypeArguments: [{ } options] } ? options : null);

    private static string Join(string prefix, string key) => prefix.Length == 0 ? key : prefix + ":" + key;

    private static bool Is(ITypeSymbol type, string face) =>
        type.ToDisplayString() == face || type.AllInterfaces.Any(i => i.ToDisplayString() == face);

    /// <summary>A key where a string names it: its characters within a plainly written string, else the whole expression.</summary>
    private static ConfigurationKey Key(string project, string path, SourceText text, string key, string kind, string type, string typeName, bool guarded,
        ExpressionSyntax written, IReadOnlyList<string>? values = null, string? owner = null)
    {
        TextSpan span = written is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && literal.Token.Text == "\"" + literal.Token.ValueText + "\""
            ? new TextSpan(literal.SpanStart + 1, literal.Token.ValueText.Length) : written.Span;
        return new ConfigurationKey(project, key, kind, type, typeName, guarded, path, PlaceAt(text, span.Start), PlaceAt(text, span.End), values, owner);
    }
}
