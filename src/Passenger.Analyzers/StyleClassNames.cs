using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Analyzers;

/// <summary>Style classes as constants, so code names a class as it names any identifier, its home the XAML whose styles
/// take it: each additional file with ClassesClass metadata gives that public class in namespace Passenger a const for
/// every class a style's selector in it names, named by the class with its first letter a capital and each '-' an
/// underscore ("emptyText" is StyleClasses.EmptyText), marked [IdentifierHome].</summary>
[Generator(LanguageNames.CSharp)]
public sealed class StyleClassNames : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassesFile> files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, cancel) => pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.ClassesClass", out string? name)
                && name.Length > 0 ? Read(pair.Left, name, cancel) : null)
            .Where(file => file is not null)!;
        context.RegisterSourceOutput(files.Collect(), (output, all) =>
        {
            foreach (IGrouping<string, ClassesFile> group in all.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Write(output, group.Key, group.ToList());
            }
        });
    }

    private static ClassesFile Read(AdditionalText file, string name, CancellationToken cancel)
    {
        List<string> classes = [];
        string? problem = null;
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(file.GetText(cancel)?.ToString() ?? string.Empty));
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Style" && reader.GetAttribute("Selector") is { } selector)
                {
                    classes.AddRange(ClassesOf(selector));
                }
            }
        }
        catch (XmlException e)
        {
            problem = e.Message;
        }
        return new ClassesFile(name, Path.GetFileName(file.Path), classes, problem);
    }

    /// <summary>The classes a selector names: each '.' outside a property's brackets begins one.</summary>
    private static IEnumerable<string> ClassesOf(string selector)
    {
        int depth = 0;
        for (int i = 0; i < selector.Length; i++)
        {
            char c = selector[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == '.' && depth == 0)
            {
                int end = i + 1;
                while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '_' or '-'))
                {
                    end++;
                }
                if (end > i + 1)
                {
                    yield return selector.Substring(i + 1, end - i - 1);
                }
                i = end - 1;
            }
        }
    }

    private static void Write(SourceProductionContext output, string name, IReadOnlyList<ClassesFile> files)
    {
        SortedDictionary<string, string> members = new(StringComparer.Ordinal);
        foreach (ClassesFile file in files)
        {
            if (file.Problem is not null)
            {
                output.ReportDiagnostic(Diagnostic.Create(Rules.ClassesUnread, Location.None, file.Name, name, file.Problem));
                continue;
            }
            foreach (string style in file.Classes)
            {
                string member = char.ToUpperInvariant(style[0]) + style.Substring(1).Replace('-', '_');
                if (members.TryGetValue(member, out string? other))
                {
                    if (other != style)
                    {
                        output.ReportDiagnostic(Diagnostic.Create(Rules.ClassesUnread, Location.None, file.Name, name,
                            $"\"{style}\" and \"{other}\" would both be {member}"));
                    }
                    continue;
                }
                if (!SyntaxFacts.IsValidIdentifier(member))
                {
                    output.ReportDiagnostic(Diagnostic.Create(Rules.ClassesUnread, Location.None, file.Name, name,
                        $"\"{style}\" makes no C# name"));
                    continue;
                }
                members[member] = style;
            }
        }
        StringBuilder text = new();
        foreach (KeyValuePair<string, string> member in members)
        {
            text.Append("        public const string ").Append(member.Key).Append(" = \"").Append(member.Value).AppendLine("\";");
        }
        string from = string.Join(", ", files.Select(f => f.Name).Distinct());
        output.AddSource($"{name}.g.cs", SourceText.From($$"""
            // <auto-generated/>
            namespace Passenger
            {
                /// <summary>The style classes {{from}} style, from there (Passenger.Analyzers' StyleClassNames).</summary>
                [global::Passenger.IdentifierHome]
                public static class {{name}}
                {
            {{text}}    }
            }
            """, Encoding.UTF8));
    }

    private sealed class ClassesFile(string name, string file, IReadOnlyList<string> classes, string? problem)
    {
        public string Class { get; } = name;
        public string Name { get; } = file;
        public IReadOnlyList<string> Classes { get; } = classes;
        public string? Problem { get; } = problem;
    }
}
