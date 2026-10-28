using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Xaml.Metadata;

/// <summary>The types one C# file of a project declares, read from its syntax alone (Roslyn's parser, no compilation):
/// what XAML in the same project names before any build, its view models' properties, its own controls' Avalonia or
/// WPF properties, a code-behind's handlers, each with the place it is declared. Names in signatures stay as written,
/// with the scope they are read in, for the type system to resolve.</summary>
public sealed class SourceTypes
{
    private SourceTypes(string path, IReadOnlyList<ClrType> types, IReadOnlyList<string> globalUsings, IReadOnlyList<ClrAttribute> attributes, CSharpParseOptions? parsing)
    {
        Path = path;
        Types = types;
        GlobalUsings = globalUsings;
        Attributes = attributes;
        Parsing = parsing;
    }

    public string Path { get; }

    /// <summary>What the file was parsed with: its project's preprocessor symbols and C# version. Null for types declared
    /// other than in C#.</summary>
    public CSharpParseOptions? Parsing { get; }

    /// <summary>Every type the file declares, nested ones included, each part of a partial type on its own.</summary>
    public IReadOnlyList<ClrType> Types { get; }

    /// <summary>The <c>global using</c>s it declares, which hold in every file of the project.</summary>
    public IReadOnlyList<string> GlobalUsings { get; }

    /// <summary>Its assembly attributes a dialect reads (<c>[assembly: XmlnsDefinition(...)]</c>).</summary>
    public IReadOnlyList<ClrAttribute> Attributes { get; }

    /// <summary>Types a file declares other than in C#: the class a XAML file's <c>x:Class</c> names, which its XAML compiler
    /// makes when no C# does.</summary>
    public static SourceTypes Declaring(string path, IReadOnlyList<ClrType> types) => new(path, types, [], [], null);

