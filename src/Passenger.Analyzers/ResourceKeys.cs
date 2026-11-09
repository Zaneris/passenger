using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Passenger.Analyzers;

/// <summary>Resource keys as constants, so code names a theme's key as it names any identifier, its home the XAML that gives
/// it: each additional file with KeysClass and KeysPrefix metadata gives that public class in namespace Passenger a const
/// for every x:Key it holds with the prefix, named by the key without it, each further dot an underscore
/// ("Passenger.Editor.Background" is TokenKeys.Editor_Background), marked [IdentifierHome].</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ResourceKeys : IIncrementalGenerator
{
    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ConstantsFile> files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((pair, cancel) => Read(pair.Left, pair.Right.GetOptions(pair.Left), cancel))
            .Where(file => file is not null)!;
        context.RegisterSourceOutput(files.Collect(), (output, all) =>
        {
            foreach (IGrouping<string, ConstantsFile> group in all.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                ConstantsClass.Write(output, group.Key, [.. group], Rules.KeysUnread, sorted: false,
                    from => $"The resource keys {from} give, from there (Passenger.Analyzers' ResourceKeys).");
            }
        });
    }

    private static ConstantsFile? Read(AdditionalText file, AnalyzerConfigOptions options, CancellationToken cancel)
    {
        if (!options.TryGetValue("build_metadata.AdditionalFiles.KeysClass", out string? name) || name.Length == 0
            || !options.TryGetValue("build_metadata.AdditionalFiles.KeysPrefix", out string? prefix) || prefix.Length == 0)
        {
            return null;
        }
        List<(string Member, string Value)> keys = [];
        string? problem = null;
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(file.GetText(cancel)?.ToString() ?? string.Empty));
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.GetAttribute("Key", Xaml) is { } key
                    && key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keys.Add((key.Substring(prefix.Length).Replace('.', '_'), key));
                }
            }
        }
        catch (XmlException e)
        {
            problem = e.Message;
        }
        return new ConstantsFile(name, Path.GetFileName(file.Path), keys, problem);
    }
}
