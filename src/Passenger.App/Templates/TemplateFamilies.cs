namespace Passenger.App.Templates;

/// <summary>A row of New Project's list: related templates as one, each a Type of it (Unit Test's xUnit, NUnit and MSTest,
/// Web's Web App, Web API and Blazor Web App); a family of one template has no Type to choose.</summary>
public sealed record TemplateFamily(string Name, IReadOnlyList<ProjectTemplate> Types)
{
    /// <summary>Whether none of it is installed yet: every Type comes with a package dotnet new has not got.</summary>
    public bool NotInstalled => Types.All(t => t.Pack is not null);

    /// <summary>Whether a search finds the family: every word in its name, or in a Type's name, Type name or short names
    /// (with the family's name), those Types being the ones it names; a search the family's name answers names none.</summary>
    public bool Matches(IReadOnlyList<string> words, out IReadOnlyList<ProjectTemplate> types)
    {
        types = [];
        if (words.All(w => Has(Name, w)))
        {
            return true;
        }
        types = [.. Types.Where(t => words.All(w => Has(Name, w) || Has(t.Name, w) || Has(t.TypeName, w) || t.ShortNames.Any(s => Has(s, w))))];
        return types.Count > 0;

        static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The family as New Project lists it: its project templates alone, since only New Solution makes a solution;
    /// null when it has none.</summary>
    public TemplateFamily? Projects() =>
        Types.All(t => !t.IsSolution) ? this : Types.Any(t => !t.IsSolution) ? this with { Types = [.. Types.Where(t => !t.IsSolution)] } : null;
}

/// <summary>How templates become families, from their own metadata: a template group (a groupIdentity's templates, its
/// languages the Language option) is a Type, which joins the product its classifications name first
/// (<see cref="Products"/>), else the first of <see cref="Purposes"/> they name, whoever made it (TUnit's is a Unit
/// Test). Those none takes go by their short name's family (avalonia.app and avalonia.mvvm are Avalonia's), named by the
/// words their names begin with, or stand alone. The project types come in their order, then the rest by name.</summary>
public static class TemplateFamilies
{
    /// <summary>The project types that are products, which take a template before what it is for (an Aspire test project
    /// is Aspire's); of two a template names, the one it names first (MAUI's Aspire service defaults is MAUI's, the AI chat
    /// app tagged Aspire is AI's). The leading short names give the one order metadata cannot (an Aspire app's starter
    /// before its single projects).</summary>
    private static readonly Kind[] Products =
    [
        new("AI Extensions", c => c.Contains("AI"), [], []),
        new("Aspire", c => c.Contains("Aspire") || c.Contains(".NET Aspire"), [".NET Aspire ", "Aspire "], ["aspire-starter", "aspire"]),
        new("Azure Functions", c => c.Contains("Azure Functions"), [], []),
        new("MAUI", c => c.Contains("MAUI"), [".NET MAUI for ", ".NET MAUI "], []),
    ];

    /// <summary>The project types for what a template is for, in the order one no product takes is tried against them (a
    /// test tagged Desktop and Web is a test). Web leaves workers and cloud templates to the others; Desktop is WPF and
    /// Windows Forms, not all tagged Desktop (Avalonia's). The leading short names give the one order metadata cannot (the
    /// plain template first, the test frameworks' order).</summary>
    private static readonly Kind[] Purposes =
    [
        new("Unit Test", c => c.Contains("Test"), [" Test Project"], ["xunit", "nunit", "mstest"]),
        new("Desktop", c => c.Contains("WPF") || c.Contains("WinForms"), [], []),
        new("Web", c => c.Contains("Web") && !c.Contains("Worker") && !c.Contains("Cloud"), ["ASP.NET Core "], ["webapp", "mvc", "webapi", "web"]),
        new("Class Library", c => c.Contains("Library"), [" Class Library"], ["classlib"]),
        new("Services", c => c.Contains("Worker"), [], []),
        new("Console", c => c.Contains("Console"), [], []),
    ];

    private static readonly string[] RiderOrder = ["Console", "Class Library", "Desktop", "Web", "AI Extensions", "Services", "Unit Test", "Aspire", "Azure Functions", "MAUI"];