    /// <summary>A C# file's, parsed with what its project compiles it with (<paramref name="parsing"/>), its documentation
    /// comments passed over.</summary>
    public static SourceTypes Read(string path, string text, string assemblyName, CSharpParseOptions parsing)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, parsing.WithDocumentationMode(DocumentationMode.None), path);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Reader reader = new(path, tree, assemblyName);
        reader.Unit(root);
        return new SourceTypes(path, reader.Types, reader.GlobalUsings, reader.AssemblyAttributes, parsing);
    }

    private sealed class Reader(string path, SyntaxTree tree, string assemblyName)
    {
        public List<ClrType> Types { get; } = [];

        public List<string> GlobalUsings { get; } = [];

        public List<ClrAttribute> AssemblyAttributes { get; } = [];

        public void Unit(CompilationUnitSyntax root)
        {
            List<string> usings = [];
            Dictionary<string, string> aliases = new(StringComparer.Ordinal);
            Usings(root.Usings, usings, aliases);
            foreach (AttributeListSyntax list in root.AttributeLists)
            {
                if (list.Target?.Identifier.Text == "assembly")
                {
                    AssemblyAttributes.AddRange(list.Attributes.Select(Attribute));
                }
            }
            string? fileNamespace = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            Members(root.Members, fileNamespace, usings, aliases, []);
        }

        private void Usings(SyntaxList<UsingDirectiveSyntax> directives, List<string> usings, Dictionary<string, string> aliases)
        {
            foreach (UsingDirectiveSyntax directive in directives)
            {
                if (directive.NamespaceOrType is not { } named || directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                {
                    continue;
                }
                string name = Strip(named.ToString());
                if (directive.Alias is { } alias)
                {
                    aliases[alias.Name.Identifier.Text] = name;
                }
                else if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                {
                    GlobalUsings.Add(name);
                }
                else
                {
                    usings.Add(name);
                }
            }
        }

        private void Members(SyntaxList<MemberDeclarationSyntax> members, string? @namespace, List<string> usings, Dictionary<string, string> aliases, List<string> enclosing)
        {
            foreach (MemberDeclarationSyntax member in members)
            {
                switch (member)
                {
                    case FileScopedNamespaceDeclarationSyntax file:
                        Members(file.Members, file.Name.ToString(), usings, aliases, enclosing);
                        break;
                    case NamespaceDeclarationSyntax block:
                        List<string> innerUsings = [.. usings];
                        Dictionary<string, string> innerAliases = new(aliases, StringComparer.Ordinal);
                        Usings(block.Usings, innerUsings, innerAliases);
                        string name = Strip(block.Name.ToString());
                        Members(block.Members, @namespace is null ? name : $"{@namespace}.{name}", innerUsings, innerAliases, enclosing);
                        break;
                    case BaseTypeDeclarationSyntax type:
                        Type(type, @namespace ?? string.Empty, new SourceScope(@namespace, usings, aliases, enclosing), enclosing);
                        break;
                    case DelegateDeclarationSyntax @delegate:
                        string delegateName = Arity(@delegate.Identifier.Text, @delegate.TypeParameterList?.Parameters.Count ?? 0);
                        Types.Add(new ClrType(@namespace ?? string.Empty, delegateName, ClrTypeKind.Delegate, Origin(@delegate.Identifier), _ => ClrTypeDetails.None,
                            enclosing.Count > 0 ? enclosing[^1] : null));
                        break;
                }
            }
        }

        private void Type(BaseTypeDeclarationSyntax declaration, string @namespace, SourceScope scope, List<string> enclosing)
        {
            int arity = declaration is TypeDeclarationSyntax { TypeParameterList: { } parameters } ? parameters.Parameters.Count : 0;
            string name = Arity(declaration.Identifier.Text, arity);
            ClrTypeKind kind = declaration switch
            {
                InterfaceDeclarationSyntax => ClrTypeKind.Interface,
                EnumDeclarationSyntax => ClrTypeKind.Enum,
                StructDeclarationSyntax => ClrTypeKind.Struct,
                RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => ClrTypeKind.Struct,
                _ => ClrTypeKind.Class,
            };
            string? declaringName = enclosing.Count > 0 ? enclosing[^1] : null;
            ClrType type = new(@namespace, name, kind, Origin(declaration.Identifier), t => Details(t, declaration, scope), declaringName);
            // Read now, so the type holds what it declares and not the file's whole syntax tree.
            _ = type.Members;
            Types.Add(type);
            if (declaration is TypeDeclarationSyntax withMembers)
            {
                List<string> inner = [.. enclosing, type.FullName];
                SourceScope innerScope = scope with { Enclosing = inner };
                foreach (BaseTypeDeclarationSyntax nested in withMembers.Members.OfType<BaseTypeDeclarationSyntax>())
                {
                    Type(nested, @namespace, innerScope, inner);
                }
            }
        }

        private ClrTypeDetails Details(ClrType type, BaseTypeDeclarationSyntax declaration, SourceScope scope)
        {
            SourceScope inside = scope with { Enclosing = [.. scope.Enclosing, type.FullName] };
            List<TypeRef> bases = declaration.BaseList?.Types.Select(b => Reference(b.Type, inside)).ToList() ?? [];
            List<ClrAttribute> attributes = [.. declaration.AttributeLists.SelectMany(l => l.Attributes).Select(Attribute)];
            List<ClrMember> members = [];
            bool anyConstructor = false;
            bool defaultConstructor = false;
            if (declaration is EnumDeclarationSyntax @enum)
            {
                TypeRef self = new(type.FullName, []);
                foreach (EnumMemberDeclarationSyntax member in @enum.Members)
                {
                    members.Add(new ClrMember(type, member.Identifier.Text, MemberKind.Field, self, true, true, false, place: Place(member.Identifier)));
                }
                return new ClrTypeDetails(TypeRef.Named("System.Enum"), [], attributes, members, false, false, IsVisible(declaration.Modifiers, type.IsNested), false, []);
            }
            TypeDeclarationSyntax typed = (TypeDeclarationSyntax)declaration;
            // A record's positional parameters are its properties.
            if (typed is RecordDeclarationSyntax { ParameterList: { } positional })
            {
                anyConstructor = true;
                foreach (ParameterSyntax parameter in positional.Parameters)
                {
                    if (parameter.Type is { } parameterType)
                    {
                        members.Add(new ClrMember(type, parameter.Identifier.Text, MemberKind.Property, Reference(parameterType, inside), false, true, false,
                            place: Place(parameter.Identifier)));
                    }
                }
            }
            bool isInterface = typed is InterfaceDeclarationSyntax;
            foreach (MemberDeclarationSyntax member in typed.Members)
            {
                bool isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword) || member.Modifiers.Any(SyntaxKind.ConstKeyword);
                bool visible = isInterface || IsAccessible(member.Modifiers);
                IReadOnlyList<ClrAttribute> memberAttributes = [.. member.AttributeLists.SelectMany(l => l.Attributes).Select(Attribute)];
                switch (member)
                {
                    case PropertyDeclarationSyntax property:
                        bool writable = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && IsAccessible(a.Modifiers, inherit: true)) == true;
                        members.Add(new ClrMember(type, property.Identifier.Text, MemberKind.Property, Reference(property.Type, inside), isStatic, visible, writable,
                            attributes: memberAttributes, place: Place(property.Identifier), constant: isStatic ? Given(property) : null));
                        break;
                    case FieldDeclarationSyntax field:
                        bool readOnly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword);
                        TypeRef fieldType = Reference(field.Declaration.Type, inside);
                        foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                        {
                            members.Add(new ClrMember(type, variable.Identifier.Text, MemberKind.Field, Registered(fieldType, variable, inside), isStatic, visible, !readOnly,
                                attributes: memberAttributes, place: Place(variable.Identifier)));
                            // The MVVM Toolkit's generator makes a property of a field marked [ObservableProperty]: name from _name.
                            if (!isStatic && memberAttributes.Any(a => a.ShortName == "ObservableProperty") && Generated(variable.Identifier.Text) is { } property)
                            {
                                members.Add(new ClrMember(type, property, MemberKind.Property, fieldType, false, true, true, attributes: memberAttributes, place: Place(variable.Identifier)));
                            }
                        }
                        break;
                    case MethodDeclarationSyntax method:
                        members.Add(new ClrMember(type, method.Identifier.Text, MemberKind.Method, Reference(method.ReturnType, inside), isStatic, visible, false,
                            [.. method.ParameterList.Parameters.Select(p => p.Type is { } t ? Reference(t, inside) : TypeRef.Named("System.Object"))],
                            [.. method.ParameterList.Parameters.Select(p => p.Identifier.Text)], memberAttributes, Place(method.Identifier)));
                        // And a command of a method marked [RelayCommand]: SaveCommand from Save or SaveAsync.
                        if (memberAttributes.Any(a => a.ShortName == "RelayCommand"))
                        {
                            string command = method.Identifier.Text.EndsWith("Async", StringComparison.Ordinal) ? method.Identifier.Text[..^"Async".Length] : method.Identifier.Text;
                            members.Add(new ClrMember(type, command + "Command", MemberKind.Property, TypeRef.Named("CommunityToolkit.Mvvm.Input.IRelayCommand"), false, true, false,
                                attributes: memberAttributes, place: Place(method.Identifier)));
                        }
                        break;
                    case ConstructorDeclarationSyntax constructor when !isStatic:
                        anyConstructor = true;
                        defaultConstructor |= constructor.ParameterList.Parameters.Count == 0 && IsAccessible(constructor.Modifiers);
                        members.Add(new ClrMember(type, ".ctor", MemberKind.Constructor, TypeRef.Named("System.Void"), false, IsAccessible(constructor.Modifiers), false,
                            [.. constructor.ParameterList.Parameters.Select(p => p.Type is { } t ? Reference(t, inside) : TypeRef.Named("System.Object"))],
                            [.. constructor.ParameterList.Parameters.Select(p => p.Identifier.Text)], memberAttributes, Place(constructor.Identifier)));
                        break;
                    case EventFieldDeclarationSyntax eventField:
                        TypeRef handler = Reference(eventField.Declaration.Type, inside);
                        foreach (VariableDeclaratorSyntax variable in eventField.Declaration.Variables)
                        {
                            members.Add(new ClrMember(type, variable.Identifier.Text, MemberKind.Event, handler, isStatic, visible, true, place: Place(variable.Identifier)));
                        }
                        break;
                    case EventDeclarationSyntax @event:
                        members.Add(new ClrMember(type, @event.Identifier.Text, MemberKind.Event, Reference(@event.Type, inside), isStatic, visible, true, place: Place(@event.Identifier)));
                        break;
                }
            }
            bool isStaticType = typed.Modifiers.Any(SyntaxKind.StaticKeyword);
            bool isAbstract = typed.Modifiers.Any(SyntaxKind.AbstractKeyword);
            string[] generics = typed.TypeParameterList is { } list ? [.. list.Parameters.Select(p => p.Identifier.Text)] : [];
            TypeRef? baseType = bases.Count > 0 ? bases[0] : type.Kind == ClrTypeKind.Struct ? TypeRef.Named("System.ValueType") : null;
            return new ClrTypeDetails(baseType, [.. bases.Skip(1)], attributes, members, isAbstract, isStaticType, IsVisible(typed.Modifiers, type.IsNested),
                !anyConstructor || defaultConstructor, generics);
        }

        /// <summary>The string a static property's getter first gives a call or a constructor, as a class a .resx generates
        /// writes its key (<c>ResourceManager.GetString("Welcome", …)</c>, Arcade's <c>GetResourceString("Welcome")</c>) and its
        /// ResourceManager's name (<c>new ResourceManager("App.Properties.Resources", …)</c>); null for none.</summary>
        private static string? Given(PropertyDeclarationSyntax property)
        {
            SyntaxNode? getter = (SyntaxNode?)property.ExpressionBody ?? property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            foreach (SyntaxNode node in getter?.DescendantNodesAndSelf() ?? [])
            {
                ArgumentListSyntax? arguments = node switch
                {
                    InvocationExpressionSyntax call => call.ArgumentList,
                    BaseObjectCreationExpressionSyntax made => made.ArgumentList,
                    _ => null,
                };
                if (arguments?.Arguments is [{ Expression: LiteralExpressionSyntax literal }, ..] && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    return literal.Token.ValueText;
                }
            }
            return null;
        }

        /// <summary>An Avalonia property's field declared by its untyped base (<c>AvaloniaProperty</c>) takes the property
        /// type its registration makes, whose value type a XAML value is read as.</summary>
        private static TypeRef Registered(TypeRef declared, VariableDeclaratorSyntax variable, SourceScope scope)
        {
            if (declared.Arguments.Count > 0 || variable.Initializer?.Value is not InvocationExpressionSyntax call)
            {
                return declared;
            }
            if (call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "AvaloniaProperty" }, Name: GenericNameSyntax generic }
                && generic.TypeArgumentList.Arguments.Count > 0)
            {
                string method = generic.Identifier.Text;
                TypeRef value = Reference(generic.TypeArgumentList.Arguments[^1], scope);
                string? made = method switch
                {
                    "Register" or "RegisterAttached" => method == "RegisterAttached" ? "Avalonia.AttachedProperty`1" : "Avalonia.StyledProperty`1",
                    "RegisterDirect" => "Avalonia.DirectProperty`2",
                    _ => null,
                };
                if (made is not null)
                {
                    return new TypeRef(made, made.EndsWith("`2", StringComparison.Ordinal) ? [Reference(generic.TypeArgumentList.Arguments[0], scope), value] : [value]);
                }
            }
            return declared;
        }

        private ClrAttribute Attribute(AttributeSyntax attribute)
        {
            string name = Strip(attribute.Name.ToString());
            if (!name.EndsWith("Attribute", StringComparison.Ordinal))
            {
                name += "Attribute";
            }
            List<object?> positional = [];
            List<KeyValuePair<string, object?>> named = [];
            foreach (AttributeArgumentSyntax argument in attribute.ArgumentList?.Arguments ?? [])
            {
                object? value = argument.Expression switch
                {
                    LiteralExpressionSyntax literal => literal.Token.Value,
                    TypeOfExpressionSyntax typeOf => Strip(typeOf.Type.ToString()),
                    InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } nameOf
                        => nameOf.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Split('.')[^1],
                    _ => argument.Expression.ToString(),
                };
                if (argument.NameEquals is { } equals)
                {
                    named.Add(new KeyValuePair<string, object?>(equals.Name.Identifier.Text, value));
                }
                else
                {
                    positional.Add(value);
                }
            }
            return new ClrAttribute(name, positional, named);
        }

        private TypeOrigin Origin(SyntaxToken identifier) => new(assemblyName, null, Place(identifier));

        private SourcePlace Place(SyntaxToken identifier)
        {
            Microsoft.CodeAnalysis.Text.LinePosition start = tree.GetLineSpan(identifier.Span).StartLinePosition;
            return new SourcePlace(path, start.Line, start.Character);
        }
    }

    /// <summary>A type written in source as a reference to resolve in its scope.</summary>
    public static TypeRef Reference(TypeSyntax syntax, SourceScope scope)
    {
        switch (syntax)
        {
            case PredefinedTypeSyntax predefined:
                return TypeRef.Named(TypeNames.FromKeyword(predefined.Keyword.Text) ?? "System.Object");
            case NullableTypeSyntax nullable:
                return Reference(nullable.ElementType, scope);
            case ArrayTypeSyntax array:
                return Reference(array.ElementType, scope) with { IsArray = true };
            case GenericNameSyntax generic:
                return new TypeRef(Arity(generic.Identifier.Text, generic.TypeArgumentList.Arguments.Count),
                    [.. generic.TypeArgumentList.Arguments.Select(a => Reference(a, scope))], Scope: scope);
            case QualifiedNameSyntax qualified:
                TypeRef right = Reference(qualified.Right, scope);
                return right with { Name = $"{Strip(qualified.Left.ToString())}.{right.Name}" };
            case AliasQualifiedNameSyntax aliased:
                return Reference(aliased.Name, scope);
            case IdentifierNameSyntax identifier:
                return identifier.Identifier.Text == "var" ? TypeRef.Named("System.Object") : new TypeRef(identifier.Identifier.Text, [], Scope: scope);
            case TupleTypeSyntax tuple:
                return new TypeRef($"System.ValueTuple`{tuple.Elements.Count}", [.. tuple.Elements.Select(e => Reference(e.Type, scope))]);
            default:
                return TypeRef.Named("System.Object");
        }
    }

    private static string Arity(string name, int arity) => arity == 0 ? name : $"{name}`{arity}";

    /// <summary>The property the MVVM Toolkit generates for an [ObservableProperty] field: its name without a leading
    /// <c>_</c> or <c>m_</c>, its first letter upper case; null for a name that leaves nothing.</summary>
    private static string? Generated(string field)
    {
        string bare = field.StartsWith("m_", StringComparison.Ordinal) ? field[2..] : field.TrimStart('_');
        return bare.Length == 0 ? null : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    /// <summary>A name without the <c>global::</c> it may be written with, or the generic arguments of a written one.</summary>
    private static string Strip(string name)
    {
        string bare = name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;
        int angle = bare.IndexOf('<');
        return angle < 0 ? bare : bare[..angle];
    }

    /// <summary>Whether a member is reachable from the project's own XAML: anything but private (a code-behind's private
    /// handlers are, since the generated code is the same class's, which <see cref="ClrMember.IsPublic"/> leaves to the
    /// dialect to decide).</summary>
    private static bool IsAccessible(SyntaxTokenList modifiers, bool inherit = false)
    {
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && !modifiers.Any(SyntaxKind.ProtectedKeyword))
        {
            return false;
        }
        return inherit || modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.InternalKeyword) || modifiers.Any(SyntaxKind.ProtectedKeyword);
    }

    private static bool IsVisible(SyntaxTokenList modifiers, bool nested) =>
        !modifiers.Any(SyntaxKind.PrivateKeyword) || !nested;
}
