using System.Xml;
using Microsoft.CodeAnalysis;

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
        IncrementalValuesProvider<ConstantsFile> files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, cancel) => pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.ClassesClass", out string? name)
                && name.Length > 0 ? Read(pair.Left, name, cancel) : null)
            .Where(file => file is not null)!;
        context.RegisterSourceOutput(files.Collect(), (output, all) =>
        {
            foreach (IGrouping<string, ConstantsFile> group in all.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                ConstantsClass.Write(output, group.Key, [.. group], Rules.ClassesUnread, sorted: true,
                    from => $"The style classes {from} style, from there (Passenger.Analyzers' StyleClassNames).");
            }
        });
    }

    private static ConstantsFile Read(AdditionalText file, string name, CancellationToken cancel)
    {
        List<(string Member, string Value)> classes = [];
        string? problem = null;
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(file.GetText(cancel)?.ToString() ?? string.Empty));
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Style" && reader.GetAttribute("Selector") is { } selector)
                {
                    classes.AddRange(ClassesOf(selector).Select(style => (char.ToUpperInvariant(style[0]) + style.Substring(1).Replace('-', '_'), style)));
                }
            }
        }
        catch (XmlException e)
        {
            problem = e.Message;
        }
        return new ConstantsFile(name, Path.GetFileName(file.Path), classes, problem);
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
}
