using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>Create from usage in XAML: a binding path its data context's type lacks creates the property there (a
/// command for a <c>Command</c>), in the view model's file as its C# is written; and a converter's
/// <c>{StaticResource}</c> nothing defines creates the converter's class, in a file of its own, and its resource. With
/// the MVVM Toolkit in the project (its <c>[ObservableProperty]</c> attribute known), a property is a field it generates
/// one from and a command a <c>[RelayCommand]</c> method, the class made partial; in a class that notifies its changes,
/// a property with a field that raises them through the class's own helper; else an automatic property.</summary>
public static class CreateFromUsage
{
    private const string Unit = "    ";

    /// <summary>The namespaces the SDK uses in every file of a project with ImplicitUsings on.</summary>
    private static readonly HashSet<string> ImplicitUsings = new(StringComparer.Ordinal)
    {
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    };

    /// <summary>The setter the change-notifying bases of the MVVM libraries give their properties, by base.</summary>
    private static readonly Dictionary<string, string> KnownSetters = new(StringComparer.Ordinal)
    {
        ["CommunityToolkit.Mvvm.ComponentModel.ObservableObject"] = "SetProperty",
        ["Prism.Mvvm.BindableBase"] = "SetProperty",
        ["GalaSoft.MvvmLight.ObservableObject"] = "Set",
        ["ReactiveUI.ReactiveObject"] = "this.RaiseAndSetIfChanged",
    };