    /// <summary>The families of these templates, in the project types' order, each one's Types in its order.</summary>
    public static IReadOnlyList<TemplateFamily> Of(IEnumerable<ProjectTemplate> templates)
    {
        Dictionary<Kind, List<ProjectTemplate>> kinds = [];
        List<ProjectTemplate> rest = [];
        foreach (ProjectTemplate template in templates)
        {
            if (KindOf(template) is { } kind)
            {
                (kinds.TryGetValue(kind, out List<ProjectTemplate>? members) ? members : kinds[kind] = []).Add(template);
            }
            else
            {
                rest.Add(template);
            }
        }
        List<TemplateFamily> families = [.. kinds.OrderBy(k => Array.IndexOf(RiderOrder, k.Key.Name))
            .Select(k => Family(k.Key.Name, k.Value, k.Key.Cut, k.Key.Lead))];
        List<TemplateFamily> others = [];
        foreach (IGrouping<string, ProjectTemplate> group in rest.GroupBy(t => ShortFamily(t.ShortName), StringComparer.OrdinalIgnoreCase))
        {
            string shared = group.Count() > 1 ? Shared([.. group.Select(t => t.Name)]) : string.Empty;
            if (shared.Length == 0)
            {
                others.AddRange(group.Select(t => Family(t.Name, [t], [], [])));
            }
            else
            {
                others.Add(Family(shared, [.. group], [$"{shared} .NET ", $"{shared} "], []));
            }
        }
        return [.. families, .. others.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The project type a template joins: the product one of its classifications names first, else the first
    /// purpose they name; null for none.</summary>
    private static Kind? KindOf(ProjectTemplate template)
    {
        foreach (string classification in template.Classifications)
        {
            HashSet<string> named = new([classification], StringComparer.OrdinalIgnoreCase);
            if (Products.FirstOrDefault(k => k.Takes(named)) is { } product)
            {
                return product;
            }
        }
        HashSet<string> classifications = new(template.Classifications, StringComparer.OrdinalIgnoreCase);
        return Purposes.FirstOrDefault(k => k.Takes(classifications));
    }

    /// <summary>The families with one more in its place: among those of no project type, by name.</summary>
    public static IReadOnlyList<TemplateFamily> With(IReadOnlyList<TemplateFamily> families, TemplateFamily added)
    {
        int at = families.Count;
        for (int i = 0; i < families.Count; i++)
        {
            if (!RiderOrder.Contains(families[i].Name) && StringComparer.OrdinalIgnoreCase.Compare(families[i].Name, added.Name) > 0)
            {
                at = i;
                break;
            }
        }
        return [.. families.Take(at), added, .. families.Skip(at)];
    }

    /// <summary>A family, each Type named and in its place, its first the one a family opens on: those installed before
    /// those whose package is not, then the leading short names, then projects before the solutions made around them
    /// (Avalonia's App and MVVM App, then its Cross Platform Application), then by Type name.</summary>
    private static TemplateFamily Family(string name, List<ProjectTemplate> members, string[] cut, string[] lead)
    {
        List<ProjectTemplate> types = [.. members.Select(t => t with { Family = name, TypeName = TypeName(t.Name, cut) })
            .OrderBy(t => t.Pack is not null).ThenBy(t => Lead(t, lead)).ThenBy(t => t.IsSolution).ThenBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)];
        return new TemplateFamily(name, types);
    }

    private static int Lead(ProjectTemplate template, string[] lead) =>
        Array.FindIndex(lead, l => template.ShortNames.Contains(l, StringComparer.OrdinalIgnoreCase)) is var at and >= 0 ? at : lead.Length;

    /// <summary>A template's name without the words its family's name says already, at its start or end; its whole name
    /// when nothing would be left.</summary>
    private static string TypeName(string name, string[] cut)
    {
        string type = name;
        foreach (string words in cut)
        {
            if (type.StartsWith(words, StringComparison.OrdinalIgnoreCase))
            {
                type = type[words.Length..];
            }
            else if (type.EndsWith(words, StringComparison.OrdinalIgnoreCase))
            {
                type = type[..^words.Length];
            }
        }
        type = type.Trim();
        return type.Length == 0 ? name : type;
    }

    /// <summary>A short name's family: its part before the first dot or dash (avalonia for avalonia.mvvm).</summary>
    private static string ShortFamily(string shortName) => shortName.Split('.', '-')[0];

    /// <summary>The words the names all begin with ("Avalonia" of "Avalonia .NET App" and "Avalonia Cross Platform
    /// Application"), none when they share none.</summary>
    private static string Shared(IReadOnlyList<string> names)
    {
        string[][] words = [.. names.Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))];
        int count = 0;
        while (words.All(w => w.Length > count + 1) && words.All(w => string.Equals(w[count], words[0][count], StringComparison.OrdinalIgnoreCase)))
        {
            count++;
        }
        return string.Join(' ', words[0].Take(count));
    }

    /// <summary>A project type: its name, the classifications that make a template one, the words cut from a template's
    /// name for its Type's, and the short names whose templates lead its Types.</summary>
    private sealed record Kind(string Name, Func<IReadOnlySet<string>, bool> Takes, string[] Cut, string[] Lead);
}