    /// <summary>The fix that creates a property a binding names on the type that lacks it; null for a type not written in
    /// the project's C#, or a name that cannot be a member's.</summary>
    public static XamlFix? Property(XamlFile file, ClrType owner, string name, XmlAttribute attribute, bool last)
    {
        if (!owner.IsFromSource || owner.Kind is not (ClrTypeKind.Class or ClrTypeKind.Struct) || !SyntaxFacts.IsValidIdentifier(name)
            || SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SourceFile(file, owner) is not { } path
            || file.Types.ParsingOf(path) is not { } parsing)
        {
            return null;
        }
        Planned member = Plan(file, owner, name, attribute, last);
        if (string.Equals(file.Project.Inputs.PropertyValue("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase))
        {
            member = member with { Usings = [.. member.Usings.Where(u => !ImplicitUsings.Contains(u))] };
        }
        return new XamlFix($"Create property '{name}'", path, text => Edits(text, owner, member, parsing))
        {
            Detail = $"{member.Said} in {Path.GetFileName(path)}",
        };
    }

    /// <summary>The fix that creates a converter a binding's <c>Converter={StaticResource key}</c> names: its class in a
    /// file of its own (the project's Converters folder, else beside the XAML), implementing the dialect's
    /// <c>IValueConverter</c>, and its resource in the file's resources, its namespace declared on the root.</summary>
    public static XamlFix? Converter(XamlFile file, string key)
    {
        string name = Identifier(key);
        if (name.Length == 0)
        {
            return null;
        }
        string folder = Directory.Exists(Path.Combine(file.Project.Folder, "Converters")) ? Path.Combine(file.Project.Folder, "Converters") : Path.GetDirectoryName(file.Path)!;
        string path = Path.Combine(folder, name + ".cs");
        if (File.Exists(path))
        {
            return null;
        }
        string space = NamespaceOf(file, folder);
        bool avalonia = file.Dialect is AvaloniaDialect;
        string uri = file.Dialect.ClrNamespaceUri(space, null);
        string wanted = space.EndsWith(".Converters", StringComparison.Ordinal) || space == "Converters" ? "converters" : "local";
        string made = ConverterText(file, space, name, avalonia);
        return new XamlFix($"Create converter '{key}'", file.Path, text =>
        {
            XmlTree tree = XmlTree.Parse(text);
            List<TextEdit> declared = [];
            string prefix = XamlRefactorings.Prefix(text, tree, uri, wanted, declared);
            string x = XamlRefactorings.Prefix(text, tree, XamlDialect.XamlUri, "x", declared);
            return XamlRefactorings.AddResource(text, $"<{(prefix.Length > 0 ? prefix + ":" : string.Empty)}{name} {x}:Key=\"{key}\" />", declared);
        })
        {
            Detail = $"{name}.cs, IValueConverter",
            Others = [new XamlFileChange(path, null, made)],
        };
    }

    /// <summary>What a new member is written as, where in its type it goes, what it needs (usings, a partial class), and
    /// what the fix's list says of it.</summary>
    private sealed record Planned(string Code, Placement Place, IReadOnlyList<string> Usings, bool Partial, string Said);

    private enum Placement
    {
        Field,
        Property,
        Method,
    }

    private static Planned Plan(XamlFile file, ClrType owner, string name, XmlAttribute attribute, bool last)
    {
        TypeSystem types = file.Types;
        bool nullable = string.Equals(file.Project.Inputs.PropertyValue("Nullable"), "enable", StringComparison.OrdinalIgnoreCase);
        XamlProperty? target = last ? TargetOf(file, attribute) : null;
        bool command = target is { } set && (set.Name == "Command" || types.Resolve(file.Dialect.ValueTypeOf(set.ValueType))?.FullName == "System.Windows.Input.ICommand");
        // The toolkit in the project, and the class one of its: an ObservableObject, marked as one, or with members it generates from.
        bool observable = types.IsA(owner, "CommunityToolkit.Mvvm.ComponentModel.ObservableObject") || owner.Has("INotifyPropertyChanged") || owner.Has("ObservableObject");
        bool uses = observable || types.SelfAndBases(owner).SelectMany(t => t.Members).Any(m => m.Has("ObservableProperty") || m.Has("RelayCommand"));
        bool toolkit = observable && types.Find("CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute") is not null;
        bool relay = uses && types.Find("CommunityToolkit.Mvvm.Input.RelayCommandAttribute") is not null;
        string field = "_" + char.ToLowerInvariant(name[0]) + name[1..];
        if (command && relay && name.EndsWith("Command", StringComparison.Ordinal) && name.Length > "Command".Length)
        {
            return new Planned($"[RelayCommand]\nprivate void {name[..^"Command".Length]}()\n{{\n}}", Placement.Method, ["CommunityToolkit.Mvvm.Input"], true, "[RelayCommand]");
        }
        List<string> usings = [];
        string type = command ? Nullable("ICommand", true, nullable) : target is null ? Nullable("object", true, nullable) : CSharp(types, file.Dialect.ValueTypeOf(target.ValueType), usings, nullable);
        if (command)
        {
            usings.Add("System.Windows.Input");
            return new Planned($"public {type} {name} {{ get; set; }}", Placement.Property, usings, false, "ICommand property");
        }
        if (toolkit)
        {
            usings.Add("CommunityToolkit.Mvvm.ComponentModel");
            // Partial properties where the class has them (toolkit 8.4's, which flags a marked field, and Avalonia's template's).
            List<ClrMember> marked = [.. types.SelfAndBases(owner).SelectMany(t => t.Members).Where(m => m.Has("ObservableProperty"))];
            bool partialProperties = marked.Any(p => p.Kind == MemberKind.Property && !marked.Any(f => f.Kind == MemberKind.Field && f.Place == p.Place));
            return partialProperties
                ? new Planned($"[ObservableProperty]\npublic partial {type} {name} {{ get; set; }}", Placement.Property, usings, true, "[ObservableProperty]")
                : new Planned($"[ObservableProperty]\nprivate {type} {field};", Placement.Field, usings, true, "[ObservableProperty]");
        }
        if (!types.IsA(owner, "System.ComponentModel.INotifyPropertyChanged"))
        {
            return new Planned($"public {type} {name} {{ get; set; }}", Placement.Property, usings, false, "Property");
        }
        // A library's base keeps its helpers protected, which its metadata as read here leaves out: those are known by name.
        IReadOnlyList<ClrType> bases = [.. types.SelfAndBases(owner)];
        IReadOnlyList<ClrMember> members = [.. types.MembersOf(owner).Where(m => m.Kind == MemberKind.Method && !m.IsStatic)];
        string backing = $"private {type} {field};\n\npublic {type} {name}\n{{\n{Unit}get => {field};\n";
        string? setter = bases.Select(b => b.FullName).FirstOrDefault(KnownSetters.ContainsKey) is { } known ? KnownSetters[known]
            : members.FirstOrDefault(m => m.Name is "SetProperty" or "SetField" or "Set" && m.Parameters.Count >= 2)?.Name;
        if (setter is not null)
        {
            if (setter.StartsWith("this.", StringComparison.Ordinal))
            {
                usings.Add("ReactiveUI");
            }
            return new Planned($"{backing}{Unit}set => {setter}(ref {field}, value);\n}}", Placement.Property, usings, false, "Property with change notification");
        }
        string raise;
        if (members.FirstOrDefault(m => m.Name is "OnPropertyChanged" or "RaisePropertyChanged" or "NotifyPropertyChanged" or "NotifyOfPropertyChange"
            && m.Parameters.Count == 1 && m.Parameters[0].Name == "System.String") is { } notify)
        {
            raise = $"{notify.Name}(nameof({name}));";
        }
        else if (owner.Members.Any(m => m.Kind == MemberKind.Event && m.Name == "PropertyChanged"))
        {
            usings.Add("System.ComponentModel");
            raise = $"PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof({name})));";
        }
        else
        {
            return new Planned($"public {type} {name} {{ get; set; }}", Placement.Property, usings, false, "Property");
        }
        string body = $"{Unit}set\n{Unit}{{\n{Unit}{Unit}if (Equals({field}, value))\n{Unit}{Unit}{{\n{Unit}{Unit}{Unit}return;\n{Unit}{Unit}}}\n"
            + $"{Unit}{Unit}{field} = value;\n{Unit}{Unit}{raise}\n{Unit}}}\n}}";
        return new Planned(backing + body, Placement.Property, usings, false, "Property with change notification");
    }

    /// <summary>The property a binding's attribute sets: the attribute's own, or a setter's <c>Property</c>'s.</summary>
    private static XamlProperty? TargetOf(XamlFile file, XmlAttribute attribute)
    {
        if (attribute.Name == "Value" && XamlCompletion.IsSetterOrTrigger(attribute.Owner))
        {
            return XamlCompletion.SetterProperty(file, attribute.Owner);
        }
        return file.MeaningOf(attribute) is { Kind: XamlAttributeKind.Property or XamlAttributeKind.Attached, Property: { } property } ? property : null;
    }

    /// <summary>A type as C# writes it: its keyword, else its name with its namespace used, generic arguments and
    /// <c>Nullable&lt;T&gt;</c> as <c>T?</c>, a reference type nullable where the project's nullable context is on.</summary>
    private static string CSharp(TypeSystem types, TypeRef type, List<string> usings, bool nullable)
    {
        if (type.Name == "System.Nullable`1" && type.Arguments.Count == 1)
        {
            return CSharp(types, type.Arguments[0], usings, nullable: false).TrimEnd('?') + "?";
        }
        if (TypeNames.Keyword(type.Name) is { } keyword)
        {
            return Nullable(type.IsArray ? keyword + "[]" : keyword, keyword is "string" or "object" || type.IsArray, nullable);
        }
        if (types.Resolve(type) is not { } resolved || resolved.IsNested || type.IsGenericParameter)
        {
            return Nullable("object", true, nullable);
        }
        if (resolved.Namespace.Length > 0)
        {
            usings.Add(resolved.Namespace);
        }
        string written = resolved.Name;
        if (type.Arguments.Count > 0)
        {
            written += "<" + string.Join(", ", type.Arguments.Select(a => CSharp(types, a, usings, nullable: false))) + ">";
        }
        bool reference = resolved.Kind is ClrTypeKind.Class or ClrTypeKind.Interface or ClrTypeKind.Delegate || type.IsArray;
        return Nullable(type.IsArray ? written + "[]" : written, reference, nullable);
    }

    private static string Nullable(string type, bool reference, bool nullable) => reference && nullable ? type + "?" : type;

    /// <summary>The file a type is written in that a new member goes in: the one named after it, else its first part's,
    /// never what a build generated.</summary>
    private static string? SourceFile(XamlFile file, ClrType owner)
    {
        List<string> paths = [.. file.Types.PlacesOf(owner.FullName).Select(p => p.Path)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal)];
        if (paths.Count == 0 && owner.Origin.Place is { } place)
        {
            paths.Add(place.Path);
        }
        return paths.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == owner.Name) ?? paths.FirstOrDefault();
    }

    /// <summary>The member put in its type's file as it is now, read as its project compiles it (<paramref name="parsing"/>):
    /// after the type's last field, property or member (a property after the properties, a field after the fields), a
    /// blank line between, indented as the type's members are; the class made partial and the usings added that it needs.</summary>
    private static IReadOnlyList<TextEdit> Edits(string text, ClrType owner, Planned member, CSharpParseOptions parsing)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(text, parsing).GetRoot();
        TypeDeclarationSyntax? type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => FullName(t) == owner.FullName);
        if (type is null || type.OpenBraceToken.IsMissing)
        {
            return [];
        }
        string unit = (Indentation.Detect(text, 4) ?? new IndentOptions()).Unit;
        string typeIndent = XamlRefactorings.LineIndent(text, type.SpanStart);
        string inside = type.Members.Count > 0 ? XamlRefactorings.LineIndent(text, type.Members[0].SpanStart) : typeIndent + unit;
        string code = Indent(member.Code.Replace(Unit, unit, StringComparison.Ordinal), inside);
        List<TextEdit> edits = [];
        MemberDeclarationSyntax? after = member.Place switch
        {
            Placement.Field => type.Members.OfType<FieldDeclarationSyntax>().LastOrDefault(),
            Placement.Property => (MemberDeclarationSyntax?)type.Members.OfType<PropertyDeclarationSyntax>().LastOrDefault()
                ?? type.Members.OfType<FieldDeclarationSyntax>().LastOrDefault() ?? type.Members.LastOrDefault(),
            _ => type.Members.LastOrDefault(),
        };
        if (after is not null)
        {
            edits.Add(new TextEdit(after.Span.End, 0, "\n\n" + inside + code));
        }
        else if (type.Members.Count > 0)
        {
            // A field goes first, before the first member and what its leading lines say of it.
            edits.Add(new TextEdit(type.Members[0].FullSpan.Start, 0, inside + code + "\n\n"));
        }
        else
        {
            edits.Add(new TextEdit(type.OpenBraceToken.Span.End, type.CloseBraceToken.SpanStart - type.OpenBraceToken.Span.End, "\n" + inside + code + "\n" + typeIndent));
        }
        if (member.Partial && !type.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            edits.Add(new TextEdit(type.Keyword.SpanStart, 0, "partial "));
        }
        if (Usings(root, member.Usings, owner.Namespace) is { } usings)
        {
            edits.Add(usings);
        }
        return edits;
    }

    /// <summary>The full name a declared type has in metadata, as <see cref="ClrType.FullName"/> gives it: its namespaces,
    /// and the types it is nested in each before a <c>+</c>, each name with its arity.</summary>
    private static string FullName(BaseTypeDeclarationSyntax type)
    {
        string name = MetadataName(type);
        for (SyntaxNode? at = type.Parent; at is not null; at = at.Parent)
        {
            if (at is BaseTypeDeclarationSyntax outer)
            {
                name = $"{MetadataName(outer)}+{name}";
            }
            else if (at is BaseNamespaceDeclarationSyntax space)
            {
                string written = space.Name.ToString();
                name = $"{(written.StartsWith("global::", StringComparison.Ordinal) ? written["global::".Length..] : written)}.{name}";
            }
        }
        return name;
    }

    private static string MetadataName(BaseTypeDeclarationSyntax type) =>
        type is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 and int arity } ? $"{type.Identifier.Text}`{arity}" : type.Identifier.Text;

    /// <summary>The using directives a file lacks of those a member needs, after its last one (or at its top).</summary>
    private static TextEdit? Usings(SyntaxNode root, IReadOnlyList<string> needed, string own)
    {
        if (root is not CompilationUnitSyntax unit)
        {
            return null;
        }
        HashSet<string> present = [.. unit.Usings.Select(u => u.NamespaceOrType?.ToString() ?? string.Empty)];
        List<string> missing = [.. needed.Distinct(StringComparer.Ordinal).Where(n => !present.Contains(n) && n != own && !own.StartsWith(n + ".", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
        if (missing.Count == 0)
        {
            return null;
        }
        string lines = string.Concat(missing.Select(n => $"using {n};\n"));
        return unit.Usings.Count > 0
            ? new TextEdit(unit.Usings[^1].Span.End, 0, "\n" + lines.TrimEnd('\n'))
            : new TextEdit(unit.SpanStart, 0, lines + "\n");
    }

    /// <summary>A member's lines after its first indented as the type's members are.</summary>
    private static string Indent(string code, string inside) =>
        string.Join('\n', code.Split('\n').Select((line, i) => i == 0 || line.Length == 0 ? line : inside + line));

    /// <summary>A key as a class's name: its letters, digits and underscores, starting as an identifier may.</summary>
    private static string Identifier(string key)
    {
        StringBuilder name = new();
        foreach (char c in key)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                name.Append(c);
            }
        }
        string made = name.ToString();
        return made.Length > 0 && char.IsDigit(made[0]) ? "_" + made : made;
    }

    /// <summary>The namespace a new type in a folder goes in: that of the project's types already there, else the
    /// project's root namespace with the folder's path after it.</summary>
    private static string NamespaceOf(XamlFile file, string folder)
    {
        string? there = file.Types.All.Where(t => t.IsFromSource && !t.IsNested && t.Origin.Place is { } place && Path.GetDirectoryName(place.Path) == folder && t.Namespace.Length > 0)
            .GroupBy(t => t.Namespace).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (there is not null)
        {
            return there;
        }
        string root = file.Project.Inputs.PropertyValue("RootNamespace") is { Length: > 0 } named ? named : file.Project.AssemblyName;
        string relative = Path.GetRelativePath(file.Project.Folder, folder);
        return relative == "." ? root : root + "." + relative.Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.');
    }

    /// <summary>A converter's file: its class implementing the dialect's IValueConverter, each method left to write,
    /// the namespace written as the project's other C# writes it.</summary>
    private static string ConverterText(XamlFile file, string space, string name, bool avalonia)
    {
        bool nullable = string.Equals(file.Project.Inputs.PropertyValue("Nullable"), "enable", StringComparison.OrdinalIgnoreCase);
        bool implicitUsings = string.Equals(file.Project.Inputs.PropertyValue("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase);
        string q = nullable ? "?" : string.Empty;
        string ret = avalonia ? $"object{q}" : "object";
        string value = avalonia ? $"object{q}" : "object";
        bool scoped = FileScoped(file);
        StringBuilder made = new();
        if (!implicitUsings)
        {
            made.Append("using System;\n");
        }
        made.Append("using System.Globalization;\n").Append(avalonia ? "using Avalonia.Data.Converters;\n" : "using System.Windows.Data;\n").Append('\n');
        string indent = scoped ? string.Empty : Unit;
        made.Append(scoped ? $"namespace {space};\n\n" : $"namespace {space}\n{{\n");
        made.Append($"{indent}public class {name} : IValueConverter\n{indent}{{\n");
        foreach (string method in (string[])["Convert", "ConvertBack"])
        {
            made.Append($"{indent}{Unit}public {ret} {method}({value} value, Type targetType, {value} parameter, CultureInfo culture)\n")
                .Append($"{indent}{Unit}{{\n{indent}{Unit}{Unit}throw new NotImplementedException();\n{indent}{Unit}}}\n");
            if (method == "Convert")
            {
                made.Append('\n');
            }
        }
        made.Append($"{indent}}}\n");
        if (!scoped)
        {
            made.Append("}\n");
        }
        return made.ToString();
    }

    /// <summary>Whether the project's C# writes file-scoped namespaces: as the code-behind of the file does, else yes.</summary>
    private static bool FileScoped(XamlFile file)
    {
        string? behind = File.Exists(file.Path + ".cs") ? file.Path + ".cs" : file.Types.All.FirstOrDefault(t => t.IsFromSource && t.Origin.Place is not null)?.Origin.Place?.Path;
        try
        {
            if (behind is not null && File.Exists(behind))
            {
                string text = File.ReadAllText(behind);
                return !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*namespace\s+[\w.]+\s*(\{|$)", System.Text.RegularExpressions.RegexOptions.Multiline)
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*namespace\s+[\w.]+\s*;", System.Text.RegularExpressions.RegexOptions.Multiline);
            }
        }
        catch (IOException)
        {
            // Unread, the newer form is written.
        }
        return true;
    }
}
